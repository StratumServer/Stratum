# Building Stratum

## Prerequisites

- .NET 10 SDK
- git
- PowerShell 7 or later on Windows
- bash, python3, curl, tar, and perl on Linux or macOS
- About 2 GB of free disk space

## First Build

Windows:

```powershell
git clone https://github.com/StratumServer/Stratum.git
cd Stratum
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

Linux and macOS:

```bash
git clone https://github.com/StratumServer/Stratum.git
cd Stratum
make build
```

Bootstrap does this:

1. Resolves the official server archive from Anego's release manifest.
2. Verifies the archive MD5 from the manifest.
3. Extracts the archive into `.vanilla/`.
4. Installs `ilspycmd` if needed.
5. Decompiles `VintagestoryLib.dll` and `VintagestoryServer.dll`.
6. Clones the open-source Anego forks pinned in `forks.json`.
7. Applies Stratum patches and copies `sources/` into the working tree.

## Different Base Version

```powershell
.\scripts\bootstrap.ps1 -Version 1.22.3
.\scripts\bootstrap.ps1 -Version 1.22.3 -Refresh
```

Linux and macOS:

```bash
scripts/bootstrap.sh --version 1.22.3
scripts/bootstrap.sh --version 1.22.3 --refresh
```

## Local Archive

```powershell
.\scripts\bootstrap.ps1 -ServerZip C:\downloads\vs_server_win-x64_1.22.3.zip
```

```bash
scripts/bootstrap.sh --server-archive ~/downloads/vs_server_linux-x64_1.22.3.tar.gz
```

## Producing Patches

After editing the working tree:

```powershell
.\scripts\extract-patches.ps1
```

```bash
scripts/extract-patches.sh
```

Commit the updated `patches/` and `sources/` files.

## Release Zips

```powershell
.\scripts\pack-release.ps1 -Rids win-x64,linux-x64,linux-arm64 -OutDir release-out
```

Release zips contain `StratumServer` plus Stratum patched managed files. They do
not contain the full official Vintage Story server archive or files. On first run, the
launcher downloads and verifies the official archive, extracts it, writes the
patched files, and then starts the server.

Every project except `StratumServer` builds AnyCPU, and the launcher is published
framework-dependent, so the only architecture-specific artifact is the apphost. The SDK
cross-targets it from an x64 host - `linux-arm64` needs no arm64 build machine.

## arm64

Anego publishes no `linux-arm64` server archive, so on an arm64 host the launcher still
downloads the official `linux-x64` archive for its assets and IL, then replaces the
architecture-specific natives (`libe_sqlite3.so`, `libSkiaSharp.so`, `libzstd.so`) with the
aarch64 builds from
[anegostudios/VintagestoryServerArm64](https://github.com/anegostudios/VintagestoryServerArm64).
Only `*.so` files are taken from that overlay: its managed and `VintagestoryServer` host files
lag the base game by a patch release or two, and Stratum ships its own apphost anyway.

The overlay asset for each Vintage Story minor version is pinned in `forks.json`
(`arm64NativeOverlays`), embedded into the assembly at build time, and verified against its
sha256 digest on download - resolving it needs no GitHub API call, and there is no
unauthenticated-fallback path that skips verification. Add an entry there (name, url, sha256 from
the release asset's `digest` field) when overlaying a new Vintage Story minor version; boot on
arm64 without one and the launcher fails with a clear error instead of guessing. The base game
version this overlay was applied for is recorded in `.stratum-arm64-natives`.
