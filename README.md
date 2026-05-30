# Stratum

[![Release](https://img.shields.io/github/v/release/trevorftp/Stratum?display_name=tag&sort=semver&logo=github&label=release)](https://github.com/trevorftp/Stratum/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/trevorftp/Stratum/release.yml?logo=githubactions&logoColor=white&label=build)](https://github.com/trevorftp/Stratum/actions/workflows/release.yml)
[![License](https://img.shields.io/github/license/trevorftp/Stratum?label=license)](LICENSE)
[![Discord](https://img.shields.io/badge/chat-on%20discord-5865F2?logo=discord&logoColor=white)](https://discord.gg/pd24fawhsD)
[![Stars](https://img.shields.io/github/stars/trevorftp/Stratum?logo=github&style=flat)](https://github.com/trevorftp/Stratum/stargazers)

Stratum is a high-performance, server-side fork of [Vintage Story](https://www.vintagestory.at).

## Install

1. Grab the latest `stratum-<version>-<rid>.zip` from [Releases](https://github.com/trevorftp/Stratum/releases).
2. Extract it.
3. Run `StratumServer.exe` (Windows) or `dotnet StratumServer.dll` (Linux).

First launch downloads and unpacks the matching vanilla server build from `cdn.vintagestory.at`.

## Flags

| Flag | Effect |
| --- | --- |
| `--stratum-version` | Print version info and exit |
| `--stratum-help` | Print launcher options and exit |
| `--stratum-refresh` | Re-download and re-extract vanilla assets |
| `--stratum-skip-bootstrap` | Skip the first-run download |
| `--stratum-no-banner` | Suppress the startup banner |

Anything else is forwarded to the server (`--port`, `--dataPath`, ...).

## Build

Requires the .NET 10 SDK, PowerShell 5.1+, and `git`.

```powershell
git clone https://github.com/trevorftp/Stratum.git
cd Stratum
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

`bootstrap.ps1` downloads the targeted vanilla server zip, decompiles
`VintagestoryLib`, `VintagestoryServer`, and `cairo-sharp` into `baseline/`,
clones the forks pinned in [forks.json](forks.json), applies every patch in
`patches/`, and drops Stratum-only files from `sources/` into place.

After bootstrap, every patched file carries a `// Stratum - <reason>` marker
so edits are easy to find. Edit, build, then run
`.\scripts\extract-patches.ps1` to regenerate the diffs.

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md).

## License

[LICENSE](LICENSE).
