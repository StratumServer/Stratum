<#
.SYNOPSIS
Packages Stratum release archives from `dotnet publish` output.

Only Stratum-built artifacts go in the zip:
  StratumServer.exe / .dll / .runtimeconfig.json
  VintagestoryServer.dll, VintagestoryLib.dll, VintagestoryAPI.dll, cairo-sharp.dll
  Mods/VSEssentials.dll, Mods/VSSurvivalMod.dll

Vanilla assets (assets/, Lib/, base mods, native runtimes) are NOT included.
They are downloaded from cdn.vintagestory.at on first launch by StratumServer.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishRoot,
    [Parameter(Mandatory)][string]$OutDir
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$infoFile = Join-Path $repoRoot 'sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs'
$infoText = Get-Content $infoFile -Raw
$version  = [regex]::Match($infoText, 'Version\s*=\s*"([^"]+)"').Groups[1].Value
if (-not $version) { throw "Could not read Stratum version from $infoFile" }

$keepFiles = @(
    'StratumServer.exe',
    'StratumServer.dll',
    'StratumServer.runtimeconfig.json',
    'StratumServer.deps.json',
    'VintagestoryServer.exe',
    'VintagestoryServer.dll',
    'VintagestoryServer.runtimeconfig.json',
    'VintagestoryLib.dll',
    'VintagestoryAPI.dll',
    'cairo-sharp.dll'
)
$keepMods = @(
    'VSEssentials.dll',
    'VSSurvivalMod.dll'
)

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Get-ChildItem -Directory $PublishRoot | ForEach-Object {
    $rid = $_.Name
    $stage = Join-Path $OutDir "stage-$rid"
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    foreach ($name in $keepFiles) {
        $src = Join-Path $_.FullName $name
        if (Test-Path $src) { Copy-Item $src $stage }
    }

    $modsDst = Join-Path $stage 'Mods'
    New-Item -ItemType Directory -Force -Path $modsDst | Out-Null
    foreach ($name in $keepMods) {
        $candidates = @(
            (Join-Path $_.FullName "Mods/$name"),
            (Join-Path $_.FullName $name)
        )
        $src = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($src) { Copy-Item $src $modsDst }
    }

    Copy-Item (Join-Path $repoRoot 'LICENSE')  $stage
    Copy-Item (Join-Path $repoRoot 'README.md') $stage

    $zipName = "stratum-$version-$rid.zip"
    $zipPath = Join-Path $OutDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath
    Write-Host "Wrote $zipPath"
}
