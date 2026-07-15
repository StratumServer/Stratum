<#
.SYNOPSIS
Builds and packages Stratum launcher zips.

Produces a tiny zip per RID containing only:
  StratumServer.exe (single-file, framework-dependent, with Stratum patched managed files embedded as resources)
  StratumServer.runtimeconfig.json
  LICENSE, README.md

The launcher resolves the matching official server archive through https://api.vintagestory.at/stable-unstable.json on first run
then writes the embedded Stratum patched files over the extracted server.
#>

[CmdletBinding()]
param(
	[Parameter(Mandatory)][string[]]$Rids,
	[Parameter(Mandatory)][string]$OutDir,
	[string]$Version,
	[string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    $infoFile = @(
        (Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'baseline/VintagestoryLib/Vintagestory.Server/StratumInfo.cs'),
        (Join-Path $repoRoot 'baseline/VintagestoryLib/Vintagestory/Server/StratumInfo.cs')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $infoFile) { throw 'Could not locate StratumInfo.cs for version parsing.' }
    $infoText = Get-Content $infoFile -Raw
    $baseVer = [regex]::Match($infoText, 'BaseGameVersion\s*=\s*"([^"]+)"').Groups[1].Value
    $rev     = [regex]::Match($infoText, 'StratumRevision\s*=\s*"([^"]+)"').Groups[1].Value
    $pre     = [regex]::Match($infoText, 'PreRelease\s*=\s*"([^"]*)"').Groups[1].Value
    if (-not $baseVer -or -not $rev) { throw "Could not parse version pieces from $infoFile" }
	$sourceVersion = if ($pre) { "$baseVer-stratum.$rev-$pre" } else { "$baseVer-stratum.$rev" }
	if (-not $Version -and $env:STRATUM_VER) {
		$Version = $env:STRATUM_VER
	}
	if (-not $Version) {
		$Version = $sourceVersion
	}
	$versionBase = ($Version -split '-stratum')[0]
	if ($versionBase -ne $baseVer) {
		throw "Release version '$Version' does not match StratumInfo.BaseGameVersion '$baseVer'."
	}
	if ($Version -ne $sourceVersion) {
		Write-Host "Packing version $Version from tag/env; StratumInfo fallback is $sourceVersion"
	}

    $libProject = @(
        (Join-Path $repoRoot 'baseline/VintagestoryLib/VintagestoryLib.csproj'),
        (Join-Path $repoRoot 'VintagestoryLib/VintagestoryLib.csproj')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $libProject) { throw 'Could not find VintagestoryLib.csproj in baseline/VintagestoryLib/ or VintagestoryLib/.' }

    # Build the patched server library first. The solution intentionally skips the baseline lib project, but StratumServer embeds its output during publish.
	Write-Host "Building patched server library (Configuration=$Configuration, Version=$Version)"
	& dotnet build $libProject -c $Configuration -p:Version=$Version -p:InformationalVersion=$Version -p:SkipDeployToVSInstall=true -nologo
    if ($LASTEXITCODE -ne 0) { throw "Patched server library build failed" }

    # Build the rest of the solution so API/mod outputs exist before publish.
	Write-Host "Building solution (Configuration=$Configuration, Version=$Version)"
	& dotnet build VintageStory.slnx -c $Configuration -p:Version=$Version -p:InformationalVersion=$Version -nologo
    if ($LASTEXITCODE -ne 0) { throw "Solution build failed" }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

    foreach ($rid in $Rids) {
        Write-Host "Publishing StratumServer for $rid"
        $publishDir = Join-Path $repoRoot "StratumServer/bin/$Configuration/publish-$rid"
        if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }

        & dotnet publish StratumServer/StratumServer.csproj `
            -c $Configuration `
            -r $rid `
            -p:SelfContained=false `
            -p:PublishSingleFile=true `
            -p:EmbedPatchedFiles=true `
			-p:Version=$Version `
			-p:InformationalVersion=$Version `
            -o $publishDir `
            -nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $rid" }

        $stage = Join-Path $OutDir "stage-$rid"
        if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
        New-Item -ItemType Directory -Force -Path $stage | Out-Null

        $exeName = if ($rid -like 'win-*') { 'StratumServer.exe' } else { 'StratumServer' }
        Copy-Item (Join-Path $publishDir $exeName) $stage
        $runtimeCfg = Join-Path $publishDir 'StratumServer.runtimeconfig.json'
        if (Test-Path $runtimeCfg) { Copy-Item $runtimeCfg $stage }
        Copy-Item (Join-Path $repoRoot 'LICENSE')  $stage
        Copy-Item (Join-Path $repoRoot 'README.md') $stage

		$zipName = "stratum-$Version-$rid.zip"
        $zipPath = Join-Path $OutDir $zipName
        if (Test-Path $zipPath) { Remove-Item $zipPath }
        Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
        Write-Host "Wrote $zipPath"
    }
}
finally {
    Pop-Location
}
