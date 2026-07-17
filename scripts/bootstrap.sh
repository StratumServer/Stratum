#!/usr/bin/env bash
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: scripts/bootstrap.sh [--version VERSION] [--server-archive PATH] [--client-lib-dir PATH] [--refresh]

Builds a clean working tree by laying down:
  1. Decompiled closed-source vanilla VS libraries from the official server archive.
  2. Open-source Anego forks cloned at the refs pinned in forks.json.
  3. Stratum patches and sources over those baselines.

Options:
  --version VERSION        Vintage Story server version to download. Default: 1.22.3
  --server-archive PATH    Existing vs_server_*.zip or .tar.gz archive to use.
  --client-lib-dir PATH    Optional full client Lib/ folder for client-only deps.
  --refresh               Force re-extract, re-decompile, and re-clone.
  -h, --help              Show this help.
EOF
}

version="1.22.3"
server_archive=""
client_lib_dir="${VS_CLIENT_LIB_DIR:-}"
refresh=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      version="${2:?--version requires a value}"
      shift 2
      ;;
    --server-archive|--server-zip)
      server_archive="${2:?$1 requires a value}"
      shift 2
      ;;
    --client-lib-dir)
      client_lib_dir="${2:?--client-lib-dir requires a value}"
      shift 2
      ;;
    --refresh)
      refresh=1
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

require_cmd() {
  local cmd="$1"
  if ! command -v "$cmd" >/dev/null 2>&1; then
    echo "Missing required command: $cmd" >&2
    exit 1
  fi
}

install_ilspycmd_if_missing() {
  if command -v ilspycmd >/dev/null 2>&1; then
    return
  fi

  local dotnet_tools="$HOME/.dotnet/tools"
  if [[ -x "$dotnet_tools/ilspycmd" ]]; then
    export PATH="$dotnet_tools:$PATH"
    return
  fi

  # Install the pinned version from .config/dotnet-tools.json when available.
  local manifest="$repo_root/.config/dotnet-tools.json"
  if [[ -f "$manifest" ]]; then
    local pinned_version
    pinned_version=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['tools']['ilspycmd']['version'])" "$manifest" 2>/dev/null)
    if [[ -n "$pinned_version" ]]; then
      echo "Installing ilspycmd $pinned_version (from tool manifest)"
      dotnet tool install -g ilspycmd --version "$pinned_version" >/dev/null
      export PATH="$dotnet_tools:$PATH"
      return
    fi
  fi

  echo "Installing ilspycmd (no manifest found, using latest)"
  dotnet tool install -g ilspycmd >/dev/null
  export PATH="$dotnet_tools:$PATH"
}

extract_archive() {
  local archive="$1"
  local dest="$2"

  mkdir -p "$dest"
  case "$archive" in
    *.tar.gz|*.tgz)
      tar -xzf "$archive" -C "$dest"
      ;;
    *.zip)
      if command -v unzip >/dev/null 2>&1; then
        unzip -q "$archive" -d "$dest"
      else
    python3 - "$archive" "$dest" <<'PY'
import sys
import zipfile

with zipfile.ZipFile(sys.argv[1]) as archive:
    archive.extractall(sys.argv[2])
PY
      fi
      ;;
    *)
      echo "Unsupported server archive extension: $archive" >&2
      exit 1
      ;;
  esac
}

normalize_lang_version() {
  local dir="$1"
  find "$dir" -maxdepth 1 -type f -name '*.csproj' -print0 |
    while IFS= read -r -d '' csproj; do
      perl -0pi -e 's#<LangVersion>15\.0</LangVersion>#<LangVersion>latest</LangVersion>#g' "$csproj"
    done
}

normalize_lf() {
  local dir="$1"
  find "$dir" -type f \( \
    -name '*.cs' -o \
    -name '*.csproj' -o \
    -name '*.json' -o \
    -name '*.xml' -o \
    -name '*.props' -o \
    -name '*.targets' \
  \) -print0 |
    while IFS= read -r -d '' file; do
      perl -0pi -e 's/\r\n/\n/g' "$file"
    done
}

copy_tree_fresh() {
  local src="$1"
  local dst="$2"
  rm -rf "$dst"
  mkdir -p "$(dirname "$dst")"
  cp -a "$src" "$dst"
}

