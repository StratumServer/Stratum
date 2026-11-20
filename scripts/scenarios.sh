#!/usr/bin/env bash
set -euo pipefail

# Runs the Atlas scenario suite in tests/StratumScenarios against a prepared install.
# Builds if the launcher is missing, materializes the install with one
# --stratum-prepare-only launch, then runs dotnet test with VINTAGE_STORY pointing at it.
# Uses $CONFIGURATION (Release by default), which make scenarios passes along.
# Extra arguments go to dotnet test, for example: bash scripts/scenarios.sh --filter BootScenarios

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
framework="$(sed -n 's:.*<FrameworkVersion>\(.*\)</FrameworkVersion>.*:\1:p' "$repo_root/Directory.Build.props" | head -n 1)"
# The launcher materializes the vanilla install and the patched overlay into its own
# output directory, which is not configurable (AppContext.BaseDirectory), so that is
# what VINTAGE_STORY has to point at.
server_dir="$repo_root/StratumServer/bin/$configuration/${framework:-net10.0}"

cd "$repo_root"

# Build if the launcher is missing. Two passes, like the Makefile's build target: the
# embed pass points at sibling projects' bin output by raw path, so on a tree where
# those outputs do not exist yet it can race the projects that produce them.
if [[ ! -f "$server_dir/StratumServer.dll" ]]; then
  echo "Building $configuration..."
  dotnet build VintageStory.slnx -c "$configuration" --verbosity quiet
  dotnet build VintageStory.slnx -c "$configuration" -p:EmbedPatchedFiles=true --verbosity quiet
fi

# Stops right after the overlay, before any world is created, and exits non-zero if the
# build carried no patched files to overlay, which would leave a stale lib in place.
dotnet "$server_dir/StratumServer.dll" --stratum-prepare-only --stratum-no-banner

VINTAGE_STORY="$server_dir" dotnet test tests/StratumScenarios -c "$configuration" "$@"
