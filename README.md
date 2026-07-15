# Stratum

[![Release](https://img.shields.io/github/v/release/StratumServer/Stratum?display_name=tag\&sort=semver\&logo=github\&label=release)](https://github.com/StratumServer/Stratum/releases)
[![Build](https://img.shields.io/github/actions/workflow/status/StratumServer/Stratum/release.yml?logo=githubactions\&logoColor=white\&label=build)](https://github.com/StratumServer/Stratum/actions/workflows/release.yml)
[![License](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
[![Discord](https://img.shields.io/badge/chat-on%20discord-5865F2?logo=discord\&logoColor=white)](https://discord.gg/pd24fawhsD)
[![Stars](https://img.shields.io/github/stars/StratumServer/Stratum?logo=github\&style=flat)](https://github.com/StratumServer/Stratum/stargazers)
[![Support on OpenCollective](https://img.shields.io/badge/Support-OpenCollective-7FADF2?logo=opencollective\&logoColor=white)](https://opencollective.com/stratum)

Stratum is a high-performance, server-side fork of [Vintage Story](https://www.vintagestory.at).

## Sponsors

Big thanks to everyone supporting Stratum.

Financial support now goes through [OpenCollective](https://opencollective.com/stratum), which gives the project a cleaner and more transparent way to handle donations and project costs.

Older Ko-fi supporters are still listed below because they helped Stratum early on.

### Supporters

<div align="center">

<table>
  <tr>
    <td align="center" width="140">
      <a href="https://ko-fi.com/imtsubaki">
        <img
          src="https://storage.ko-fi.com/cdn/useruploads/2e1bd9ac-27b3-4473-90c5-85715da416d3_c9a1e3e3-4f81-40b0-93b3-9ffd57da54b7.png"
          width="96"
          height="96"
          alt="Algernon"
        />
      </a>
      <br />
      <strong>Algernon</strong>
    </td>
    <td align="center" width="140">
      <a href="https://ko-fi.com/imtsubaki">
        <img
          src="https://storage.ko-fi.com/cdn/useruploads/08030810-3fe7-497e-a282-001ae6eeca2d_c83f8475-28b8-49b6-82bf-7b2db24edfd7.png"
          width="96"
          height="96"
          alt="PHoenixOPHury"
        />
      </a>
      <br />
      <strong>PHoenixOPHury</strong>
    </td>
    <td align="center" width="140">
      <a href="https://ko-fi.com/imtsubaki">
        <img
          src="https://storage.ko-fi.com/cdn/useruploads/db85e196-0575-44a2-8e23-2610d11753ac_a9cd6dc4-5584-4aa4-89a0-385ffa345451.png"
          width="96"
          height="96"
          alt="PARTIZAN_N"
        />
      </a>
      <br />
      <strong>PARTIZAN_N</strong>
    </td>
  </tr>
</table>

<sub>Thanks for helping keep Stratum moving.</sub>

</div>

## Server Stats
<div align="center">
  
![Stratum Network](https://my.stratumvs.dev/stratum-stats.php?graph)  
<sub>Statistics shown are as of v1.22.3-stratum.15-indev.1 and on.</sub>

</div>


## Install

1. Grab the latest `stratum-<version>-<rid>.zip` from [Releases](https://github.com/StratumServer/Stratum/releases).
2. Extract it.
3. Run `StratumServer.exe` on Windows or `./StratumServer` on Linux.

First launch gets the official Vintage Story server archive through Anego's
release manifest, verifies it, unpacks it, and writes Stratum's patched files on
top. Later launches use the prepared install unless the Stratum or base version
changes.

## Flags

| Flag                       | Effect                                    |
| -------------------------- | ----------------------------------------- |
| `--stratum-version`        | Print version info and exit               |
| `--stratum-help`           | Print launcher options and exit           |
| `--stratum-refresh`        | Download and prepare the base server again |
| `--stratum-skip-bootstrap` | Skip first-run prepare work                |
| `--stratum-prepare-only`   | Prepare the install, then exit             |
| `--stratum-no-banner`      | Suppress the startup banner               |

Anything else is forwarded to the server, such as `--port` or `--dataPath`.

## Build

Requires the .NET 10 SDK and `git`. Linux/macOS also need `bash`, `python3`, and `curl`.

```bash
# Linux / macOS
git clone https://github.com/StratumServer/Stratum.git
cd Stratum
make build        # bootstrap + build in one step
make smoke        # build + boot-test the server
```

```powershell
# Windows (PowerShell)
git clone https://github.com/StratumServer/Stratum.git
cd Stratum
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
.\scripts\smoke-test.ps1    # boot-test the server
```

`bootstrap.ps1` resolves the targeted official server archive through Anego's
release manifest, verifies it, decompiles `VintagestoryLib` and
`VintagestoryServer` into `baseline/`, clones the forks pinned in
[forks.json](forks.json), applies every patch in `patches/`, and drops
Stratum-only files from `sources/` into place.

After bootstrap, every patched file carries a `// Stratum:` marker so edits
are easy to find. Edit, build, then run `.\scripts\extract-patches.ps1` to
regenerate the diffs. Full workflow in [CONTRIBUTING.md](CONTRIBUTING.md).

See [SECURITY.md](SECURITY.md) for reporting exploits.

## Versions

Stratum builds are tagged `v<vs-version>-stratum.<rev>`, for example `v1.22.3-stratum.1`.

The `vs-version` half always matches the Vintage Story release the server was built against. The `stratum.<rev>` half increments per public Stratum release on that base.

See [CONTRIBUTING.md](CONTRIBUTING.md#versioning-and-releases) for the full scheme.

## License

[MIT](LICENSE).