download_server_archive() {
  local cache_dir="$1"
  mkdir -p "$cache_dir"

  # The manifest is passed as a file: it is over 128 KiB and Linux caps a single
  # exec argument at MAX_ARG_STRLEN (32 pages), so passing it through argv fails
  # with "Argument list too long".
  local manifest_file="$cache_dir/.stable-unstable.json"
  curl -L --fail --silent https://api.vintagestory.at/stable-unstable.json -o "$manifest_file"
  local archive_info
  archive_info="$(python3 - "$version" "$manifest_file" <<'PY'
import json
import sys

version = sys.argv[1]
with open(sys.argv[2], "r", encoding="utf-8") as fh:
    data = json.load(fh)
try:
    entry = data[version]["linuxserver"]
except KeyError as exc:
    raise SystemExit(f"server archive not found in Anego manifest for linuxserver {version}") from exc

print(entry["filename"])
print(entry["urls"]["cdn"])
print(entry["md5"])
PY
)"
  local archive_name url md5
  archive_name="$(printf '%s\n' "$archive_info" | sed -n '1p')"
  url="$(printf '%s\n' "$archive_info" | sed -n '2p')"
  md5="$(printf '%s\n' "$archive_info" | sed -n '3p')"
  local archive_path="$cache_dir/$archive_name"
  if [[ -f "$archive_path" ]]; then
    local actual_md5
    actual_md5="$(python3 - "$archive_path" <<'PY'
import hashlib
import sys

with open(sys.argv[1], "rb") as file:
    print(hashlib.md5(file.read()).hexdigest())
PY
)"
    if [[ "${actual_md5,,}" == "${md5,,}" ]]; then
      echo "Using cached $archive_path" >&2
      printf '%s\n' "$archive_path"
      return
    fi
    echo "Cached archive failed checksum, downloading a fresh copy" >&2
    rm -f "$archive_path"
  fi

  echo "Downloading $url" >&2
  curl -L --fail --output "$archive_path" "$url"
  local actual_md5
  actual_md5="$(python3 - "$archive_path" <<'PY'
import hashlib
import sys

with open(sys.argv[1], "rb") as file:
    print(hashlib.md5(file.read()).hexdigest())
PY
)"
  if [[ "${actual_md5,,}" != "${md5,,}" ]]; then
    rm -f "$archive_path"
    echo "Downloaded server archive failed MD5 verification: $archive_name" >&2
    exit 1
  fi
  printf '%s\n' "$archive_path"
}

copy_optional_client_libs() {
  local src_dir="$1"
  local dst_dir="$2"

  if [[ -z "$src_dir" ]]; then
    return
  fi
  if [[ ! -d "$src_dir" ]]; then
    echo "Client lib dir not found: $src_dir" >&2
    exit 1
  fi

  mkdir -p "$dst_dir"
  local dlls=(
    OpenTK.Graphics.dll
    csogg.dll
    csvorbis.dll
    xplatforminterface.dll
  )

  for dll in "${dlls[@]}"; do
    if [[ -f "$src_dir/$dll" ]]; then
      cp -f "$src_dir/$dll" "$dst_dir/"
    fi
  done
}

require_cmd dotnet
require_cmd git
require_cmd find
require_cmd perl
require_cmd python3
require_cmd tar
require_cmd curl

cd "$repo_root"

lib_projects=("VintagestoryLib:baseline/VintagestoryLib:VintagestoryLib.dll" "VintagestoryServer:baseline/VintagestoryServer:VintagestoryServer.dll")
vanilla_dir="$repo_root/.vanilla"
baseline_dir="$repo_root/.baseline"
zip_cache_dir="$repo_root/.vanilla-zips"

if [[ "$refresh" == "1" ]]; then
  rm -rf "$vanilla_dir" "$baseline_dir"
fi

if [[ -z "$server_archive" ]]; then
  server_archive="$(download_server_archive "$zip_cache_dir")"
fi

if [[ ! -d "$vanilla_dir" ]]; then
  if [[ ! -f "$server_archive" ]]; then
    echo "Server archive not found: $server_archive" >&2
    exit 1
  fi
  echo "Extracting $server_archive"
  extract_archive "$server_archive" "$vanilla_dir"
