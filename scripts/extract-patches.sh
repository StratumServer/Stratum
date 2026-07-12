#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"

baseline_dir="$repo_root/.baseline"
patches_dir="$repo_root/patches"
sources_dir="$repo_root/sources"

if [[ ! -d "$baseline_dir" ]]; then
  echo "No .baseline/ found. Run scripts/bootstrap.sh first." >&2
  exit 1
fi

export LC_ALL=C.UTF-8

exclude_segments=(bin obj .vs .git Generated DevMenu ImGui)
vanilla_projects=(VintagestoryLib VintagestoryServer)

patches_written=0
sources_written=0
cleared=0

declare -A kept_sources

is_vanilla_project() {
  local proj="$1"
  local item
  for item in "${vanilla_projects[@]}"; do
    [[ "$item" == "$proj" ]] && return 0
  done
  return 1
}

is_excluded() {
  local rel="$1"
  local segment
  IFS='/' read -r -a parts <<<"${rel//\\//}"
  for segment in "${parts[@]}"; do
    local excluded
    for excluded in "${exclude_segments[@]}"; do
      [[ "$segment" == "$excluded" ]] && return 0
    done
  done
  return 1
}

normalize_text() {
  local src="$1"
  local dst="$2"
  mkdir -p -- "$(dirname -- "$dst")"
  sed '1s/^\xEF\xBB\xBF//' "$src" | perl -pe 's/\r\n/\n/g; s/\r/\n/g' > "$dst"
}

write_patch() {
  local base_file="$1"
  local work_file="$2"
  local rel_path="$3"
  local patch_file="$4"
  local tmp_base tmp_work tmp_diff status

  tmp_base="$(mktemp)"
  tmp_work="$(mktemp)"
  tmp_diff="$(mktemp)"
  normalize_text "$base_file" "$tmp_base"
  normalize_text "$work_file" "$tmp_work"

  set +e
  git --no-pager -c core.safecrlf=false diff --no-color --no-index -U5 -- "$tmp_base" "$tmp_work" > "$tmp_diff"
  status=$?

  if [[ "$status" -eq 1 ]]; then
    mkdir -p -- "$(dirname -- "$patch_file")"
    sed \
      -e "s#^diff --git .*\$#diff --git a/$rel_path b/$rel_path#" \
      -e "s#^--- .*\$#--- a/$rel_path#" \
      -e "s#^+++ .*\$#+++ b/$rel_path#" \
      "$tmp_diff" > "$patch_file"
    ((patches_written += 1))
    echo "  $rel_path"
  elif [[ "$status" -ne 0 ]]; then
    cat "$tmp_diff" >&2
    rm -f -- "$tmp_base" "$tmp_work" "$tmp_diff"
    exit "$status"
  fi

  rm -f -- "$tmp_base" "$tmp_work" "$tmp_diff"
  return "$status"
}

sync_new_file() {
  local proj="$1"
  local work_rel="$2"
  local src_file="$3"
  local dst="$sources_dir/$proj/$work_rel"

  normalize_text "$src_file" "$dst"
  kept_sources["$(realpath "$dst")"]=1
  ((sources_written += 1))
}

clear_stale_patches() {
  local proj="$1"
  local base_root="$2"
  local work_root="$3"
  local proj_patch_dir="$patches_dir/$proj"

  [[ -d "$proj_patch_dir" ]] || return 0

  while IFS= read -r -d '' patch_file; do
    local rel_patch rel_source base_file work_file
    rel_patch="${patch_file#$proj_patch_dir/}"
    rel_source="${rel_patch%.patch}"

    if is_excluded "$rel_source"; then
      rm -f -- "$patch_file"
      ((cleared += 1))
      continue
    fi

    base_file="$base_root/$rel_source"
    work_file="$work_root/$rel_source"

    if [[ -f "$base_file" && -f "$work_file" ]]; then
      local tmp_base tmp_work
      tmp_base="$(mktemp)"
      tmp_work="$(mktemp)"
      normalize_text "$base_file" "$tmp_base"
      normalize_text "$work_file" "$tmp_work"
      if cmp -s "$tmp_base" "$tmp_work"; then
        rm -f -- "$patch_file"
        ((cleared += 1))
      fi
      rm -f -- "$tmp_base" "$tmp_work"
      continue
    fi

    if [[ ! -f "$base_file" && ! -f "$work_file" ]]; then
      rm -f -- "$patch_file"
      ((cleared += 1))
    fi
  done < <(find "$proj_patch_dir" -type f -name '*.patch' -print0)
}

clear_stale_sources() {
  local proj="$1"
  local proj_src_dir="$sources_dir/$proj"

  [[ -d "$proj_src_dir" ]] || return 0

  while IFS= read -r -d '' source_file; do
    local full
    full="$(realpath "$source_file")"
    if [[ -z "${kept_sources[$full]+x}" ]]; then
      rm -f -- "$source_file"
      ((cleared += 1))
    fi
  done < <(find "$proj_src_dir" -type f -name '*.cs' -print0)

  while IFS= read -r -d '' dir; do
    rmdir --ignore-fail-on-non-empty "$dir" 2>/dev/null || true
  done < <(find "$proj_src_dir" -depth -type d -print0)
}

project_names_from_forks() {
  local forks_file="$repo_root/forks.json"
  [[ -f "$forks_file" ]] || return 0

  if command -v jq >/dev/null 2>&1; then
    jq -r '.forks[]?.name' "$forks_file"
  else
    python3 - "$forks_file" <<'PY'
import json
import sys
with open(sys.argv[1], encoding="utf-8") as f:
    data = json.load(f)
for fork in data.get("forks", []):
    name = fork.get("name")
    if name:
        print(name)
PY
  fi
}

process_project() {
  local proj="$1"
  local work_root="$2"
  local base_root="$3"

  [[ -d "$work_root" && -d "$base_root" ]] || return 0

  echo -e "\e[36mExtracting patches for $proj\e[0m"

  while IFS= read -r -d '' file; do
    local rel base_file patch_file rel_patch status
    rel="${file#$work_root/}"
    rel="${rel//\\//}"
    is_excluded "$rel" && continue

    base_file="$base_root/$rel"
    patch_file="$patches_dir/$proj/$rel.patch"
    rel_patch="$proj/$rel"

    if [[ ! -f "$base_file" ]]; then
      sync_new_file "$proj" "$rel" "$file"
      echo -e "  $rel \e[1;33m(source)\e[0m"
      continue
    fi

    set +e
    write_patch "$base_file" "$file" "$rel_patch" "$patch_file"
    status=$?
    set -e

    if [[ "$status" -eq 0 && -f "$patch_file" ]]; then
      rm -f -- "$patch_file"
      ((cleared += 1))
    fi
  done < <(find "$work_root" -type f -name '*.cs' -print0)

  clear_stale_patches "$proj" "$base_root" "$work_root"
  clear_stale_sources "$proj"
}

cd "$repo_root"

while IFS= read -r proj; do
  [[ -n "$proj" ]] || continue
  process_project "$proj" "$repo_root/$proj" "$baseline_dir/$proj"
done < <(project_names_from_forks)

for proj in "${vanilla_projects[@]}"; do
  process_project "$proj" "$repo_root/baseline/$proj" "$baseline_dir/$proj"
done

echo "Wrote $patches_written patch(es), $sources_written source file(s); cleared $cleared stale entry(ies)."
