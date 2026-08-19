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
    [switch]$KeepRunning
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

# A debuggee left alive holds a file lock on the fixture and breaks the next run.
function Stop-Leftovers {
    Get-Process -Name 'dbgtest' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    foreach ($proc in $started) {
        if ($proc -and -not $proc.HasExited) {
            Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
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
    & dotnet test "$repo\tests\dnSpy.MCP.IntegrationTests" -c Release --nologo
    $testExit = $LASTEXITCODE

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