fi

copy_optional_client_libs "$client_lib_dir" "$vanilla_dir/Lib"

install_ilspycmd_if_missing

for entry in "${lib_projects[@]}"; do
  IFS=':' read -r project work_rel dll <<<"$entry"
  dll_path="$(find "$vanilla_dir" -type f -name "$dll" -print -quit)"
  if [[ -z "$dll_path" ]]; then
    echo "Skipping $dll, not found in archive" >&2
    continue
  fi

  out="$baseline_dir/$project"
  if [[ ! -d "$out" || "$refresh" == "1" ]]; then
    echo "Decompiling $dll into $out"
    rm -rf "$out"
    mkdir -p "$out"
    ilspycmd "$dll_path" --project -o "$out" >/dev/null
    normalize_lang_version "$out"
  fi

  copy_tree_fresh "$out" "$repo_root/$work_rel"
done

forks_file="$repo_root/forks.json"
if [[ -f "$forks_file" ]]; then
  mapfile -t forks < <(
    python3 - "$forks_file" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as file:
    data = json.load(file)

for fork in data.get("forks", []):
    print(f"{fork['name']}\t{fork['url']}\t{fork['ref']}")
PY
  )

  for fork in "${forks[@]}"; do
    IFS=$'\t' read -r name url ref <<<"$fork"
    base="$baseline_dir/$name"

    if [[ ! -d "$base" || "$refresh" == "1" ]]; then
      rm -rf "$base"
      echo "Cloning $url at $ref into $base"
      git clone --quiet "$url" "$base"
      git -C "$base" checkout --quiet "$ref"
      rm -rf "$base/.git"
      normalize_lf "$base"
    fi

    copy_tree_fresh "$base" "$repo_root/$name"
  done
fi

patches_dir="$repo_root/patches"
vanilla_patch_projects=("VintagestoryLib" "VintagestoryServer")
if [[ -d "$patches_dir" ]]; then
  failed=()
  while IFS= read -r -d '' patch; do
    rel="${patch#$repo_root/}"
    top_proj="$(printf '%s\n' "$rel" | cut -d/ -f2)"
    apply_args=(apply --whitespace=nowarn)

    for vanilla_project in "${vanilla_patch_projects[@]}"; do
      if [[ "$top_proj" == "$vanilla_project" ]]; then
        apply_args+=(--directory=baseline)
        break
      fi
    done

    echo "Applying $rel"
    if ! output="$(git "${apply_args[@]}" "$patch" 2>&1)"; then
      failed+=("$rel")
      while IFS= read -r line; do
        [[ -n "$line" ]] && echo "  $line"
      done <<<"$output"
    fi
  done < <(find "$patches_dir" -type f -name '*.patch' -print0)

  if [[ "${#failed[@]}" -gt 0 ]]; then
    echo
    echo "${#failed[@]} patch(es) failed to apply:" >&2
    printf '  %s\n' "${failed[@]}" >&2
    echo "Fix the conflicts in the working tree, then run scripts/extract-patches.sh." >&2
  fi
else
  echo "No patches/ directory, skipping patch step."
fi

sources_dir="$repo_root/sources"
if [[ -d "$sources_dir" ]]; then
  while IFS= read -r -d '' project_dir; do
    project="$(basename "$project_dir")"
    dst="$repo_root/$project"

    for vanilla_project in "${vanilla_patch_projects[@]}"; do
      if [[ "$project" == "$vanilla_project" ]]; then
        dst="$repo_root/baseline/$project"
        break
      fi
    done

    if [[ ! -d "$dst" ]]; then
      echo "Warning: sources/$project has no matching working folder; skipping." >&2
      continue
    fi

    while IFS= read -r -d '' src; do
      rel="${src#$project_dir/}"
      target="$dst/$rel"
      mkdir -p "$(dirname "$target")"
      cp -f "$src" "$target"
    done < <(find "$project_dir" -type f -print0)

    echo "Synced sources/$project into ${dst#$repo_root/}/"
  done < <(find "$sources_dir" -mindepth 1 -maxdepth 1 -type d -print0)
fi

echo
echo "Bootstrap complete. Run: dotnet build VintageStory.slnx -c Release"
