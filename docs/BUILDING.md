# Building Stratum

## Prerequisites

* Windows 10 or later (Linux support is planned, not tested).
* .NET 10 SDK.
* PowerShell 7 or later.
* git.
* About 2 GB of free disk space for the vanilla source tree and build output.

## First build

```powershell
git clone https://github.com/trevorftp/Stratum.git
cd Stratum
.\scripts\bootstrap.ps1
dotnet build VintageStory.slnx -c Release
```

`bootstrap.ps1` does the following:

1. Downloads `vs_server_win-x64_<version>.zip` from `cdn.vintagestory.at` into
   `.vanilla-zips/`. Cached, so re-runs are quick.
2. Extracts the zip into `.vanilla/`.
3. Installs `ilspycmd` as a dotnet global tool if it is not already on PATH.
4. Decompiles `VintagestoryLib.dll`, `VintagestoryServer.dll`, and `cairo-sharp.dll`
   into `.baseline/<project>/`.
5. Clones the open-source Anego forks (`vsapi`, `vsessentialsmod`, `vssurvivalmod`)
   at the commits pinned in `forks.json` into `.baseline/<project>/`.
6. Copies every baseline into the matching working folder at the repo root.
7. Applies every `.patch` file under `patches/` with `git apply --3way`.

After that the solution has every project it needs and `dotnet build` works.

### Targeting a different version

```powershell
.\scripts\bootstrap.ps1 -Version 1.22.3
```

### Using a zip you already have

```powershell
.\scripts\bootstrap.ps1 -ServerZip C:\downloads\vs_server_win-x64_1.22.3.zip
```

### Re-bootstrapping after an upstream version bump

```powershell
.\scripts\bootstrap.ps1 -Version 1.22.4 -Refresh
```

`-Refresh` wipes `.vanilla/` and `.baseline/` so the new release gets decompiled clean.
For the open-source forks, bump the `ref` values in `forks.json` to the matching
upstream commits before running with `-Refresh`. Expect some patches to fail. Fix them
in the working tree and run `scripts\extract-patches.ps1` to regenerate the patch files.

## Producing patches

After editing anything inside `VintagestoryLib/`, `VintagestoryServer/`, or `Cairo/`:

```powershell
.\scripts\extract-patches.ps1
```

That diffs each file against the corresponding baseline and writes or updates files
under `patches/<project>/<relative-path>.patch`. Commit those.

## Running the server

The solution outputs to `<project>\bin\Release\net10.0\`. For a real install you still
need the vanilla assets, native libs, and base mods. The `BuildStratumOutputs` and
`DeployStratumServer` MSBuild targets in `StratumServer.csproj` handle that locally if
you set `VSInstall` and `StratumInstall` in a `*.local.props` file at the repo root.

## Troubleshooting

* `ilspycmd` install fails: install it manually with `dotnet tool install -g ilspycmd`
  and make sure `%USERPROFILE%\.dotnet\tools` is on PATH.
* A patch fails to apply: usually means upstream changed the surrounding code. Open
  the working file, fix the conflict, then re-run `extract-patches.ps1`.
* `dotnet build` reports missing types from `VintagestoryLib`: the bootstrap step did
  not complete. Re-run `scripts\bootstrap.ps1` and watch for errors.
