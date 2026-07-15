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
.\scripts\pack-release.ps1 -Rids win-x64,linux-x64 -OutDir release-out
```

Release zips contain `StratumServer` plus Stratum patched managed files. They do
not contain the full official Vintage Story server archive or files. On first run, the
launcher downloads and verifies the official archive, extracts it, writes the
patched files, and then starts the server.
