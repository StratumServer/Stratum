<#
.SYNOPSIS
Builds a clean working tree by laying down:
  1. Decompiled closed-source vanilla VS libraries (VintagestoryLib, VintagestoryServer),
     reconstructed from the official server archive.
  2. Open-source Anego forks (VintagestoryApi, VSEssentials, VSSurvivalMod), cloned from
     their upstream repos at the commits pinned in forks.json.

Then applies every patch in patches/ on top.

.PARAMETER Version
Vintage Story server version to download when -ServerZip is not provided. Defaults to 1.22.4.

.PARAMETER ServerZip
Path to an already-downloaded official server archive. If omitted, the script resolves
the archive from https://api.vintagestory.at/stable-unstable.json into .vanilla-zips/.

.PARAMETER Refresh
Force re-extract, re-decompile, and re-clone even if cached output already exists.

.EXAMPLE
.\scripts\bootstrap.ps1
.\scripts\bootstrap.ps1 -Version 1.22.4
.\scripts\bootstrap.ps1 -Refresh
#>

[CmdletBinding()]
param(
    [string]$Version = '1.22.4',
    [string]$ServerZip,
    [switch]$Refresh
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Get-ServerArchiveInfo {
    param([string]$Version)

    $manifestUrl = 'https://api.vintagestory.at/stable-unstable.json'
    $platformKey = if ($IsWindows -or [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        'windowsserver'
    } else {
        'linuxserver'
    }

    $manifest = Invoke-RestMethod -Uri $manifestUrl
    $versionEntry = $manifest.PSObject.Properties[$Version].Value
    if (-not $versionEntry) {
        throw "Vintage Story version not found in Anego manifest: $Version"
    }

    $archiveEntry = $versionEntry.PSObject.Properties[$platformKey].Value
    if (-not $archiveEntry) {
        throw "Vintage Story server archive not found in Anego manifest for $platformKey $Version"
    }

    [pscustomobject]@{
        FileName = $archiveEntry.filename
        Url = $archiveEntry.urls.cdn
        Md5 = $archiveEntry.md5
    }
}

function Test-Md5 {
    param(
        [string]$Path,
        [string]$Expected
    )

    if (-not (Test-Path $Path)) {
        return $false
    }

    $actual = (Get-FileHash -Algorithm MD5 -Path $Path).Hash
    return $actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Expand-ServerArchive {
    param(
        [string]$Archive,
        [string]$Destination
    )

    if ($Archive.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        Expand-Archive -Path $Archive -DestinationPath $Destination -Force
        return
    }

    if ($Archive.EndsWith('.tar.gz', [StringComparison]::OrdinalIgnoreCase) -or $Archive.EndsWith('.tgz', [StringComparison]::OrdinalIgnoreCase)) {
        tar -xzf $Archive -C $Destination
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed to extract $Archive"
        }
        return
    }

    throw "Unsupported server archive type: $Archive"
}

Push-Location $repoRoot
try {
    # Closed-source DLLs decompiled from the official server archive. Cairo used to
    # live here too, but its source is now mirrored on GitHub (anegostudios/Cairo)
    # and cloned via forks.json instead.
    $libMap = [ordered]@{
        'VintagestoryLib.dll'    = @{ Project = 'VintagestoryLib';    Work = 'baseline/VintagestoryLib' }
        'VintagestoryServer.dll' = @{ Project = 'VintagestoryServer'; Work = 'baseline/VintagestoryServer' }
    }

    $vanillaDir  = Join-Path $repoRoot '.vanilla'
    $baselineDir = Join-Path $repoRoot '.baseline'
    $zipCacheDir = Join-Path $repoRoot '.vanilla-zips'

    if ($Refresh -and (Test-Path $vanillaDir))  { Remove-Item -Recurse -Force $vanillaDir }
    if ($Refresh -and (Test-Path $baselineDir)) { Remove-Item -Recurse -Force $baselineDir }

    #
    # 1. Vanilla server archive -> decompile -> baseline -> working folders
    #
    if (-not $ServerZip) {
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $archiveInfo = Get-ServerArchiveInfo -Version $Version
        $ServerZip = Join-Path $zipCacheDir $archiveInfo.FileName
        if (-not (Test-Path $ServerZip)) {
            Write-Host "Downloading $($archiveInfo.Url)"
            $ProgressPreference = 'Continue'
            Invoke-WebRequest -Uri $archiveInfo.Url -OutFile $ServerZip
        } elseif (-not (Test-Md5 -Path $ServerZip -Expected $archiveInfo.Md5)) {
            Write-Host "Cached archive failed checksum, downloading $($archiveInfo.Url)"
            Remove-Item $ServerZip -Force
            Invoke-WebRequest -Uri $archiveInfo.Url -OutFile $ServerZip
        } else {
            Write-Host "Using cached $ServerZip"
        }

        if (-not (Test-Md5 -Path $ServerZip -Expected $archiveInfo.Md5)) {
            throw "Downloaded server archive failed MD5 verification: $ServerZip"
        }
    }

    if (-not (Test-Path $vanillaDir)) {
        if (-not (Test-Path $ServerZip)) { throw "Server archive not found: $ServerZip" }
        Write-Host "Extracting $ServerZip"
        New-Item -ItemType Directory -Force -Path $vanillaDir | Out-Null
        Expand-ServerArchive -Archive $ServerZip -Destination $vanillaDir
    }

    # Build-time compatibility: some server archives omit a small set of managed
    # client-facing references that VintagestoryLib still compiles against.
    # Pull only those DLLs from the public Linux client archive when missing.
    $vanillaLibDir = Join-Path $vanillaDir 'Lib'
    New-Item -ItemType Directory -Force -Path $vanillaLibDir | Out-Null
    $requiredClientRefs = @('OpenTK.Graphics.dll', 'csogg.dll', 'csvorbis.dll')
    $missingClientRefs = @($requiredClientRefs | Where-Object { -not (Test-Path (Join-Path $vanillaLibDir $_)) })
    if ($missingClientRefs.Count -gt 0) {
        Write-Host "Missing managed refs in server archive: $($missingClientRefs -join ', ')"
        New-Item -ItemType Directory -Force -Path $zipCacheDir | Out-Null
        $clientTarName = "vs_client_linux-x64_$Version.tar.gz"
        $clientTarPath = Join-Path $zipCacheDir $clientTarName
        if (-not (Test-Path $clientTarPath)) {
            $clientManifest = Invoke-RestMethod -Uri 'https://api.vintagestory.at/stable-unstable.json'
            $clientEntry = $clientManifest.PSObject.Properties[$Version].Value.linux
            $clientUrl = $clientEntry.urls.cdn
            Write-Host "Downloading $clientUrl"
            Invoke-WebRequest -Uri $clientUrl -OutFile $clientTarPath
        } else {
            Write-Host "Using cached $clientTarPath"
        }

        $tmpClientExtract = Join-Path $repoRoot ".tmp-client-$Version"
        if (Test-Path $tmpClientExtract) { Remove-Item -Recurse -Force $tmpClientExtract }
        New-Item -ItemType Directory -Force -Path $tmpClientExtract | Out-Null
        tar -xzf $clientTarPath -C $tmpClientExtract

        foreach ($refName in $missingClientRefs) {
            $sourceRef = Get-ChildItem -Path $tmpClientExtract -Recurse -File -Filter $refName | Select-Object -First 1
            if ($sourceRef) {
                Copy-Item -Force $sourceRef.FullName (Join-Path $vanillaLibDir $refName)
                Write-Host "Restored build ref $refName from Linux client archive"
            } else {
                Write-Warning "Could not find $refName in $clientTarName"
            }
        }

        Remove-Item -Recurse -Force $tmpClientExtract
    }

    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        $manifest = Join-Path $PSScriptRoot "../.config/dotnet-tools.json"
        if (Test-Path $manifest) {
            $json = Get-Content $manifest -Raw | ConvertFrom-Json
            $pinnedVersion = $json.tools.ilspycmd.version
            Write-Host "Installing ilspycmd $pinnedVersion (from tool manifest)"
            dotnet tool install -g ilspycmd --version $pinnedVersion | Out-Null
        } else {
            Write-Host "Installing ilspycmd (no manifest found, using latest)"
            dotnet tool install -g ilspycmd | Out-Null
        }
        $env:PATH += ";$env:USERPROFILE\.dotnet\tools"
    }

    foreach ($dll in $libMap.Keys) {
        $proj = $libMap[$dll].Project
        $workRel = $libMap[$dll].Work
        $dllPath = Get-ChildItem -Path $vanillaDir -Recurse -Filter $dll | Select-Object -First 1
        if (-not $dllPath) { Write-Warning "Skipping $dll, not found in zip"; continue }

        $out = Join-Path $baselineDir $proj
        if (-not (Test-Path $out) -or $Refresh) {
            Write-Host "Decompiling $dll into $out"
            New-Item -ItemType Directory -Force -Path $out | Out-Null
            $prevEAP = $ErrorActionPreference
            try {
                $ErrorActionPreference = 'Continue'
                $decompileOutput = & ilspycmd $dllPath.FullName --project -o $out 2>&1
                $decompileExitCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $prevEAP
            }
            if ($decompileExitCode -ne 0) {
                throw "ilspycmd failed to decompile $dll (exit $decompileExitCode):`n$($decompileOutput -join "`n")"
            }
        }

        # ilspycmd writes <LangVersion>15.0</LangVersion>, which the .NET 10 SDK
        # rejects with CS1617. Normalize to a value the current Roslyn accepts.
        Get-ChildItem -Path $out -Filter '*.csproj' -File | ForEach-Object {
            $csprojText = [IO.File]::ReadAllText($_.FullName)
            $patched = $csprojText -replace '<LangVersion>15\.0</LangVersion>', '<LangVersion>latest</LangVersion>'
            if ($patched -ne $csprojText) {
                [IO.File]::WriteAllText($_.FullName, $patched)
            }
        }

        $work = Join-Path $repoRoot $workRel
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

            # Normalize text files to LF and strip UTF-8 BOMs. extract-patches.ps1
            # diffs against LF-normalized BOM-free content, so the patches in patches/
            # assume LF and no BOM; some upstream repos ship .gitattributes that force
            # CRLF on checkout (or Windows autocrlf does), and some files have BOMs,
            # both of which make `git apply` reject hunks. Runs on every bootstrap,
            # not only at clone time: it is idempotent and heals baselines checked
            # out before BOM stripping existed.
            Get-ChildItem -Path $base -Recurse -File -Include '*.cs','*.csproj','*.json','*.xml','*.props','*.targets' -ErrorAction SilentlyContinue | ForEach-Object {
                $bytes = [IO.File]::ReadAllBytes($_.FullName)
                $hasBOM = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
                $hasCR = $false
                foreach ($byte in $bytes) { if ($byte -eq 13) { $hasCR = $true; break } }
                if ($hasCR -or $hasBOM) {
                    $start = if ($hasBOM) { 3 } else { 0 }
                    $text = [Text.Encoding]::UTF8.GetString($bytes, $start, $bytes.Length - $start) -replace "`r`n", "`n"
                    [IO.File]::WriteAllBytes($_.FullName, [Text.Encoding]::UTF8.GetBytes($text))
                }
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
        $failed = @()
        Get-ChildItem -Path $patchesDir -Recurse -Filter '*.patch' | ForEach-Object {
            $rel = $_.FullName.Substring($repoRoot.Length + 1)
            $topProj = ($rel -split '[\\/]')[1]
            $applyArgs = @('apply','--whitespace=nowarn')
            if ($vanillaPatchProjects -contains $topProj) { $applyArgs += '--directory=baseline' }
            $applyArgs += $_.FullName
            Write-Host "Applying $rel"
            $prevEAP = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            $out = & git @applyArgs 2>&1
            $code = $LASTEXITCODE
            $ErrorActionPreference = $prevEAP
            if ($code -ne 0) {
                $failed += $rel
                $out | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
            }
        }
        if ($failed.Count -gt 0) {
            Write-Host ""
            Write-Host "$($failed.Count) patch(es) failed to apply:" -ForegroundColor Red
            $failed | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
            Write-Host "Fix the conflicts in the working tree, then run scripts\extract-patches.ps1." -ForegroundColor Red
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
    if ($failed -and $failed.Count -gt 0) {
        # A build after a failed bootstrap compiles vanilla files for the failed
        # patches and extract-patches then silently deletes their content, so this
        # must be a hard failure, not a note in the scroll-back.
        Write-Host "Bootstrap FAILED: $($failed.Count) patch(es) did not apply. The working tree is incomplete." -ForegroundColor Red
        exit 1
    }
    Write-Host "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release" -ForegroundColor Green
} finally {
    Pop-Location
}
