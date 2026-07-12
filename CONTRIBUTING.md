# Contributing to Stratum

Thanks for helping out. Read this before opening a PR.

## Project layout

Stratum is a patch set over the vanilla Vintage Story server. The repo does not contain vanilla source.

- `patches/` is unified diffs against the decompiled vanilla baseline.
- `sources/` is files that exist only in Stratum. No vanilla equivalent.
- `StratumServer/` is the launcher and the first-run vanilla downloader.
- `scripts/` holds bootstrap, extract-patches, and smoke-test scripts (`.sh` for Linux/macOS, `.ps1` for Windows).
- `VintageStory.slnx` is the solution. It only opens after `bootstrap.ps1` has run.

The working tree itself is gitignored. Only `patches/` and `sources/` are tracked.

## Setting up

You need the .NET 10 SDK. Linux and macOS contributors use `scripts/bootstrap.sh`; Windows contributors use `scripts/bootstrap.ps1` (PowerShell).

```bash
# Linux / macOS
scripts/bootstrap.sh
dotnet build VintageStory.slnx -c Release

# Or use make (runs bootstrap if needed):
make build
```

```powershell
# Windows
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

`bootstrap.ps1` downloads the matching vanilla server zip, decompiles the assemblies into the working tree, applies every patch, then copies `sources/` on top. After that you have a normal C# solution to edit.

If a patch fails to apply you usually want to delete the working tree and rerun bootstrap, not hand-edit the rejected hunk.

## Workflow

1. Edit files in the working tree like any other repo.
2. Build and test locally on a real server start. See "Testing" below.
3. Run `.\scripts\extract-patches.ps1`. This regenerates `patches/` and `sources/` from your changes.
4. `git add patches sources` and commit. Never commit the working tree itself.

If you started a branch before the latest vanilla bump, rerun bootstrap against the new baseline before extracting patches. Patches that no longer apply cleanly are your problem to fix.

## Tagging

Every change to a vanilla file needs a marker so the next person reading the diff knows what is ours. New files under `sources/` do not need markers, the whole file is ours.

The preferred form is a single leading comment on the block you changed, prefixed `// Stratum:`. Use this everywhere you can:

```csharp
public long LastActivityTotalMs;

// Stratum: chat throttling state.
public long StratumLastChatMs;
public string StratumLastChatMessage;
public long StratumLastChatMessageMs;
public int StratumDroppedChatCount;

public int TotalFlySuspicions;
```

For a one-line tweak inside an existing line, append the marker at the end of the line:

```csharp
public int MaxChunkRadius = 12; // Stratum: was 8
```

For a longer inserted block where the start and end are not obvious from braces, bracket it:

```csharp
// Stratum start: forward Nimbus proxy IP after reservation hand-off
if (!string.IsNullOrEmpty(forwardedIp))
{
    ipAddress = forwardedIp;
    IsLocalConnection = ipAddress == "127.0.0.1" || ipAddress.StartsWithFast("::1");
}
// Stratum end
```

Rules of thumb:

- Name Stratum-only fields, methods, and locals with a `Stratum` prefix. It makes diffs self-documenting and avoids collisions on a vanilla rebase.
- Keep the marker reason short and concrete. "Stratum: faster" is useless. "Stratum: skip per-tick allocation, see #42" is useful.
- Do not tag every single line in a block. One marker per logical change is enough.

## What not to patch

- Generated code. Anything under `obj/`, `bin/`, or marked auto-generated.
- Files that only changed because the decompiler emitted them slightly differently between runs. If your diff is whitespace, reordered usings, or added `this.` prefixes, drop it.
- Vanilla bugs that already have a fix upstream. Wait for the next vanilla release.
- Anything that requires a custom client. Stratum stays compatible with the stock client.

## Style

- Tabs for indentation.
- File-scoped namespaces in new files.
- `internal` by default. Make things `public` only when something outside the assembly needs them.
- Match the surrounding style when editing vanilla. Do not reformat the file.
- No nullable annotations in files that don't have `#nullable enable`.
- Plain English in comments. No marketing voice. No em dashes.

## Testing

Compilation is not enough. Before opening a PR:

1. `dotnet build VintageStory.slnx -c Release` is green with zero warnings on files you touched.
2. Run the smoke test: `make smoke` (or `bash scripts/smoke-test.sh` / `.\scripts\smoke-test.ps1`). It builds, boots the server, waits for RunGame, and checks for fatal errors.
3. If your change touches a hot path (entity ticking, chunk IO, packet handling), get a before/after measurement. Server timings, frame profiler output, or a sampling profiler are all fine. "Feels faster" is not.

## Commits

- One logical change per commit.
- Subject line under 72 chars, imperative mood. "Add async chunk save", not "Added" or "Adds".
- Reference issues with `Fixes #123` when relevant.

## Versioning and releases

Stratum versions are `<vs-version>-stratum.<rev>[-<pre>]`:

| Part | Meaning |
| --- | --- |
| `vs-version` | The Vintage Story release this build patches (e.g. `1.22.3`). Matches `StratumInfo.BaseGameVersion` and `forks.json`. |
| `rev` | Stratum revision on top of that base. Starts at `1` and increments per public release. Resets to `1` when the base game version changes. |
| `pre` | Optional prerelease suffix: `rc.1`, `beta.2`, `dev`. Omitted for stable releases. |

Examples: `1.22.3-stratum.1`, `1.22.3-stratum.2-rc.1`, `1.23.0-stratum.1`.

Release tags are the version prefixed with `v`: `v1.22.3-stratum.1`. Pushing a `v*` tag triggers `.github/workflows/release.yml`, which derives the version from the tag and passes it to `dotnet publish` as `Version` / `InformationalVersion`. At runtime `StratumInfo.Version` reads the assembly's informational version when present and falls back to the constants in [StratumInfo.cs](sources/VintagestoryLib/Vintagestory/Server/StratumInfo.cs).

For development builds the constants are authoritative. Bump `StratumRevision` and clear `PreRelease` in the same PR that ships a release.

## Pull requests

Open the PR against `indev`. The template lists the checks. Don't strip them.

In the description, say what changed and why. For perf work, include the numbers.

If your PR sits without review for a week, ping it in [Discord](https://discord.gg/pd24fawhsD).

## Reporting bugs

Use the issue templates. They ask for:

- Stratum version (printed in the startup banner, also `StratumInfo.Version`).
- Base game version.
- OS and .NET runtime version.
- For bugs: steps to reproduce, expected vs actual, server log.
- For crashes: full stack trace, what the server was doing, mod list.
- For performance issues: hardware, player count, observed TPS or frame time, profiler output if you have it.

Before filing, confirm the problem does not also happen on a stock vanilla server. If it does, report it upstream.

Security issues do not go in the issue tracker. See [SECURITY.md](SECURITY.md).
