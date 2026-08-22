<#
.SYNOPSIS
    Measures how often dnSpy dies (0xC0000005) when a paused debuggee is terminated, under four
    different teardown strategies.

.DESCRIPTION
    The crash we are chasing has no dnSpy.MCP frame in it. dnSpy's own Locals window re-evaluates
    when the call stack changes; that path reads the debuggee's PE image straight out of the
    debuggee's memory (dnlib PEImage over a raw IntPtr). Terminating a *paused* process lets that
    refresh read memory that has just been freed, and dnSpy takes an access violation.

    The integration suite used to hit this. It stopped hitting it after two changes landed in
    Dbg.Reset() at the same time: (a) resume the process before stopping it, and (b) sleep ~750 ms
    afterwards. Nobody measured which one mattered. That matters, because adopting (a) inside the
    dbg_stop tool has a real semantic cost: the debuggee runs on for a moment after the user asked
    for it to stop. This harness measures the two changes separately so the decision is made on
    data instead of on a guess.

    Four variants, N iterations each:
      StopImmediately  stop while paused, no delay          (the suspected trigger)
      DelayOnly        stop while paused, then wait          (change (b) alone)
      ResumeOnly       resume, then stop, no extra wait      (change (a) alone)
      ResumeAndDelay   resume, then stop, then wait          (today's suite behaviour)

    One iteration = launch the fixture under the debugger, break at DbgTest.Program.Add, apply the
    variant's teardown, then confirm dnSpy is still alive. dnSpy *will* die during this run; that is
    the point. A death is recorded, dnSpy is relaunched, and the remaining iterations continue.

    The deliverable is the summary table at the end: crashes/N and crash rate per variant. See
    "READING THE RESULTS" at the bottom of this file for the confounds that can make the table lie.

    dnSpy is a WPF app, so this needs an interactive desktop session. It will not work from a
    service or a headless CI agent.

.PARAMETER Iterations
    Iterations per variant. Default 30. With 30 samples a 0/30 result still has a 95% upper bound
    near 11%, so treat "0 crashes" as "rare", not as "fixed" (the report prints the interval).

.PARAMETER DnSpy
    Path to dnSpy.exe. Defaults to the repo's Release output.

.PARAMETER Variants
    Subset of variants to measure. Default: all four.

.PARAMETER DelayMs
    The "delay" in DelayOnly / ResumeAndDelay. Default 750, matching Dbg.Reset().

.PARAMETER ResumeSettleMs
    Pause between dbg_continue and dbg_stop in the Resume* variants. Default 0, matching the suite.
    Raise it if you want to test whether the resume needs time to take effect to be protective.

.PARAMETER Grouped
    Run all N iterations of a variant before moving to the next. Default is round-robin
    interleaving, which stops per-variant results from absorbing time-ordering effects (notably
    "first iteration after a relaunch").

.PARAMETER SkipBuild
    Skip the fixture build. Only safe if tests\fixture\dbgtest is already built for Debug.

.EXAMPLE
    .\tests\bisect-stop-crash.ps1

.EXAMPLE
    .\tests\bisect-stop-crash.ps1 -Iterations 50 -Variants StopImmediately,ResumeOnly
#>
[CmdletBinding()]
param(
    [int]$Iterations = 30,
    [string]$DnSpy,
    [ValidateSet('StopImmediately', 'DelayOnly', 'ResumeOnly', 'ResumeAndDelay')]
    [string[]]$Variants = @('StopImmediately', 'DelayOnly', 'ResumeOnly', 'ResumeAndDelay'),
    [int]$DelayMs = 750,
    [int]$ResumeSettleMs = 0,
    [int]$BreakTimeoutMs = 15000,
    # bp_add_method resolves a module by full path, or by file name only if dnSpy already has it
    # open. A fresh instance has nothing open, so a bare name quietly sets no breakpoint and every
    # iteration reports no-break — a null result that looks like a measurement. Empty means "use the
    # built fixture's full path".
    [string]$Module = '',
    [string]$Method = 'DbgTest.Program.Add',
    [switch]$Grouped,
    [switch]$SkipBuild,
    [string]$OutCsv
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

$script:rpcId = 0
$script:proc = $null              # the dnSpy process WE launched, never anybody else's
$script:url = $null
$script:token = $null
$script:port = $null
$script:settingsFiles = @()       # every isolated profile we made, deleted in finally
$script:records = @()             # one row per iteration
$script:launchCount = 0

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Note($message) { Write-Host "    $message" -ForegroundColor DarkGray }

function Resolve-DnSpy {
    if ($DnSpy) {
        if (-not (Test-Path $DnSpy)) { throw "dnSpy.exe not found at $DnSpy" }
        return (Resolve-Path $DnSpy).Path
    }
    $candidates = @(
        "$repo\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.exe",
        "$repo\dnSpy\dnSpy\bin\Release\net48\dnSpy.exe",
        "$repo\dnSpy\dnSpy\bin\Debug\net10.0-windows\dnSpy.exe"
    ) | Where-Object { Test-Path $_ }
    if (-not $candidates) {
        throw "Could not find a built dnSpy.exe. Build the repo first (./build.ps1) or pass -DnSpy."
    }
    return $candidates[0]
}

# A free port keeps repeat and parallel runs from colliding.
function Get-FreePort {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    return $port
}

<#
    One JSON-RPC tools/call. Never throws: a transport failure here is a *result* (it usually means
    dnSpy just died mid-call), so it is returned as Ok=$false and the caller decides.
#>
function Invoke-Mcp {
    param(
        [Parameter(Mandatory)][string]$Tool,
        [hashtable]$Arguments = @{},
        [int]$TimeoutSec = 30
    )
    $script:rpcId++
    $payload = @{
        jsonrpc = '2.0'
        id      = $script:rpcId
        method  = 'tools/call'
        params  = @{ name = $Tool; arguments = $Arguments }
    } | ConvertTo-Json -Depth 8 -Compress

    try {
        $headers = @{ Authorization = "Bearer $($script:token)" }
        $res = Invoke-WebRequest -Uri $script:url -Method Post -Body $payload -Headers $headers `
            -ContentType 'application/json' -TimeoutSec $TimeoutSec -UseBasicParsing
        $obj = $res.Content | ConvertFrom-Json
        $text = $null
        if ($obj.result -and $obj.result.content) { $text = $obj.result.content[0].text }
        $err = $null
        if ($obj.error) { $err = "$($obj.error.message)" }
        return [pscustomobject]@{ Ok = $true; Text = $text; Error = $err; Transport = $null }
    }
    catch {
        # Connection refused / reset / timeout. dnSpy is probably gone; the caller confirms.
        return [pscustomobject]@{ Ok = $false; Text = $null; Error = $null; Transport = $_.Exception.Message }
    }
}

function Wait-ForEndpoint($timeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $headers = @{ Authorization = "Bearer $($script:token)" }
            $body = '{"jsonrpc":"2.0","id":1,"method":"ping"}'
            $res = Invoke-WebRequest -Uri $script:url -Method Post -Body $body -Headers $headers `
                -ContentType 'application/json' -TimeoutSec 5 -UseBasicParsing
            if ($res.StatusCode -eq 200) { return $true }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    return $false
}

<#
    Is OUR dnSpy still up? Deliberately keyed on the PID we launched: the developer running this
    almost certainly has their own dnSpy open, and `Get-Process -Name dnSpy` would happily report
    that one as ours and hide every crash we are trying to count.
#>
function Test-DnSpyAlive {
    if (-not $script:proc) { return $false }
    $live = Get-Process -Id $script:proc.Id -ErrorAction SilentlyContinue
    if (-not $live) { return $false }
    # Same PID could in principle be recycled; check it is still a dnSpy.
    return ($live.ProcessName -eq 'dnSpy')
}

# Exit code of the dead instance, so an access violation can be told apart from a clean exit.
# 0xC0000005 surfaces as -1073741819.
function Get-DnSpyExitCode {
    if (-not $script:proc) { return $null }
    try {
        $script:proc.Refresh()
        if ($script:proc.HasExited) { return $script:proc.ExitCode }
    }
    catch { }
    return $null
}

function Format-ExitCode($code) {
    if ($null -eq $code) { return '(unknown)' }
    $hex = '0x{0:X8}' -f ([uint32]([int64]$code -band 0xFFFFFFFF))
    if ($hex -eq '0xC0000005') { return "$hex ACCESS_VIOLATION" }
    return $hex
}

# A debuggee left alive holds a file lock on the fixture and poisons every later iteration.
function Stop-Leftovers {
    Get-Process -Name 'dbgtest' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Stop-OurDnSpy {
    if ($script:proc) {
        try {
            $script:proc.Refresh()
            if (-not $script:proc.HasExited) {
                Stop-Process -Id $script:proc.Id -Force -ErrorAction SilentlyContinue
            }
        }
        catch { }
    }
    $script:proc = $null
}

<#
    Launch a fresh dnSpy on a fresh port with a fresh, throwaway settings profile.

    --settings-file is MANDATORY here and is not a nicety. This harness clears all breakpoints every
    iteration, and dnSpy persists breakpoints to %APPDATA%\dnSpy\dnSpy.xml. Without an isolated
    profile a run would silently delete the breakpoints in the operator's own dnSpy.
#>
function Start-DnSpyInstance($exe) {
    $script:launchCount++
    $script:port = Get-FreePort
    $script:token = [guid]::NewGuid().ToString('N')
    $script:url = "http://127.0.0.1:$($script:port)/mcp"

    $settings = Join-Path ([IO.Path]::GetTempPath()) "dnSpy.bisect.$([guid]::NewGuid().ToString('N')).xml"
    $script:settingsFiles += $settings

    $env:DNSPY_MCP_PORT = "$($script:port)"
    $env:DNSPY_MCP_TOKEN = $script:token
    $script:proc = Start-Process -FilePath $exe -ArgumentList '--settings-file', $settings -PassThru

    Write-Note "launch #$($script:launchCount): pid $($script:proc.Id), port $($script:port), settings $settings"
    if (-not (Wait-ForEndpoint)) {
        throw "The MCP endpoint at $($script:url) never came up (launch #$($script:launchCount))."
    }
}

# Bring dnSpy back after it died. Used mid-run so a variant that kills dnSpy on iteration 3 still
# completes iterations 4..N.
function Restore-DnSpy($exe) {
    Stop-OurDnSpy
    Stop-Leftovers
    # A crash may leave a WER dialog holding things up; it only ever appears because we just killed
    # dnSpy, so clearing it here is safe and keeps the run unattended.
    Get-Process -Name 'WerFault', 'WerFaultSecure' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Start-DnSpyInstance $exe
}

function Get-DbgStatus {
    $r = Invoke-Mcp -Tool 'dbg_status'
    if (-not $r.Ok -or -not $r.Text) { return $null }
    try { return ($r.Text | ConvertFrom-Json) } catch { return $null }
}

<#
    Wilson score interval. On 30 samples the raw ratio is a bad summary on its own: 0/30 and 1/30
    overlap heavily, and reading "0%" as "safe" is exactly the mistake this harness exists to
    prevent. Printed alongside every rate.
#>
function Get-WilsonInterval($k, $n) {
    if ($n -le 0) { return , @(0.0, 0.0) }
    $z = 1.96
    $p = $k / $n
    $den = 1 + ($z * $z / $n)
    $centre = ($p + ($z * $z / (2 * $n))) / $den
    $margin = ($z * [math]::Sqrt(($p * (1 - $p) / $n) + ($z * $z / (4 * $n * $n)))) / $den
    $lo = [math]::Max(0.0, $centre - $margin)
    $hi = [math]::Min(1.0, $centre + $margin)
    return , @($lo, $hi)
}

<#
    One iteration: arm the breakpoint, start the fixture, break, tear down the variant's way, then
    decide whether dnSpy survived.

    Returns a record. Crashed=$true means our dnSpy PID is gone. Unresponsive=$true means the
    process is alive but the endpoint stopped answering — a different failure (deadlock in the same
    Locals refresh path is a plausible cause), counted separately so it cannot be mistaken for a
    crash or quietly swallowed as a success.
#>
function Invoke-Iteration($variant, $index, $fixture) {
    $record = [pscustomobject]@{
        Index        = $index
        Variant      = $variant
        Broke        = $false
        Crashed      = $false
        Unresponsive = $false
        ExitCode     = $null
        StoppedWhile = ''        # 'paused' or 'running' - what the debuggee was doing at dbg_stop
        DurationMs   = 0
        Note         = ''
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    Stop-Leftovers

    # Fresh breakpoint state. bp_remove all also undoes anything a previous crashed run left behind.
    $null = Invoke-Mcp -Tool 'bp_remove' -Arguments @{ all = $true }

    # If a previous iteration's teardown half-failed we could still be attached; clear that first so
    # this iteration measures its own variant and not the leftovers of the last one.
    $pre = Get-DbgStatus
    if ($pre -and $pre.isDebugging) {
        $null = Invoke-Mcp -Tool 'dbg_stop'
        Start-Sleep -Milliseconds 300
    }

    $bp = Invoke-Mcp -Tool 'bp_add_method' -Arguments @{ module = $Module; method = $Method }
    if (-not $bp.Ok) {
        $record.Note = 'dnSpy died before the breakpoint was set'
    }
    elseif ($bp.Error) {
        $record.Note = "bp_add_method: $($bp.Error)"
    }

    if ($bp.Ok) {
        $start = Invoke-Mcp -Tool 'dbg_start' -Arguments @{ path = $fixture }
        if ($start.Ok) {
            $brk = Invoke-Mcp -Tool 'dbg_wait_for_break' `
                -Arguments @{ timeout_ms = $BreakTimeoutMs } `
                -TimeoutSec ([int]($BreakTimeoutMs / 1000) + 20)
            if ($brk.Ok -and -not $brk.Error) {
                $st = Get-DbgStatus
                # isRunning=$false while attached is the paused-at-breakpoint state the crash needs.
                if ($st -and $st.isDebugging -and -not $st.isRunning) {
                    $record.Broke = $true
                }
                elseif ($st -and $st.isDebugging) {
                    $record.Note = 'attached but not paused when the break was reported'
                }
                else {
                    $record.Note = 'not debugging after dbg_wait_for_break'
                }
            }
            elseif (-not $brk.Ok) {
                $record.Note = 'dnSpy died during dbg_wait_for_break'
            }
            else {
                $record.Note = "dbg_wait_for_break: $($brk.Error)"
            }
        }
        else {
            $record.Note = 'dnSpy died during dbg_start'
        }
    }

    # ---- the teardown under test -------------------------------------------------------------
    # Everything above is scaffolding; these few lines are the entire experiment.
    if ($record.Broke) {
        switch ($variant) {
            'StopImmediately' {
                $record.StoppedWhile = 'paused'
                $null = Invoke-Mcp -Tool 'dbg_stop'
            }
            'DelayOnly' {
                $record.StoppedWhile = 'paused'
                $null = Invoke-Mcp -Tool 'dbg_stop'
                Start-Sleep -Milliseconds $DelayMs
            }
            'ResumeOnly' {
                $null = Invoke-Mcp -Tool 'dbg_continue'
                if ($ResumeSettleMs -gt 0) { Start-Sleep -Milliseconds $ResumeSettleMs }
                $record.StoppedWhile = 'running'
                $null = Invoke-Mcp -Tool 'dbg_stop'
            }
            'ResumeAndDelay' {
                $null = Invoke-Mcp -Tool 'dbg_continue'
                if ($ResumeSettleMs -gt 0) { Start-Sleep -Milliseconds $ResumeSettleMs }
                $record.StoppedWhile = 'running'
                $null = Invoke-Mcp -Tool 'dbg_stop'
                Start-Sleep -Milliseconds $DelayMs
            }
        }
    }
    else {
        # Never got to a clean paused state, so the variant was not really exercised. Still stop, so
        # the next iteration starts clean; the record is flagged and excluded from the denominator.
        $null = Invoke-Mcp -Tool 'dbg_stop'
    }
    # ------------------------------------------------------------------------------------------

    # The AV happens on dnSpy's UI thread a beat after the process dies, so give it room to fall
    # over before declaring it healthy. Without this settle we would under-count crashes.
    Start-Sleep -Milliseconds 750

    if (-not (Test-DnSpyAlive)) {
        $record.Crashed = $true
        $record.ExitCode = Get-DnSpyExitCode
        if (-not $record.Note) { $record.Note = 'dnSpy exited' }
    }
    else {
        # Alive is not the same as well: confirm it still serves requests.
        $probe = Invoke-Mcp -Tool 'dbg_status' -TimeoutSec 20
        if (-not $probe.Ok) {
            if (-not (Test-DnSpyAlive)) {
                $record.Crashed = $true
                $record.ExitCode = Get-DnSpyExitCode
                if (-not $record.Note) { $record.Note = 'dnSpy exited during the health probe' }
            }
            else {
                $record.Unresponsive = $true
                if (-not $record.Note) { $record.Note = 'alive but the endpoint stopped answering' }
            }
        }
    }

    Stop-Leftovers
    $sw.Stop()
    $record.DurationMs = [int]$sw.ElapsedMilliseconds
    return $record
}

function Write-Report {
    Write-Host ''
    Write-Host '================ teardown strategy vs. dnSpy crash rate ================' -ForegroundColor Cyan

    if (-not $script:records -or $script:records.Count -eq 0) {
        Write-Host 'No iterations completed.' -ForegroundColor Yellow
        return
    }

    $rows = foreach ($v in $Variants) {
        $all = @($script:records | Where-Object { $_.Variant -eq $v })
        if ($all.Count -eq 0) { continue }
        # Denominator = iterations that actually reached a paused breakpoint, i.e. that really
        # exercised the variant. Iterations that never broke are reported separately, not counted
        # as clean passes.
        $valid = @($all | Where-Object { $_.Broke })
        $crashes = @($valid | Where-Object { $_.Crashed }).Count
        $av = @($valid | Where-Object { $_.Crashed -and $_.ExitCode -eq -1073741819 }).Count
        $hung = @($valid | Where-Object { $_.Unresponsive }).Count
        $noBreak = $all.Count - $valid.Count
        $n = $valid.Count
        $ci = Get-WilsonInterval $crashes $n
        $rate = 0.0
        if ($n -gt 0) { $rate = $crashes / $n }

        [pscustomobject]@{
            Variant   = $v
            Crashes   = "$crashes/$n"
            'Rate %'  = ('{0,6:N1}' -f ($rate * 100))
            '95% CI'  = ('{0:N1}-{1:N1}%' -f ($ci[0] * 100), ($ci[1] * 100))
            AV        = $av
            Hung      = $hung
            NoBreak   = $noBreak
        }
    }

    $rows | Format-Table -AutoSize | Out-String | Write-Host

    Write-Host 'Crashes   = our dnSpy PID gone / iterations that reached a paused breakpoint' -ForegroundColor DarkGray
    Write-Host 'AV        = of those, exit code 0xC0000005 (the access violation we are chasing)' -ForegroundColor DarkGray
    Write-Host 'Hung      = process alive but the MCP endpoint stopped answering' -ForegroundColor DarkGray
    Write-Host 'NoBreak   = iterations that never reached a paused breakpoint; excluded from the rate' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host 'How to read it:' -ForegroundColor Cyan
    Write-Host '  StopImmediately vs DelayOnly    isolates the 750 ms sleep.' -ForegroundColor DarkGray
    Write-Host '  StopImmediately vs ResumeOnly   isolates the resume-before-stop.' -ForegroundColor DarkGray
    Write-Host '  Only adopt resume-before-stop in dbg_stop if ResumeOnly is clearly below' -ForegroundColor DarkGray
    Write-Host '  StopImmediately AND DelayOnly is not. A resume makes the debuggee run on after the' -ForegroundColor DarkGray
    Write-Host '  user asked for a stop, so it needs evidence, not a tie.' -ForegroundColor DarkGray
    Write-Host '  Overlapping CIs at N=30 mean "not measured yet", not "the same".' -ForegroundColor DarkGray
    Write-Host ''
    Write-Host "dnSpy launches during this run: $($script:launchCount) (1 initial + 1 per recovery)" -ForegroundColor DarkGray

    if ($script:csvPath) {
        try {
            $script:records | Export-Csv -Path $script:csvPath -NoTypeInformation -Encoding UTF8
            Write-Host "Per-iteration records: $($script:csvPath)" -ForegroundColor DarkGray
        }
        catch {
            Write-Host "Could not write $($script:csvPath): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

# ---------------------------------------------------------------------------------------------

try {
    $exe = Resolve-DnSpy
    Write-Step "Using dnSpy at $exe"

    $fixture = "$repo\tests\fixture\dbgtest\bin\Debug\net8.0\dbgtest.dll"
    if (-not $SkipBuild) {
        Write-Step 'Building the fixture debuggee'
        & dotnet build "$repo\tests\fixture\dbgtest" -c Debug --nologo -v q
        if ($LASTEXITCODE -ne 0) { throw 'fixture build failed' }
    }
    if (-not (Test-Path $fixture)) { throw "fixture not found at $fixture (build it, or drop -SkipBuild)" }
    $fixture = (Resolve-Path $fixture).Path
    if ([string]::IsNullOrWhiteSpace($Module)) { $Module = $fixture }

    if (-not $OutCsv) {
        $OutCsv = Join-Path ([IO.Path]::GetTempPath()) "dnSpy.bisect.$(Get-Date -Format yyyyMMdd-HHmmss).csv"
    }
    $script:csvPath = $OutCsv

    # Build the schedule up front. Round-robin by default: if a variant were run as one solid block,
    # any drift over the run (thermal, background load, and especially "the iteration right after a
    # relaunch behaves differently") would land entirely on one variant and read as a real effect.
    $plan = @()
    if ($Grouped) {
        foreach ($v in $Variants) { for ($i = 1; $i -le $Iterations; $i++) { $plan += , @($v, $i) } }
    }
    else {
        for ($i = 1; $i -le $Iterations; $i++) { foreach ($v in $Variants) { $plan += , @($v, $i) } }
    }

    Write-Step "Plan: $($Variants.Count) variants x $Iterations iterations = $($plan.Count) runs"
    Write-Note "delay=${DelayMs}ms  resume-settle=${ResumeSettleMs}ms  break timeout=${BreakTimeoutMs}ms"
    Write-Note "order: $(if ($Grouped) { 'grouped by variant' } else { 'round-robin interleaved' })"
    Write-Host ''
    Write-Host 'IMPORTANT: when the first break happens, look at the dnSpy window and confirm the' -ForegroundColor Yellow
    Write-Host 'Locals window is visible. The crash lives in the Locals refresh that runs on a call' -ForegroundColor Yellow
    Write-Host 'stack change. With Locals closed nothing reads the debuggee PE image and every' -ForegroundColor Yellow
    Write-Host 'variant will honestly measure 0% - a null result that means nothing.' -ForegroundColor Yellow
    Write-Host ''

    Stop-Leftovers
    Start-DnSpyInstance $exe
    Write-Step 'Endpoint is live'

    $n = 0
    foreach ($entry in $plan) {
        $variant = $entry[0]
        $iter = $entry[1]
        $n++

        # dnSpy may have died at the tail of the previous iteration; make sure we start healthy so
        # a crash is always attributed to the iteration that caused it.
        if (-not (Test-DnSpyAlive)) {
            Write-Note 'dnSpy is down before this iteration - relaunching'
            Restore-DnSpy $exe
        }

        Write-Host ("[{0,3}/{1}] {2} #{3} " -f $n, $plan.Count, $variant, $iter) -NoNewline
        $record = Invoke-Iteration $variant $iter $fixture
        $script:records += $record

        if ($record.Crashed) {
            Write-Host "CRASH $(Format-ExitCode $record.ExitCode)" -ForegroundColor Red
            Restore-DnSpy $exe
        }
        elseif ($record.Unresponsive) {
            Write-Host 'HUNG' -ForegroundColor Magenta
            # Alive but useless. Restart so later iterations still measure something.
            Restore-DnSpy $exe
        }
        elseif (-not $record.Broke) {
            Write-Host "no-break ($($record.Note))" -ForegroundColor Yellow
        }
        else {
            Write-Host "ok ($($record.DurationMs) ms)" -ForegroundColor DarkGray
        }
    }
}
finally {
    # Report from whatever we collected, even on Ctrl-C or an abort partway through - a partial
    # table is still evidence, and re-running from scratch is expensive.
    try { Write-Report } catch { Write-Host "report failed: $($_.Exception.Message)" -ForegroundColor Yellow }

    Write-Step 'Tearing down'
    Stop-OurDnSpy
    Stop-Leftovers
    foreach ($f in $script:settingsFiles) {
        if ($f -and (Test-Path $f)) { Remove-Item $f -Force -ErrorAction SilentlyContinue }
    }
}

<#
    READING THE RESULTS - confounds that can make the table lie
    ----------------------------------------------------------
    1. Locals window visibility. The crash is in dnSpy's Locals refresh. If the throwaway settings
       profile comes up without the Locals window shown, nothing reads the debuggee's PE image and
       every variant measures 0%. Check this by eye once at the start of a run. This is the single
       most likely way to get a confident, meaningless, all-zeroes table.

    2. The Resume* variants may not be stopping the same thing. After dbg_continue the fixture may
       run to completion on its own, so dbg_stop terminates an already-exiting - or already-exited -
       process. If so, ResumeOnly looks protective only because there was nothing left to race with,
       not because resuming is safe. The StoppedWhile column and the NoBreak count are the hints;
       for a real answer, check whether the debuggee was still alive at dbg_stop for those variants.

    3. Timing, not semantics. Both DelayOnly and ResumeAndDelay put a sleep *after* dbg_stop, which
       cannot change whether the crash occurred - only whether this harness is still watching when
       it does. The 750 ms settle before the health check is there to blunt that, but if crashes
       land later than ~750 ms after the stop they will be attributed to the *next* iteration, and
       under interleaving that means the next *variant*. Long DurationMs values plus crashes with a
       Note of "before this iteration" are the tell.

    4. Sample size. N=30 per variant distinguishes ~0% from ~30%, not 3% from 10%. Read the CI
       column, not the point estimate. If two variants' intervals overlap, the run has not decided
       anything and the honest move is more iterations, not a conclusion.

    5. Fresh process every relaunch. Recovering from a crash gives dnSpy a cold JIT, an empty
       assembly cache and a fresh settings profile. If the crash needs warm state, iterations right
       after a recovery are systematically less likely to crash, which drags every variant's rate
       down. Interleaving spreads that cost evenly instead of concentrating it, but does not remove
       it.

    6. This measures the observable outcome (dnSpy dies), not the mechanism. A variant scoring 0%
       has not been shown to fix the race - only that it did not lose it 30 times running.
#>
