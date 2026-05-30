# Contributing to Stratum

Thanks for helping out. Read this before opening a PR.

## Project layout

Stratum is a patch set over the vanilla Vintage Story server. The repo does not contain vanilla source.

- `patches/` — unified diffs against the decompiled vanilla baseline.
- `sources/` — files that exist only in Stratum (new code, no vanilla equivalent).
- `StratumServer/` — the launcher and first-run vanilla downloader.
- `scripts/` — `bootstrap.ps1` rebuilds the working tree from `patches/` + `sources/`. `extract-patches.ps1` writes your changes back out.
- `VintageStory.slnx` — solution. Only opens after `bootstrap.ps1` has run.

## Setting up

```powershell
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

`bootstrap.ps1` downloads the matching vanilla server zip, decompiles the assemblies, applies every patch, and copies `sources/` over the top. After that you have a normal C# solution to edit.

## Workflow

1. Edit files under the working tree as if it were any other repo.
2. Build and test locally.
3. `.\scripts\extract-patches.ps1` — regenerates `patches/` and `sources/` from your changes.
4. `git add patches sources` and commit. Never commit the working tree itself.

## Tagging

Every change to a vanilla file needs a marker so it's obvious what is ours.

Single line:

```csharp
public int ViewDistance = 256; // Stratum - raise default view distance
```

Multi line:

```csharp
// Stratum start - async chunk save
Task.Run(() => SaveChunk(c));
// Stratum end
```

New files under `sources/` don't need markers. The whole file is ours.

## What not to patch

- Generated code (anything under `obj/`, `bin/`, or files marked auto-generated).
- Files that only changed because the decompiler emitted them slightly differently. If your diff is just whitespace, reordered usings, or `this.` prefixes, drop it.
- Vanilla bugs that have a fix upstream. Wait for the next vanilla release instead.

## Style

- Tabs.
- File-scoped namespaces in new files.
- `internal` by default. Only make things `public` when something outside the assembly needs them.
- Match surrounding style when patching vanilla. Don't reformat the file.
- Plain English in comments and commit messages. No marketing voice.

## Commits

- One logical change per commit.
- Subject line under 72 chars, imperative ("Add async chunk save", not "Added" or "Adds").
- Reference issues with `Fixes #123` when relevant.

## Pull requests

Before opening:

- [ ] `.\scripts\extract-patches.ps1` ran clean.
- [ ] `dotnet build VintageStory.slnx -c Release` is green.
- [ ] Every vanilla edit has a `// Stratum` marker.
- [ ] No vanilla source committed.
- [ ] Tested against a real server start, not just compilation.

In the PR description say what changed and why. If it's a perf change, include before/after numbers.

## Bug reports

Open an issue with:

- Stratum version (`StratumInfo.Version`) and base game version.
- OS and .NET runtime version.
- Steps to reproduce.
- Server log excerpt, not a screenshot.
