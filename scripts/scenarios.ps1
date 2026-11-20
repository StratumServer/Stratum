<#
.SYNOPSIS
Runs the Atlas scenario suite in tests/StratumScenarios against a prepared install on
Windows. Builds if the launcher is missing, materializes the install with one
--stratum-prepare-only launch, then runs dotnet test with VINTAGE_STORY pointing at it
(mirrors scripts/scenarios.sh). Uses $env:CONFIGURATION, Release by default.

Extra arguments are passed to dotnet test.

.EXAMPLE
.\scripts\scenarios.ps1
.\scripts\scenarios.ps1 --filter BootScenarios
#>

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Release' }
$framework = 'net10.0'
$match = Select-String -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Pattern '<FrameworkVersion>([^<]+)</FrameworkVersion>' | Select-Object -First 1
if ($match) { $framework = $match.Matches[0].Groups[1].Value }
# The launcher materializes the vanilla install and the patched overlay into its own
# output directory, which is not configurable (AppContext.BaseDirectory), so that is
# what VINTAGE_STORY has to point at.
$serverDir = Join-Path $repoRoot "StratumServer\bin\$configuration\$framework"

$previousVintageStory = $env:VINTAGE_STORY
Push-Location -LiteralPath $repoRoot
try {
    # Build if the launcher is missing. Two passes, like the Makefile's build target: the
    # embed pass points at sibling projects' bin output by raw path, so on a tree where
    # those outputs do not exist yet it can race the projects that produce them.
    if (-not (Test-Path -LiteralPath (Join-Path $serverDir 'StratumServer.dll'))) {
        Write-Host "Building $configuration..."
        dotnet build VintageStory.slnx -c $configuration --verbosity quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet build VintageStory.slnx -c $configuration -p:EmbedPatchedFiles=true --verbosity quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    # Stops right after the overlay, before any world is created, and exits non-zero if
    # the build carried no patched files to overlay, which would leave a stale lib in place.
    dotnet (Join-Path $serverDir 'StratumServer.dll') --stratum-prepare-only --stratum-no-banner
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $env:VINTAGE_STORY = $serverDir
    dotnet test tests/StratumScenarios -c $configuration @args
    exit $LASTEXITCODE
} finally {
    $env:VINTAGE_STORY = $previousVintageStory
    Pop-Location
}
