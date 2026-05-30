<#
.SYNOPSIS
Builds a clean working tree by laying down:
  1. Decompiled closed-source vanilla VS libraries (VintagestoryLib, VintagestoryServer, Cairo),
     reconstructed from the official server zip.
  2. Open-source Anego forks (VintagestoryApi, VSEssentials, VSSurvivalMod), cloned from
     their upstream repos at the commits pinned in forks.json.

Then applies every patch in patches/ on top.

.PARAMETER Version
Vintage Story server version to download when -ServerZip is not provided. Defaults to 1.22.3.

.PARAMETER ServerZip
Path to an already-downloaded vs_server_*.zip. If omitted, the script downloads the
zip for -Version from cdn.vintagestory.at into .vanilla-zips/.

.PARAMETER Refresh
Force re-extract, re-decompile, and re-clone even if cached output already exists.

.EXAMPLE
.\scripts\bootstrap.ps1
.\scripts\bootstrap.ps1 -Version 1.22.3
.\scripts\bootstrap.ps1 -Refresh
#>

[CmdletBinding()]
param(
    [string]$Version = '1.22.3',
    [string]$ServerZip,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    # Closed-source DLLs and the project folder each one is decompiled into.
    $libMap = [ordered]@{
        'VintagestoryLib.dll'    = 'VintagestoryLib'
        'VintagestoryServer.dll' = 'VintagestoryServer'
        'cairo-sharp.dll'        = 'Cairo'
    }

    $vanillaDir  = Join-Path $repoRoot '.vanilla'
    $baselineDir = Join-Path $repoRoot '.baseline'
    $zipCacheDir = Join-Path $repoRoot '.vanilla-zips'

    if ($Refresh -and (Test-Path $vanillaDir))  { Remove-Item -Recurse -Force $vanillaDir }
    if ($Refresh -and (Test-Path $baselineDir)) { Remove-Item -Recurse -Force $baselineDir }

    #
    # 1. Vanilla server zip -> decompile -> baseline -> working folders
    #
    if (-not $ServerZip) {
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $zipName = "vs_server_win-x64_$Version.zip"
        $ServerZip = Join-Path $zipCacheDir $zipName
        if (-not (Test-Path $ServerZip)) {
            $url = "https://cdn.vintagestory.at/gamefiles/stable/$zipName"
            Write-Host "Downloading $url"
            $ProgressPreference = 'Continue'
            Invoke-WebRequest -Uri $url -OutFile $ServerZip
        } else {
            Write-Host "Using cached $ServerZip"
        }
    }

    if (-not (Test-Path $vanillaDir)) {
        if (-not (Test-Path $ServerZip)) { throw "Server zip not found: $ServerZip" }
        Write-Host "Extracting $ServerZip"
        Expand-Archive -Path $ServerZip -DestinationPath $vanillaDir -Force
    }

    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        Write-Host "Installing ilspycmd"
        dotnet tool install -g ilspycmd | Out-Null
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }

    foreach ($dll in $libMap.Keys) {
        $proj = $libMap[$dll]
        $dllPath = Get-ChildItem -Path $vanillaDir -Recurse -Filter $dll | Select-Object -First 1
        if (-not $dllPath) { Write-Warning "Skipping $dll, not found in zip"; continue }

        $out = Join-Path $baselineDir $proj
        if (-not (Test-Path $out) -or $Refresh) {
            Write-Host "Decompiling $dll into $out"
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            ilspycmd $dllPath.FullName --project -o $out | Out-Null
        }

        $work = Join-Path $repoRoot "baseline/$proj"
        if (Test-Path $work) { Remove-Item -Recurse -Force $work }
        Copy-Item -Recurse -Force $out $work
    }

    #
    # 2. Upstream Anego forks -> baseline -> working folders
    #
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) {
        $cfg = Get-Content $forksFile -Raw | ConvertFrom-Json
        foreach ($fork in $cfg.forks) {
            $name = $fork.name
            $url  = $fork.url
            $ref  = $fork.ref
            $base = Join-Path $baselineDir $name

            if (-not (Test-Path $base) -or $Refresh) {
                if (Test-Path $base) { Remove-Item -Recurse -Force $base }
                Write-Host "Cloning $url at $ref into $base"
                git clone --quiet $url $base | Out-Null
                git -C $base checkout --quiet $ref
                # Drop the upstream .git so this is just a baseline snapshot.
                Remove-Item -Recurse -Force (Join-Path $base '.git')
            }

            $work = Join-Path $repoRoot $name
            if (Test-Path $work) { Remove-Item -Recurse -Force $work }
            Copy-Item -Recurse -Force $base $work
        }
    }

    #
    # 3. Apply patches/ on top of everything.
    #
    $patchesDir = Join-Path $repoRoot 'patches'
    # Vanilla (decompiled) projects live under baseline/; their patches are applied
    # with --directory=baseline so paths like a/VintagestoryLib/... resolve into
    # baseline/VintagestoryLib/...
    $vanillaPatchProjects = @('VintagestoryLib','VintagestoryServer')
    if (Test-Path $patchesDir) {
        Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' | ForEach-Object {
            $rel = $_.FullName.Substring($repoRoot.Length + 1)
            $topProj = ($rel -split '[\\/]')[1]
            $applyArgs = @('apply','--3way','--whitespace=nowarn')
            if ($vanillaPatchProjects -contains $topProj) { $applyArgs += '--directory=baseline' }
            $applyArgs += $_.FullName
            Write-Host "Applying $rel"
            git @applyArgs 2>&1 | Write-Host
        }
    } else {
        Write-Host "No patches/ directory, skipping patch step."
    }

    #
    # 4. Drop Stratum-original sources on top.
    #    sources/<project>/<rel>.cs is committed as real source (Paper does the same
    #    with paper-server/src/main/java for its own new files); the script just
    #    copies them into the working folder so the csproj globs them.
    #
    $sourcesDir = Join-Path $repoRoot 'sources'
    if (Test-Path $sourcesDir) {
        Get-ChildItem -Path $sourcesDir -Directory | ForEach-Object {
            $proj = $_.Name
            $dst  = if ($vanillaPatchProjects -contains $proj) {
                Join-Path $repoRoot "baseline/$proj"
            } else {
                Join-Path $repoRoot $proj
            }
            if (-not (Test-Path $dst)) {
                Write-Warning "sources/$proj has no matching working folder; skipping."
                return
            }
            Get-ChildItem -Path $_.FullName -Recurse -File | ForEach-Object {
                $rel = $_.FullName.Substring((Join-Path $sourcesDir $proj).Length + 1)
                $target = Join-Path $dst $rel
                $targetDir = Split-Path $target
                if (-not (Test-Path $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
                Copy-Item -Force $_.FullName $target
            }
            Write-Host "Synced sources/$proj into $proj/"
        }
    }

    Write-Host ""
    Write-Host "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release" -ForegroundColor Green
} finally {
    Pop-Location
}
