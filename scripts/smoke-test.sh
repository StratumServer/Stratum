#!/usr/bin/env bash
set -euo pipefail

# Boot-tests the Stratum server. Builds Release, starts the server, monitors
# progress through startup phases, and verifies it reaches RunGame without
# fatal errors. Detects stalls by checking if the server made progress within
# a patience window rather than using a hard timeout.
#
# Environment overrides:
#   SMOKE_TEST_PATIENCE - seconds without progress before declaring stall (default: 60)
#   SMOKE_TEST_PORT     - server port (default: random ephemeral)
#   SMOKE_TEST_DATA     - server dataPath (default: temp dir, cleaned on exit)

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
# Server binary: prefer VintagestoryServer (runtime entry point after first bootstrap),
# fall back to StratumServer (first-run launcher that downloads vanilla).
server_dir="$repo_root/StratumServer/bin/Release/net10.0"
if [[ -f "$server_dir/VintagestoryServer" ]]; then
  server_bin="$server_dir/VintagestoryServer"
elif [[ -f "$server_dir/StratumServer" ]]; then
  server_bin="$server_dir/StratumServer"
else
  server_bin=""
fi
patience="${SMOKE_TEST_PATIENCE:-60}"
port="${SMOKE_TEST_PORT:-0}"

# Data path handling.
own_data=1
if [[ -n "${SMOKE_TEST_DATA:-}" ]]; then
  data_path="$SMOKE_TEST_DATA"
  own_data=0
else
  data_path="$(mktemp -d)"
fi
mkdir -p "$data_path"

cleanup() {
  if [[ "$own_data" == "1" && -d "$data_path" ]]; then
    rm -rf "$data_path"
  fi
}
trap cleanup EXIT

cd "$repo_root"

# Build if binary is missing.
if [[ -z "$server_bin" || ! -f "$server_bin" ]]; then
  echo "Building Release..."
  dotnet build VintageStory.slnx -c Release --verbosity quiet
  # Re-detect after build.
  if [[ -f "$server_dir/VintagestoryServer" ]]; then
    server_bin="$server_dir/VintagestoryServer"
  elif [[ -f "$server_dir/StratumServer" ]]; then
    server_bin="$server_dir/StratumServer"
  fi
fi

if [[ -z "$server_bin" || ! -f "$server_bin" ]]; then
  echo "FAIL: no server binary found in $server_dir" >&2
  exit 1
fi

# Pick an ephemeral port.
if [[ "$port" == "0" ]]; then
  port=$(python3 -c "import socket; s=socket.socket(); s.bind(('',0)); print(s.getsockname()[1]); s.close()")
fi

echo "Smoke test: port=$port patience=${patience}s data=$data_path"

log_file="$data_path/smoke-test.log"
"$server_bin" --dataPath "$data_path" --port "$port" >"$log_file" 2>&1 &
server_pid=$!

last_line_count=0
since_progress=0
last_phase="(starting)"

while true; do
  sleep 2

  # Server died on its own.
  if ! kill -0 "$server_pid" 2>/dev/null; then
    break
  fi

  # Check if reached RunGame.
  if grep -q "Entering runphase RunGame" "$log_file" 2>/dev/null; then
    sleep 5
    break
  fi

  # Check for fatal error (early exit).
  if grep -qi "^\S.* \[Server Fatal\]" "$log_file" 2>/dev/null; then
    break
  fi

  # Progress detection: is the log still growing?
  current_lines=$(wc -l < "$log_file" 2>/dev/null || echo "0")
  if [[ "$current_lines" -gt "$last_line_count" ]]; then
    last_line_count=$current_lines
    since_progress=0
    # Track current phase for reporting.
    phase=$(grep "Entering runphase" "$log_file" 2>/dev/null | tail -1 | sed 's/.*runphase //' || true)
    if [[ -n "$phase" ]]; then
      last_phase="$phase"
    fi
  else
    ((since_progress+=2))
  fi

  # Stall detection.
  if [[ $since_progress -ge $patience ]]; then
    echo "STALL: no log output for ${patience}s (last phase: $last_phase)" >&2
    break
  fi
done

# Shut down.
if kill -0 "$server_pid" 2>/dev/null; then
  kill -TERM "$server_pid" 2>/dev/null || true
  wait "$server_pid" 2>/dev/null || true
fi

# Evaluate result.
reached_rungame=0
has_fatal=0

if grep -q "Entering runphase RunGame" "$log_file" 2>/dev/null; then
  reached_rungame=1
fi
if grep -qi "Fatal\|Unhandled exception" "$log_file" 2>/dev/null; then
  has_fatal=1
fi

if [[ "$reached_rungame" == "1" && "$has_fatal" == "0" ]]; then
  echo "PASS: server reached RunGame, no fatal errors. (last phase: $last_phase)"
  exit 0
fi

echo "FAIL:" >&2
if [[ "$reached_rungame" == "0" ]]; then
  echo "  Server did not reach RunGame (last phase: $last_phase)." >&2
fi
if [[ "$has_fatal" == "1" ]]; then
  echo "  Fatal errors:" >&2
  grep -i "Fatal\|Unhandled exception" "$log_file" | head -3 | sed 's/^/    /' >&2
fi
echo "  Log: $log_file" >&2
exit 1
