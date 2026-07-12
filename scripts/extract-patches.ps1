<#
.SYNOPSIS
For each reconstituted project under .baseline/, diffs every .cs file in the working
folder against its baseline counterpart and writes:

  * Unified-diff patches to patches/<project>/<rel>.cs.patch when a baseline exists
    and content differs.
  * The raw .cs file to sources/<project>/<rel>.cs when no baseline exists. Stratum-
    original files live here as real source, not as patches (same as how Paper keeps
    paper-server/src/main/java for its own new sources).

All projects match by relative path. Open-source forks live under <name>/ at the
repo root; closed-source vanilla projects (VintagestoryLib, VintagestoryServer) live
under baseline/<name>/ and mirror the decompiled layout in .baseline/ 1:1.

Cairo is not patched (vanilla cairo-sharp is shipped untouched). Folders matching the
exclude list are skipped entirely.
#>

[CmdletBinding()]
param()

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
    $env:LC_ALL = 'C.UTF-8'
    $baselineDir = Join-Path $repoRoot '.baseline'
    $patchesDir  = Join-Path $repoRoot 'patches'
    $sourcesDir  = Join-Path $repoRoot 'sources'
    if (-not (Test-Path $baselineDir)) {
        throw "No .baseline/ found. Run scripts/bootstrap.ps1 first."
    }

    $forkProjects = @()
    $forksFile = Join-Path $repoRoot 'forks.json'
    if (Test-Path $forksFile) {
        $cfg = Get-Content $forksFile -Raw | ConvertFrom-Json
        foreach ($fork in $cfg.forks) { $forkProjects += $fork.name }
    }

    # Closed-source vanilla projects that we do patch. Cairo is intentionally omitted.
    $vanillaProjects = @('VintagestoryLib', 'VintagestoryServer')

    # Path segments that mean "don't touch this": build output, IDE state, IL/Roslyn
    # source-generated noise, and the DevMenu/ImGui scratch tree from another project.
    $excludeSegments = @('bin', 'obj', '.vs', '.git', 'Generated', 'DevMenu', 'ImGui')

    $devNull = if ($PSVersionTable.Platform -eq 'Unix') { '/dev/null' } else { 'NUL' }

    function Test-Excluded {
        param([string]$RelPath)
        $parts = $RelPath -split '[\\/]'
        foreach ($p in $parts) {
            if ($excludeSegments -contains $p) { return $true }
        }
        return $false
    }

    function Write-Lf {
        param([string]$Path, [string[]]$Lines)
        $text  = ($Lines -join "`n") + "`n"
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        $dir   = Split-Path $Path
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        [System.IO.File]::WriteAllBytes($Path, $bytes)
    }

    function Copy-AsLf {
        param([string]$Src, [string]$Dst)
        $dir = Split-Path $Dst
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        $text = [System.IO.File]::ReadAllText($Src) -replace "`r`n", "`n"
        [System.IO.File]::WriteAllBytes($Dst, [System.Text.Encoding]::UTF8.GetBytes($text))
    }

    function Write-NormalizedCopy {
        param([string]$Src, [string]$Dst)
        # VS saves .cs as UTF-8-with-BOM by default!! ilspycmd output is BOM-less LF.
        # Normalize both sides to BOM-less LF before diffing so the BOM never fkn adds
        # in a patch as a + / - byte on the first line
        $text = [System.IO.File]::ReadAllText($Src) -replace "`r`n", "`n"
        [System.IO.File]::WriteAllBytes($Dst, [System.Text.Encoding]::UTF8.GetBytes($text))
    }

    function Get-DiffLines {
        param([string]$BaseFile, [string]$WorkFile)
        $tmpBase = [System.IO.Path]::GetTempFileName()
        $tmpWork = [System.IO.Path]::GetTempFileName()
        try {
            Write-NormalizedCopy -Src $BaseFile -Dst $tmpBase
            Write-NormalizedCopy -Src $WorkFile -Dst $tmpWork
            $out = & git --no-pager -c core.safecrlf=false diff --no-color --no-index -U5 -- $tmpBase $tmpWork 2>$null
            return $out
        } finally {
            Remove-Item -Force -ErrorAction SilentlyContinue $tmpBase, $tmpWork
        }
    }

    function Format-PatchHeaders {
        param([string[]]$DiffLines, [string]$RelPath)
        if (-not $DiffLines) { return $null }
        $rel = $RelPath -replace '\\', '/'
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($line in $DiffLines) {
            if     ($line -like 'diff --git *') { $out.Add("diff --git a/$rel b/$rel") }
            elseif ($line -like '--- *')        { $out.Add("--- a/$rel") }
            elseif ($line -like '+++ *')        { $out.Add("+++ b/$rel") }
            else                                { $out.Add($line) }
        }
        return $out.ToArray()
    }

    function Get-DiffSize {
        param([string[]]$DiffLines)
        if (-not $DiffLines) { return 0 }
        $size = 0
        foreach ($l in $DiffLines) {
            if ($l.Length -gt 0 -and ($l[0] -eq '+' -or $l[0] -eq '-') -and -not ($l -like '+++ *' -or $l -like '--- *')) {
                $size++
            }
        }
        return $size
    }

    function Get-ProjectWorkFiles {
        param([string]$ProjRoot)
        Get-ChildItem -Path $ProjRoot -Recurse -File -Filter '*.cs' | Where-Object {
            $rel = $_.FullName.Substring($ProjRoot.Length + 1)
            -not (Test-Excluded $rel)
        }
    }

    $patchesWritten = 0
    $sourcesWritten = 0
    $cleared = 0

    function Sync-NewFile {
        param([string]$Proj, [string]$WorkRel, [string]$SrcFile, [ref]$WrittenRef, [hashtable]$Kept)
        $dst = Join-Path $sourcesDir "$Proj/$WorkRel"
        Copy-AsLf -Src $SrcFile -Dst $dst
        $WrittenRef.Value++
        $Kept[(Resolve-Path $dst).Path] = $true
    }

    function Clear-StalePatches {
        param([string]$Proj, [hashtable]$KeepBaseFullPaths, [string]$BaseRoot, [string]$WorkRoot)
        $projPatchDir = Join-Path $patchesDir $Proj
        if (-not (Test-Path $projPatchDir)) { return }
        Get-ChildItem -Path $projPatchDir -Recurse -File -Filter '*.patch' | ForEach-Object {
            $relPatch  = $_.FullName.Substring($projPatchDir.Length + 1)
            $relSource = $relPatch -replace '\.patch$', ''
            if (Test-Excluded $relSource) { Remove-Item $_.FullName; $script:cleared++; return }
            if ($BaseRoot) {
                $baseFile = Join-Path $BaseRoot $relSource
                if (Test-Path $baseFile) {
                    $full = (Resolve-Path $baseFile).Path
                    if ($KeepBaseFullPaths.ContainsKey($full)) { return }
                }
            }
            $workFile = Join-Path $WorkRoot $relSource
            if (Test-Path $workFile) { return }
            Remove-Item $_.FullName
            $script:cleared++
        }
    }

    function Clear-StaleSources {
        param([string]$Proj, [hashtable]$Kept, [string]$WorkRoot)
        $projSrcDir = Join-Path $sourcesDir $Proj
        if (-not (Test-Path $projSrcDir)) { return }
        Get-ChildItem -Path $projSrcDir -Recurse -File | ForEach-Object {
            # Only manage .cs files. csproj/json/xml overrides under sources/ are
            # hand-placed and not produced by this script; never delete them.
            if ($_.Extension -ne '.cs') { return }
            $full = (Resolve-Path $_.FullName).Path
            if ($Kept.ContainsKey($full)) { return }
            Remove-Item $_.FullName
            $script:cleared++
        }
        # Remove empty directories left behind.
        Get-ChildItem -Path $projSrcDir -Recurse -Directory |
            Sort-Object { $_.FullName.Length } -Descending |
            Where-Object { -not (Get-ChildItem -Path $_.FullName -Force) } |
            ForEach-Object { Remove-Item $_.FullName }
    }

    foreach ($proj in $forkProjects) {
        $work = Join-Path $repoRoot $proj
        $base = Join-Path $baselineDir $proj
        if (-not (Test-Path $work) -or -not (Test-Path $base)) { continue }

        $keptBase = @{}
        $keptSrc  = @{}

        Write-Host "Extracting patches for $proj" -ForegroundColor Cyan

        foreach ($file in Get-ProjectWorkFiles $work) {
            $rel = $file.FullName.Substring($work.Length + 1)
            $baseFile  = Join-Path $base $rel
            $patchFile = Join-Path $patchesDir "$proj/$rel.patch"

            if (-not (Test-Path $baseFile)) {
                Sync-NewFile -Proj $proj -WorkRel $rel -SrcFile $file.FullName -WrittenRef ([ref]$sourcesWritten) -Kept $keptSrc
                Write-Host "  $rel " -NoNewLine
                Write-Host "(source)" -ForegroundColor Yellow
                continue
            }
            $diff = Get-DiffLines $baseFile $file.FullName
            if ($diff) {
                $headers = Format-PatchHeaders -DiffLines $diff -RelPath "$proj/$rel"
                Write-Lf -Path $patchFile -Lines $headers
                $patchesWritten++
                $keptBase[(Resolve-Path $baseFile).Path] = $true
                Write-Host "  $rel"
            } elseif (Test-Path $patchFile) {
                Remove-Item $patchFile; $cleared++
            }
        }

        Clear-StalePatches -Proj $proj -KeepBaseFullPaths $keptBase -BaseRoot $base -WorkRoot $work
        Clear-StaleSources -Proj $proj -Kept $keptSrc -WorkRoot $work
    }

    foreach ($proj in $vanillaProjects) {
        $work = Join-Path $repoRoot "baseline/$proj"
        $base = Join-Path $baselineDir $proj
        if (-not (Test-Path $work) -or -not (Test-Path $base)) { continue }

        $keptBase = @{}
        $keptSrc  = @{}
        
        Write-Host "Extracting patches for $proj" -ForegroundColor Cyan

        foreach ($file in Get-ProjectWorkFiles $work) {
            $rel = $file.FullName.Substring($work.Length + 1)
            $baseFile  = Join-Path $base $rel
            $patchFile = Join-Path $patchesDir "$proj/$rel.patch"

            if (-not (Test-Path $baseFile)) {
                Sync-NewFile -Proj $proj -WorkRel $rel -SrcFile $file.FullName -WrittenRef ([ref]$sourcesWritten) -Kept $keptSrc
                Write-Host "  $rel " -NoNewLine
                Write-Host "(source)" -ForegroundColor Yellow
                continue
            }
            $diff = Get-DiffLines $baseFile $file.FullName
            if ($diff) {
                $headers = Format-PatchHeaders -DiffLines $diff -RelPath "$proj/$rel"
                Write-Lf -Path $patchFile -Lines $headers
                $patchesWritten++
                $keptBase[(Resolve-Path $baseFile).Path] = $true
                Write-Host "  $rel"
            } elseif (Test-Path $patchFile) {
                Remove-Item $patchFile; $cleared++
            }
        }

        Clear-StalePatches -Proj $proj -KeepBaseFullPaths $keptBase -BaseRoot $base -WorkRoot $work
        Clear-StaleSources -Proj $proj -Kept $keptSrc -WorkRoot $work
    }

    Write-Host "Wrote $patchesWritten patch(es), $sourcesWritten source file(s); cleared $cleared stale entry(ies)." -ForegroundColor Green
} finally {
    Pop-Location
}
