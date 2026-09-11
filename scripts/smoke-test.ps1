<#
.SYNOPSIS
Boot-tests the Stratum server on Windows. Builds Release, starts the server,
monitors progress through startup phases, and verifies it reaches RunGame
without fatal errors. Detects stalls by checking log growth rather than a
hard timeout.

The focused inventory privacy smoke test is run after the Release build. The
interactive console command probe remains Linux/macOS-only.

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

# Prefer StratumServer.exe, Stratum's own launcher, which applies VanillaBootstrap and
# PatchedFileOverlay. A VintagestoryServer.exe in the same directory is the vanilla
# archive's own entry point, copied in as a side effect of the first overlay; preferring
# it would silently boot-test unpatched vanilla code (mirrors scripts/smoke-test.sh).
$serverDir = Join-Path $repoRoot 'StratumServer\bin\Release\net10.0'
function Resolve-ServerBin {
    $stratum = Join-Path $serverDir 'StratumServer.exe'
    if (Test-Path $stratum) { return $stratum }
    $vanilla = Join-Path $serverDir 'VintagestoryServer.exe'
    if (Test-Path $vanilla) { return $vanilla }
    return $null
}
$serverBin = Resolve-ServerBin

Push-Location $repoRoot
try {
    # Build if binary is missing. -p:EmbedPatchedFiles=true is required so StratumServer
    # carries this working tree's patched DLLs; without it PatchedFileOverlay has nothing
    # to overlay and the server silently runs the downloaded vanilla assemblies.
    if (-not $serverBin) {
        Write-Host "Building Release..."
        dotnet build VintageStory.slnx -c Release -p:EmbedPatchedFiles=true --verbosity quiet
        $serverBin = Resolve-ServerBin
    }

    if (-not $serverBin) {
        Write-Error "Server binary not found in $serverDir"
        exit 1
    }

    dotnet run --project tests/InventoryPrivacySmoke/InventoryPrivacySmoke.csproj -c Release
    if ($LASTEXITCODE -ne 0) {
        # Stratum: $ErrorActionPreference = 'Stop' only converts PowerShell errors to
        # terminating ones. It does not inspect a native process's exit code, so a failing
        # check here would otherwise reach the PASS banner at the bottom of this script.
        Write-Error "InventoryPrivacySmoke failed (exit $LASTEXITCODE)"
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

    # Read final logs. The patched resolver reports failures on stderr on Windows.
    $errorLog = Join-Path $DataPath 'smoke-test-err.log'
    $finalLog = if (Test-Path $logFile) { Get-Content $logFile -Raw -ErrorAction SilentlyContinue } else { '' }
    $finalError = if (Test-Path $errorLog) { Get-Content $errorLog -Raw -ErrorAction SilentlyContinue } else { '' }
    $diagnosticLog = $finalLog + "`n" + $finalError

    if (-not $reachedRunGame -and $finalLog -match 'Entering runphase RunGame') {
        $reachedRunGame = $true
    }
    if ($diagnosticLog -match 'Fatal|Unhandled exception|Failed to resolve assembly') {
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
    Write-Error "  Error log: $errorLog"
    exit 1

} finally {
    Pop-Location
    if ($ownData -and (Test-Path $DataPath)) {
        Remove-Item -Recurse -Force $DataPath -ErrorAction SilentlyContinue
    }
}
