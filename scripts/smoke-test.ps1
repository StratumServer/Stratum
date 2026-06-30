<#
.SYNOPSIS
Boot-tests the Stratum server on Windows. Builds Release, starts the server,
monitors progress through startup phases, and verifies it reaches RunGame
without fatal errors. Detects stalls by checking log growth rather than a
hard timeout.

.PARAMETER Patience
Seconds without log output before declaring a stall. Default: 60.

.PARAMETER Port
Server port. Default: random ephemeral.

.PARAMETER DataPath
Server dataPath. Default: temp dir, cleaned on exit.

.EXAMPLE
.\scripts\smoke-test.ps1
.\scripts\smoke-test.ps1 -Patience 90
#>

[CmdletBinding()]
param(
    [int]$Patience = 60,
    [int]$Port = 0,
    [string]$DataPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$serverDir = Join-Path $repoRoot 'StratumServer\bin\Release\net10.0'
$serverBin = Join-Path $serverDir 'VintagestoryServer.exe'
if (-not (Test-Path $serverBin)) {
    $serverBin = Join-Path $serverDir 'StratumServer.exe'
}

Push-Location $repoRoot
try {
    # Build if binary is missing.
    if (-not (Test-Path $serverBin)) {
        Write-Host "Building Release..."
        dotnet build VintageStory.slnx -c Release --verbosity quiet
    }

    if (-not (Test-Path $serverBin)) {
        Write-Error "Server binary not found: $serverBin"
        exit 1
    }

    # Data path.
    $ownData = $false
    if (-not $DataPath) {
        $DataPath = Join-Path ([System.IO.Path]::GetTempPath()) "stratum-smoke-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        $ownData = $true
    }
    New-Item -ItemType Directory -Force -Path $DataPath | Out-Null

    # Ephemeral port.
    if ($Port -eq 0) {
        $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
        $listener.Start()
        $Port = $listener.LocalEndpoint.Port
        $listener.Stop()
    }

    Write-Host "Smoke test: port=$Port patience=${Patience}s data=$DataPath"

    $logFile = Join-Path $DataPath 'smoke-test.log'

    # Start server.
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $serverBin
    $psi.Arguments = "--dataPath `"$DataPath`" --port $Port"
    $psi.RedirectStandardOutput = $false
    $psi.RedirectStandardError = $false
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true

    # Redirect output to file at the OS level to avoid .NET stream deadlocks.
    # PowerShell's Start-Process -RedirectStandardOutput writes directly to disk,
    # so neither stdout nor stderr can block the process.
    $proc = Start-Process -FilePath $serverBin -ArgumentList "--dataPath `"$DataPath`" --port $Port" `
        -RedirectStandardOutput $logFile -RedirectStandardError (Join-Path $DataPath 'smoke-test-err.log') `
        -NoNewWindow -PassThru

    $lastLineCount = 0
    $sinceProgress = 0
    $lastPhase = '(starting)'
    $reachedRunGame = $false
    $hasFatal = $false

    try {
        while (-not $proc.HasExited) {
            Start-Sleep -Seconds 2

            $content = if (Test-Path $logFile) { Get-Content $logFile -Raw -ErrorAction SilentlyContinue } else { '' }

            # Reached RunGame?
            if ($content -match 'Entering runphase RunGame') {
                Start-Sleep -Seconds 5
                $reachedRunGame = $true
                break
            }

            # Fatal error?
            if ($content -match '\[Server Fatal\]') {
                break
            }

            # Progress detection.
            $currentLines = if (Test-Path $logFile) { (Get-Content $logFile).Count } else { 0 }
            if ($currentLines -gt $lastLineCount) {
                $lastLineCount = $currentLines
                $sinceProgress = 0
                $phaseMatch = [regex]::Matches($content, 'Entering runphase (\S+)')
                if ($phaseMatch.Count -gt 0) {
                    $lastPhase = $phaseMatch[$phaseMatch.Count - 1].Groups[1].Value
                }
            } else {
                $sinceProgress += 2
            }

            if ($sinceProgress -ge $Patience) {
                Write-Warning "STALL: no log output for ${Patience}s (last phase: $lastPhase)"
                break
            }
        }
    } finally {
        if (-not $proc.HasExited) {
            $proc.Kill()
            $proc.WaitForExit(5000)
        }
    }

    # Read final log.
    $finalLog = if (Test-Path $logFile) { Get-Content $logFile -Raw -ErrorAction SilentlyContinue } else { '' }

    if (-not $reachedRunGame -and $finalLog -match 'Entering runphase RunGame') {
        $reachedRunGame = $true
    }
    if ($finalLog -match 'Fatal|Unhandled exception') {
        $hasFatal = $true
    }

    if ($reachedRunGame -and -not $hasFatal) {
        Write-Host "PASS: server reached RunGame, no fatal errors. (last phase: $lastPhase)"
        exit 0
    }

    Write-Error "FAIL:"
    if (-not $reachedRunGame) {
        Write-Error "  Server did not reach RunGame (last phase: $lastPhase)."
    }
    if ($hasFatal) {
        Write-Error "  Fatal errors found in log."
    }
    Write-Error "  Log: $logFile"
    exit 1

} finally {
    Pop-Location
    if ($ownData -and (Test-Path $DataPath)) {
        Remove-Item -Recurse -Force $DataPath -ErrorAction SilentlyContinue
    }
}
