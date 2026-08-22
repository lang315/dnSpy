<#
.SYNOPSIS
    Runs the Tier 2 dnSpy.MCP integration tests against a real dnSpy instance.

.DESCRIPTION
    Builds the fixture debuggee, launches dnSpy with the MCP server on a free port behind a
    generated bearer token, waits for the endpoint, runs the integration suite, then tears
    everything down.

    dnSpy is a WPF app, so this needs an interactive desktop session — it will not work from a
    service or a headless CI agent. Point -DnSpy at a built dnSpy.exe.

.PARAMETER DnSpy
    Path to dnSpy.exe. Defaults to the repo's Release output.

.PARAMETER KeepRunning
    Leave dnSpy running after the tests, for investigating a failure by hand.

.EXAMPLE
    .\tests\run-integration.ps1 -DnSpy C:\dnSpy\dnSpy.exe
#>
[CmdletBinding()]
param(
    [string]$DnSpy,
    [switch]$KeepRunning,
    [string]$Filter   # optional dotnet-test --filter (e.g. 'FullyQualifiedName~StaticIntegrationTests')
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$started = @()

function Write-Step($message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Resolve-DnSpy {
    if ($DnSpy) {
        if (-not (Test-Path $DnSpy)) { throw "dnSpy.exe not found at $DnSpy" }
        return (Resolve-Path $DnSpy).Path
    }
    # Wrap the filtered pipeline in @() so a single surviving candidate stays an array. Without it,
    # a lone match collapses to a scalar string and $candidates[0] returns its first *character* ("C"),
    # which then fails Start-Process with "cannot find the file specified".
    $candidates = @(
        @(
            "$repo\dnSpy\dnSpy\bin\Release\net10.0-windows\dnSpy.exe",
            "$repo\dnSpy\dnSpy\bin\Release\net48\dnSpy.exe",
            "$repo\dnSpy\dnSpy\bin\Debug\net10.0-windows\dnSpy.exe"
        ) | Where-Object { Test-Path $_ }
    )
    if ($candidates.Count -eq 0) {
        throw "Could not find a built dnSpy.exe. Build the repo first (./build.ps1) or pass -DnSpy."
    }
    return $candidates[0]
}

# A dnSpy this script did not launch is the user's own session, running on their real
# %APPDATA%\dnSpy\dnSpy.xml. The suite clears every breakpoint between tests and dnSpy saves
# breakpoints back on a clean exit, so a run that found that instance first would delete the
# breakpoints they set by hand. Refuse rather than gamble on which instance the port lands on.
function Assert-NoRunningDnSpy {
    $existing = @(Get-Process -Name 'dnSpy', 'dnSpy-x86' -ErrorAction SilentlyContinue)
    if ($existing.Count -eq 0) { return }

    $ids = ($existing | ForEach-Object { "$($_.ProcessName) (PID $($_.Id))" }) -join ', '
    $lines = @(
        "Refusing to start: dnSpy is already running - $ids."
        'This suite clears all breakpoints, and a dnSpy you started yourself uses your real'
        '%APPDATA%\dnSpy\dnSpy.xml, so the run would delete the breakpoints you set by hand.'
        'Close it (including any instance left behind by a previous -KeepRunning) and re-run'
        'this script, which launches its own dnSpy with --settings-file pointing at a temp file.'
    )
    throw ($lines -join [Environment]::NewLine)
}

# A free port keeps repeat and parallel runs from colliding, and proves DNSPY_MCP_PORT is honoured.
function Get-FreePort {
    $probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    return $port
}

function Wait-ForEndpoint($url, $token, $timeoutSec = 90) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        try {
            $headers = @{ Authorization = "Bearer $token" }
            $body = '{"jsonrpc":"2.0","id":1,"method":"ping"}'
            $res = Invoke-WebRequest -Uri $url -Method Post -Body $body -Headers $headers `
                -ContentType 'application/json' -TimeoutSec 5 -UseBasicParsing
            if ($res.StatusCode -eq 200) { return $true }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    return $false
}

# A debuggee left alive holds a file lock on the fixture, and the next run fails in the build step
# with an error that points at MSBuild rather than at the real cause.
#
# Two shapes to catch. Launching dbgtest.exe gives a process named dbgtest; launching dbgtest.dll -
# which is what dbg_start does, and what most of the suite uses - runs it under dotnet.exe, whose
# name says nothing about the fixture. Matching only on the name misses exactly the common case.
function Stop-Leftovers {
    Get-Process -Name 'dbgtest' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -match 'dbgtest' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    foreach ($proc in $started) {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    # Before anything else, and before $started has anything in it: the teardown below must never
    # be in a position to kill an instance this script did not start.
    Assert-NoRunningDnSpy

    $exe = Resolve-DnSpy
    Write-Step "Using dnSpy at $exe"

    Write-Step 'Building the fixture debuggee'
    & dotnet build "$repo\tests\fixture\dbgtest" -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'fixture build failed' }

    $port = Get-FreePort
    $token = [guid]::NewGuid().ToString('N')
    $url = "http://127.0.0.1:$port/mcp"
    $log = Join-Path $env:TEMP 'dnSpy.MCP.log'
    if (Test-Path $log) { Remove-Item $log -Force }

    # Isolate settings. The suite clears all breakpoints between tests, and dnSpy persists
    # breakpoints into %APPDATA%\dnSpy\dnSpy.xml on exit — running against the default profile
    # would delete the breakpoints the user set in their own dnSpy.
    $settings = Join-Path ([IO.Path]::GetTempPath()) "dnSpy.mcptest.$([guid]::NewGuid().ToString('N')).xml"
    $script:settingsFile = $settings

    Write-Step "Launching dnSpy with the MCP server on port $port"
    Write-Host "    settings: $settings (isolated from your dnSpy profile)" -ForegroundColor DarkGray
    $env:DNSPY_MCP_PORT = "$port"
    $env:DNSPY_MCP_TOKEN = $token
    $started += Start-Process -FilePath $exe -ArgumentList '--settings-file', $settings -PassThru

    if (-not (Wait-ForEndpoint $url $token)) {
        if (Test-Path $log) {
            Write-Host '--- dnSpy.MCP.log ---' -ForegroundColor Yellow
            Get-Content $log -Tail 40
        }
        throw "The MCP endpoint at $url never came up. Is dnSpy running on an interactive desktop?"
    }
    Write-Step 'Endpoint is live'

    $env:DNSPY_MCP_TEST_URL = $url
    $env:DNSPY_MCP_TEST_TOKEN = $token
    $env:DNSPY_MCP_TEST_FIXTURE = "$repo\tests\fixture\dbgtest\bin\Debug"
    $env:DNSPY_MCP_TEST_FIXTURE_SRC = "$repo\tests\fixture\dbgtest"

    Write-Step 'Running the integration suite'
    # Emit a TRX so per-test results survive to disk. This suite is not in CI (it needs an interactive
    # desktop), and PowerShell's native-stderr wrapping can mangle the console summary, so the TRX is
    # the reliable record of what passed.
    $trxDir = Join-Path $repo 'TestResults'
    $filterArgs = if ($Filter) { @('--filter', $Filter) } else { @() }
    & dotnet test "$repo\tests\dnSpy.MCP.IntegrationTests" -c Release --nologo `
        --logger 'trx;LogFileName=integration.trx' --results-directory $trxDir @filterArgs
    $testExit = $LASTEXITCODE
    Write-Host "    results: $trxDir\integration.trx" -ForegroundColor DarkGray

    if (Test-Path $log) {
        Write-Step 'Server log'
        Get-Content $log -Tail 20
    }

    if ($testExit -ne 0) { throw "integration tests failed (exit $testExit)" }
    Write-Step 'All integration tests passed'
}
finally {
    if ($KeepRunning) {
        Write-Host 'Leaving dnSpy running (-KeepRunning).' -ForegroundColor Yellow
    }
    else {
        Write-Step 'Tearing down'
        Stop-Leftovers
        if ($script:settingsFile -and (Test-Path $script:settingsFile)) {
            Remove-Item $script:settingsFile -Force -ErrorAction SilentlyContinue
        }
    }
}
