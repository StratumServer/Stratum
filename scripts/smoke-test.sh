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
#   SMOKE_TEST_PROBE    - set to 0 to skip the console command probe (default: 1)

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/.." && pwd)"
# Server binary: prefer StratumServer, Stratum's own launcher, which applies
# VanillaBootstrap and PatchedFileOverlay before handing off to the game. A
# VintagestoryServer binary in this same directory is not something this repo
# builds; it is the vanilla archive's own entry point, copied in as a side
# effect of VanillaBootstrap's first-run overlay once StratumServer has run at
# least once. Preferring it here would silently boot-test unpatched vanilla
# code instead of this working tree.
server_dir="$repo_root/StratumServer/bin/Release/net10.0"
if [[ -f "$server_dir/StratumServer" ]]; then
  server_bin="$server_dir/StratumServer"
elif [[ -f "$server_dir/VintagestoryServer" ]]; then
  server_bin="$server_dir/VintagestoryServer"
else
  server_bin=""
fi
patience="${SMOKE_TEST_PATIENCE:-60}"
port="${SMOKE_TEST_PORT:-0}"

# Console command probe. After the server reaches RunGame these lines are piped into
# its stdin one at a time; the server runs them as console chat commands (a caller
# that always passes Stratum's own access check, see StratumCommandAccessCatalog) and
# writes each result to the log, so command registration and argument handling are
# covered without a game client. probe_expect is index-matched; an empty entry only
# asserts that the command was dispatched and did not trip the registration error.
probe_commands=(
  "/kitedit list"
  "/kitedit create starter_kit 1"
  "/kitedit create starter_kit"
  "/kitedit preview starter_kit"
  "/kitedit rename starter_kit"
  "/kitedit list extra"
  "/kitedit create starter_kit 1 2"
  "/kit"
)
probe_expect=(
  "No kits exist yet."
  "/kitedit create takes 1 argument."
  "Only a connected player can snapshot a kit from their own inventory."
  "No kit named 'starter_kit'."
  "Usage: /kitedit rename"
  "/kitedit list takes no arguments."
  ""
  "Only a connected player can use /kit."
)

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
  exec 3>&- 2>/dev/null || true
  rm -f "$data_path/console.fifo" 2>/dev/null || true
  if [[ "$own_data" == "1" && -d "$data_path" ]]; then
    rm -rf "$data_path"
  fi
}
trap cleanup EXIT

cd "$repo_root"

# Build if binary is missing. -p:EmbedPatchedFiles=true is required so
# StratumServer actually carries this working tree's patched DLLs; without it
# PatchedFileOverlay has nothing to overlay and the server silently runs
# vanilla's unpatched VintagestoryLib.dll from the downloaded archive instead.
if [[ -z "$server_bin" || ! -f "$server_bin" ]]; then
  echo "Building Release..."
  dotnet build VintageStory.slnx -c Release -p:EmbedPatchedFiles=true --verbosity quiet
  # Re-detect after build, same preference as above.
  if [[ -f "$server_dir/StratumServer" ]]; then
    server_bin="$server_dir/StratumServer"
  elif [[ -f "$server_dir/VintagestoryServer" ]]; then
    server_bin="$server_dir/VintagestoryServer"
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

# The server's console reader executes whatever a single stdin read returns as one
# command, so the probe needs a live pipe it can write to one line at a time.
# Opened read-write so neither open() blocks on the other end.
console_fifo="$data_path/console.fifo"
mkfifo "$console_fifo"
exec 3<>"$console_fifo"

log_file="$data_path/smoke-test.log"
# Run from the server's own directory, not repo_root: Mono.Cecil's default assembly
# resolver searches the process's current directory, and modinfo scanning at boot
# fails to resolve VintagestoryAPI (silently dropping every mod, "game" included)
# when launched from anywhere else.
(cd "$server_dir" && exec "$server_bin" --dataPath "$data_path" --port "$port" <"$console_fifo" >"$log_file" 2>&1) &
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

log_contains() {
  grep -qF -- "$1" "$log_file" 2>/dev/null
}

probe_failures=""
probe_ran=0
if [[ "${SMOKE_TEST_PROBE:-1}" == "1" ]] \
  && kill -0 "$server_pid" 2>/dev/null \
  && grep -q "Entering runphase RunGame" "$log_file" 2>/dev/null; then
  probe_ran=1
  echo "Probing ${#probe_commands[@]} console command(s)..."
  for cmd in "${probe_commands[@]}"; do
    printf '%s\n' "$cmd" >&3
    # One second between writes: two lines that land in the same read would be
    # submitted to the server as a single bogus command.
    sleep 1
  done
  # Let the results reach the log before grepping.
  sleep 5

  for i in "${!probe_commands[@]}"; do
    cmd="${probe_commands[$i]}"
    if ! log_contains "Handling Console Command $cmd"; then
      probe_failures+="    not dispatched: $cmd"$'\n'
      continue
    fi
    expected="${probe_expect[$i]}"
    if [[ -n "$expected" ]] && ! log_contains "$expected"; then
      probe_failures+="    $cmd -> missing expected output: $expected"$'\n'
    fi
  done

  if log_contains "Incomplete command"; then
    probe_failures+="    command registration incomplete: \"Incomplete command\" appeared in the log"$'\n'
  fi
fi

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

if [[ "$reached_rungame" == "1" && "$has_fatal" == "0" && -z "$probe_failures" ]]; then
  if [[ "$probe_ran" == "1" ]]; then
    echo "PASS: server reached RunGame, no fatal errors, ${#probe_commands[@]} console command(s) verified. (last phase: $last_phase)"
  else
    echo "PASS: server reached RunGame, no fatal errors. (last phase: $last_phase)"
  fi
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
if [[ -n "$probe_failures" ]]; then
  echo "  Console command probe failures:" >&2
  printf '%s' "$probe_failures" >&2
fi
echo "  Log: $log_file" >&2
exit 1
