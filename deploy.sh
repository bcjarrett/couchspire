#!/usr/bin/env bash
# Build CouchSpire and install it into a Slay the Spire 2 "mods" folder.
#
#   ./deploy.sh local                 Build and install into the local game (macOS or Linux).
#   ./deploy.sh remote [user@host]    Copy to a Linux machine (e.g. a Steam Deck or Bazzite box) over SSH
#                                      (default host: $STS2_REMOTE_HOST). `./deploy.sh mac` is a hidden alias
#                                      for `local`, kept for muscle memory.
#   ./deploy.sh logs [user@host]      Follow the remote game log, couch lines only.
#   ./deploy.sh test [scenario|all] [--repeat N] [--bless] [--review] [--strict-layout]
#                                      Run the in-game scenario runner (macOS only for now). Builds Debug, installs
#                                      with no CouchSpire.cfg, resets the test save profile, launches, reports, and
#                                      exits with the runner's exit code. See docs/testing.md.
#
# Mod settings: CouchSpire.cfg in the repo root is installed next to the mod (see CouchSpire.cfg.example).
# Game branch: builds for the Steam branch the target game is on (main or beta), detected by matching its
# release_info.json against the tools/snapshot-game-ref.sh snapshots; set GAME_TARGET=main|beta to override.
# Env overrides: CONFIG (Release|Debug), STS2_DIR (local game install dir, matches Sts2Paths.props' Sts2Dir),
# STS2_REMOTE_DIR (remote game dir), STS2_REMOTE_HOST (default `remote`/`logs` host), COUCHSPIRE_TEST_TIMEOUT
# (test mode's outer timeout in seconds, default 1200), COUCHSPIRE_TEST_LOCK_WAIT (seconds to queue for the test
# lock instead of failing, default 0).
set -euo pipefail

MOD_ID="CouchSpire"
CONFIG="${CONFIG:-Release}"
# BASH_SOURCE, not $0: this file is also sourced (not executed) by Tests/host/ scripts to reuse its functions
# without running the case dispatch below, and $0 would then point at the sourcing script instead of this one.
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
REMOTE_GAME_DIR="${STS2_REMOTE_DIR:-.local/share/Steam/steamapps/common/Slay the Spire 2}"
REMOTE_LOG=".local/share/SlayTheSpire2/logs/godot.log"

if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@9/bin/dotnet ]; then
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@9/libexec"
  export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

# $1: game branch (main|beta), from detect_game_target.
build() {
  echo "==> Building $MOD_ID ($CONFIG, $1 game branch)"
  dotnet build "$ROOT/CouchSpire.csproj" -c "$CONFIG" -p:GameTarget="$1" -nologo -v quiet
  test -f "$ROOT/$MOD_ID.dll"
}

# Prints the "version" of a release_info.json read from stdin, without the leading "v" (empty if unreadable).
release_info_version() {
  python3 -c 'import json,sys; print(json.load(sys.stdin)["version"].lstrip("v"))' 2>/dev/null || true
}

# Prints main or beta for the game whose release_info.json version is $1, by matching the snapshots that
# tools/snapshot-game-ref.sh saved. $GAME_TARGET overrides. Exits if the version matches neither snapshot: that
# game build is new (a patch landed), so refresh the snapshot first (AGENTS.md §5).
detect_game_target() {
  local installed="$1" ref_root="${STS2_REF_ROOT:-$HOME/sts2-ref}" main_version beta_version
  if [ -n "${GAME_TARGET:-}" ]; then
    echo "$GAME_TARGET"
    return
  fi
  main_version="$(release_info_version < "$ref_root/main/release_info.json" 2>/dev/null || true)"
  beta_version="$(release_info_version < "$ref_root/beta/release_info.json" 2>/dev/null || true)"
  if [ -n "$installed" ] && [ "$installed" = "$beta_version" ]; then
    echo beta
  elif [ -n "$installed" ] && [ "$installed" = "$main_version" ]; then
    echo main
  elif [ -z "$main_version$beta_version" ]; then
    echo main
  else
    echo "Game version '${installed:-unknown}' matches neither snapshot (main: ${main_version:-none}, beta: ${beta_version:-none})." >&2
    echo "If a game patch landed, run tools/snapshot-game-ref.sh main|beta for that branch; or set GAME_TARGET=main|beta." >&2
    exit 1
  fi
}

local_release_info() {
  if is_macos; then
    cat "$(local_game_dir)/SlayTheSpire2.app/Contents/Resources/release_info.json" 2>/dev/null || true
  else
    cat "$(local_game_dir)/release_info.json" 2>/dev/null || true
  fi
}

install_atomic() {
  local src="$1" dir="$2" name
  name="$(basename "$1")"
  cp "$src" "$dir/.$name.tmp"
  mv -f "$dir/.$name.tmp" "$dir/$name"
}

remote_host() {
  local host="${1:-${STS2_REMOTE_HOST:-}}"
  if [ -z "$host" ]; then
    echo "Give a host (./deploy.sh $2 user@host) or set STS2_REMOTE_HOST." >&2
    exit 1
  fi
  echo "$host"
}

# --- Local paths (macOS or Linux) ------------------------------------------------------------------------------
# Mirrors Sts2Paths.props' Sts2Dir detection, so `-p:Sts2Dir=...`/$STS2_DIR and this script agree on the game.

is_macos() {
  [ "$(uname -s)" = "Darwin" ]
}

# The game install directory. Override with $STS2_DIR (matches Sts2Paths.props).
local_game_dir() {
  if [ -n "${STS2_DIR:-}" ]; then
    echo "$STS2_DIR"
  elif is_macos; then
    echo "$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
  else
    echo "$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2"
  fi
}

# macOS ships the game inside an .app bundle; the mods folder lives under its Contents/MacOS. Linux (native or
# Steam Deck/Bazzite Game Mode) has no bundle: mods sit directly under the install directory.
local_macos_dir() {
  echo "$(local_game_dir)/SlayTheSpire2.app/Contents/MacOS"
}

local_dest() {
  if is_macos; then
    echo "$(local_macos_dir)/mods/$MOD_ID"
  else
    echo "$(local_game_dir)/mods/$MOD_ID"
  fi
}

local_user_data_root() {
  if is_macos; then
    echo "$HOME/Library/Application Support/SlayTheSpire2"
  else
    echo "$HOME/.local/share/SlayTheSpire2"
  fi
}

# Copy next to the destination, then rename over it. Overwriting a loaded DLL in place corrupts a running game
# (the runtime memory-maps assemblies; methods compiled afterwards read garbage: "Bad IL range").
# True if the local game is running. On macOS this matches the executable path (ps comm), not the command line:
# pgrep -f would also match any shell whose command merely mentions the game's path. Linux has no such fixed path
# (native install layouts vary), so it falls back to a process-name match there.
local_game_running() {
  if is_macos; then
    ps -axo comm= | grep -q "/SlayTheSpire2.app/Contents/MacOS/Slay the Spire 2$"
  else
    pgrep -f "Slay the Spire 2" >/dev/null 2>&1
  fi
}

# Install the built DLL/JSON (and, unless cfg_mode is "none", the repo's CouchSpire.cfg) into the local mods folder.
# Shared by `local` and `test` (macOS only) so the install logic lives in exactly one place.
install_local() {
  # $1: cfg mode - "user" (default; installs the repo's CouchSpire.cfg if present) or "none" (test runs never
  # install a cfg, so the pinned COUCHSPIRE_* env vars are what decides behavior; see docs/testing.md).
  local cfg_mode="${1:-user}"
  local dest
  dest="$(local_dest)"
  mkdir -p "$dest"
  install_atomic "$ROOT/$MOD_ID.dll" "$dest"
  install_atomic "$ROOT/$MOD_ID.json" "$dest"
  if [ "$cfg_mode" = "user" ] && [ -f "$ROOT/$MOD_ID.cfg" ]; then
    install_atomic "$ROOT/$MOD_ID.cfg" "$dest"
    echo "==> Settings: $(grep -E '^[a-z_]+ *=' "$ROOT/$MOD_ID.cfg" | tr -s ' ' | paste -sd, -)"
  else
    rm -f "$dest/$MOD_ID.cfg"
    if [ "$cfg_mode" = "none" ]; then
      echo "==> Settings: none installed for this test run (COUCHSPIRE_* env vars pin behavior instead; ./deploy.sh local restores your $MOD_ID.cfg)"
    else
      echo "==> Settings: defaults (no $MOD_ID.cfg in repo root; see $MOD_ID.cfg.example)"
    fi
  fi
  echo "==> Installed to $dest"
}

# --- Test-mode helpers (macOS only for now) --------------------------------------------------------------------

# Resets the isolated test save profile: seeds settings.save (mod consent) from the real steam/<id>/ profile if
# missing, then deletes everything else under it. Takes the target dir and its user-data root explicitly (rather
# than reading globals) so it can be exercised against a throwaway fake tree in Tests/host/ without touching real
# user data. Refuses anything that doesn't look exactly like "<root>/default/1", and never touches steam/.
reset_test_profile() {
  local target="${1:-}"
  local root="${2:-}"

  if [ -z "$target" ] || [ -z "$root" ]; then
    echo "refuse: reset_test_profile called with an empty target or root" >&2
    return 1
  fi

  case "$target" in
    */steam | */steam/*)
      echo "refuse: reset target looks like it's under steam/, refusing to touch it: $target" >&2
      return 1
      ;;
  esac

  if [ "$target" != "$root/default/1" ]; then
    echo "refuse: reset target '$target' is not exactly '<root>/default/1' (root: '$root')" >&2
    return 1
  fi

  if [ -L "$target" ] || [ -L "$root/default" ]; then
    echo "refuse: '$target' or its parent is a symlink; resolve it by hand" >&2
    return 1
  fi

  mkdir -p "$target"

  if [ ! -f "$target/settings.save" ]; then
    local steam_root="$root/steam"
    local matches=()
    local d
    if [ -d "$steam_root" ]; then
      for d in "$steam_root"/*/; do
        [ -d "$d" ] || continue
        matches+=("${d%/}")
      done
    fi
    if [ "${#matches[@]}" -ne 1 ]; then
      echo "refuse: expected exactly one $steam_root/<steamid> folder to seed settings.save from, found ${#matches[@]}" >&2
      return 1
    fi
    local steam_settings="${matches[0]}/settings.save"
    if [ ! -f "$steam_settings" ]; then
      echo "refuse: $steam_settings not found to seed from" >&2
      return 1
    fi
    cp "$steam_settings" "$target/settings.save"
    echo "==> Seeded $target/settings.save from $steam_settings"
  fi

  find "$target" -mindepth 1 -maxdepth 1 ! -name settings.save -exec rm -rf {} +
  echo "==> Reset $target (kept settings.save)"
}

# Only one test run may hold the game install / default/1/ tree at a time (see docs/testing.md). Sets the
# COUCHSPIRE_LOCK_DIR global and an EXIT trap on success; exits 3 on failure to acquire (never returns).
COUCHSPIRE_LOCK_DIR=""

release_test_lock() {
  if [ -n "$COUCHSPIRE_LOCK_DIR" ]; then
    rm -rf "$COUCHSPIRE_LOCK_DIR"
  fi
}

acquire_test_lock() {
  local lock_dir="$1"
  # COUCHSPIRE_TEST_LOCK_WAIT=<seconds>: queue behind a live holder instead of failing (parallel agents).
  local wait_s="${COUCHSPIRE_TEST_LOCK_WAIT:-0}" waited=0 holder_pid=""
  while true; do
    if mkdir "$lock_dir" 2>/dev/null; then
      COUCHSPIRE_LOCK_DIR="$lock_dir"
      echo "$$" > "$lock_dir/pid" 2>/dev/null || true
      trap release_test_lock EXIT
      return 0
    fi
    holder_pid="$(cat "$lock_dir/pid" 2>/dev/null || true)"
    # An empty pid is a holder between its mkdir and pid write: keep waiting. A dead pid is a stale lock: stop.
    if [ "$waited" -ge "$wait_s" ] || { [ -n "$holder_pid" ] && ! kill -0 "$holder_pid" 2>/dev/null; }; then
      break
    fi
    if [ "$waited" -eq 0 ]; then
      echo "==> Waiting up to ${wait_s}s for the test lock (held by PID $holder_pid)..."
    fi
    sleep 5
    waited=$((waited + 5))
  done

  if [ -n "$holder_pid" ] && kill -0 "$holder_pid" 2>/dev/null; then
    echo "Another test run holds the lock: PID $holder_pid ($lock_dir). Wait for it to finish." >&2
  else
    echo "Lock $lock_dir exists but its PID (${holder_pid:-unknown}) isn't running: stale. If you're sure nothing else is using it: rm -rf \"$lock_dir\"" >&2
  fi
  exit 3
}

# --- Dispatch --------------------------------------------------------------------------------------------------
# Guarded so Tests/host/ scripts can `source` this file to reuse the functions above without running any of this
# (and without risking an `exit` from inside a sourced script killing the caller's shell).
if [ "${BASH_SOURCE[0]:-$0}" = "$0" ]; then
  case "${1:-}" in
    local | mac)
      game_target="$(detect_game_target "$(local_release_info | release_info_version)")"
      build "$game_target"
      install_local user
      if local_game_running; then
        echo "==> The game is running: it keeps the old build until you quit and relaunch it."
      fi
      ;;
    remote | bazzite)
      host="$(remote_host "${2:-}" "${1:-remote}")"
      game_target="$(detect_game_target "$(ssh "$host" "cat \"$REMOTE_GAME_DIR/release_info.json\"" 2>/dev/null | release_info_version)")"
      build "$game_target"
      dest="$REMOTE_GAME_DIR/mods/$MOD_ID"
      files=("$MOD_ID.dll" "$MOD_ID.json")
      if [ -f "$ROOT/$MOD_ID.cfg" ]; then
        files+=("$MOD_ID.cfg")
      fi
      # tar over ssh (the game path has spaces, and rsync/scp remote-path quoting varies by version), unpacked into a
      # staging folder next to the mod and renamed into place, so a running game keeps its loaded copy intact.
      # COPYFILE_DISABLE and --no-xattrs keep macOS metadata out of the archive: Linux tar warns about its xattr headers,
      # and the AppleDouble "._*" copies would be left behind in the mods folder.
      COPYFILE_DISABLE=1 tar --no-xattrs -C "$ROOT" -cf - "${files[@]}" | ssh "$host" "set -e
        rm -rf \"$dest/.staging\"
        mkdir -p \"$dest/.staging\"
        tar -xf - -C \"$dest/.staging\"
        rm -f \"$dest/$MOD_ID.cfg\"
        for f in ${files[*]}; do mv -f \"$dest/.staging/\$f\" \"$dest/\$f\"; done
        rm -rf \"$dest/.staging\"
        if pgrep -f 'Slay the Spire 2' >/dev/null; then echo '==> The game is running: it keeps the old build until you restart it.'; fi"
      echo "==> Installed to $host:~/$dest"
      ;;
    logs)
      host="$(remote_host "${2:-}" logs)"
      ssh -t "$host" "tail -n 200 -F \"$REMOTE_LOG\" | grep --line-buffered -E '\\[Couch\\]|CouchSpire.*(Harmony|Mod |loaded)|ERROR|Exception'"
      ;;
    test)
      if ! is_macos; then
        echo "test mode is macOS-only for now (see docs/testing.md); use 'local' or 'remote' to deploy on Linux." >&2
        exit 1
      fi
      # Test scenarios only compile with COUCHSPIRE_TESTS, which is only defined in Debug (see docs/testing.md).
      CONFIG="Debug"

      shift
      selection="all"
      repeat=""
      bless=0
      review=0
      strict_layout=0
      if [ $# -gt 0 ] && [ "${1#--}" = "$1" ]; then
        selection="$1"
        shift
      fi
      while [ $# -gt 0 ]; do
        case "$1" in
          --repeat)
            repeat="${2:-}"
            if [ -z "$repeat" ]; then
              echo "--repeat needs a number" >&2
              exit 1
            fi
            shift 2
            ;;
          --bless)
            bless=1
            shift
            ;;
          --review)
            review=1
            shift
            ;;
          --strict-layout)
            strict_layout=1
            shift
            ;;
          *)
            echo "Unknown test option: $1" >&2
            exit 1
            ;;
        esac
      done

      # 1. Lock: only one test run may use the game install and default/1/ at a time.
      lock_dir="${TMPDIR:-/tmp}/couchspire-test.lock"
      acquire_test_lock "$lock_dir"

      # 2. Refuse if the game is already running.
      if local_game_running; then
        echo "The game is running. Quit it first: only one test run may use the game install and default/1/ at a time." >&2
        exit 3
      fi

      # 3. Build Debug for the installed game's branch and install with no CouchSpire.cfg.
      game_target="$(detect_game_target "$(local_release_info | release_info_version)")"
      build "$game_target"
      install_local none

      # 4. Reset the test save profile. Hard safety: the target must be exactly the real "<root>/default/1", and
      # HOME must be non-empty (set -u catches unset, not empty). reset_test_profile double-checks this too.
      user_root="$(local_user_data_root)"
      profile_dir="$user_root/default/1"
      expected="$HOME/Library/Application Support/SlayTheSpire2/default/1"
      if [ -z "${HOME:-}" ] || [ "$profile_dir" != "$expected" ]; then
        echo "Refusing: computed test profile path doesn't match the expected real path ($expected). Aborting." >&2
        exit 1
      fi
      reset_test_profile "$profile_dir" "$user_root"

      # 5. Results dir and launch.
      timestamp="$(date -u +%Y%m%d-%H%M%S)"
      out_dir="$ROOT/test-results/$timestamp"
      mkdir -p "$out_dir"
      echo "==> Results: $out_dir"

      game_bin="$(local_macos_dir)/Slay the Spire 2"
      if [ ! -x "$game_bin" ]; then
        echo "Game binary not found or not executable: $game_bin" >&2
        exit 2
      fi

      game_args=(--force-steam off --couch-test "$selection" --couch-test-out "$out_dir" --couch-test-baselines "$ROOT/Tests/layout-baselines/$game_target")
      if [ -n "$repeat" ]; then
        game_args+=(--repeat "$repeat")
      fi
      if [ "$bless" -eq 1 ]; then
        game_args+=(--bless)
      fi
      if [ "$review" -eq 1 ]; then
        game_args+=(--review)
      fi
      if [ "$strict_layout" -eq 1 ]; then
        game_args+=(--strict-layout)
      fi

      echo "==> Launching: $game_bin ${game_args[*]}"
      STS2_DEV_WINDOWED=1 STS2_DEV_SKIP=1 \
        COUCHSPIRE_ROUTING=1 COUCHSPIRE_SIMULTANEOUS=1 COUCHSPIRE_PROBE=0 COUCHSPIRE_OVERLAY=0 COUCHSPIRE_SHOTS=0 \
        COUCHSPIRE_EVENT_PANEL_LEFT=1 COUCHSPIRE_GAMESCOPE_FOCUS=1 \
        "$game_bin" "${game_args[@]}" >"$out_dir/game-stdout.txt" 2>&1 &
      game_pid=$!
      # Ctrl-C/TERM: quit the game too, so the lock is never released while it still owns default/1/.
      trap 'kill -TERM "$game_pid" 2>/dev/null; exit 130' INT TERM

      # Outer timeout watchdog: bash 3.2 has no `timeout` builtin, so poll in a background sleep loop. This only
      # fires on a hang; the runner is expected to quit by itself. SIGTERM, wait up to 10s, then SIGKILL.
      timeout_s="${COUCHSPIRE_TEST_TIMEOUT:-1200}"
      (
        waited=0
        while kill -0 "$game_pid" 2>/dev/null; do
          if [ "$waited" -ge "$timeout_s" ]; then
            echo "==> Outer timeout (${timeout_s}s): sending SIGTERM to PID $game_pid" >&2
            kill -TERM "$game_pid" 2>/dev/null || true
            term_wait=0
            while kill -0 "$game_pid" 2>/dev/null && [ "$term_wait" -lt 10 ]; do
              sleep 1
              term_wait=$((term_wait + 1))
            done
            if kill -0 "$game_pid" 2>/dev/null; then
              echo "==> Still alive after SIGTERM+10s: sending SIGKILL" >&2
              kill -KILL "$game_pid" 2>/dev/null || true
            fi
            touch "$out_dir/.timed-out"
            break
          fi
          sleep 1
          waited=$((waited + 1))
        done
      ) &
      watchdog_pid=$!

      set +e
      wait "$game_pid"
      game_exit=$?
      set -e

      # Stop the watchdog if the game exited on its own.
      kill "$watchdog_pid" 2>/dev/null || true
      wait "$watchdog_pid" 2>/dev/null || true

      # 6. Report.
      log_src="$user_root/logs/godot.log"
      if [ -f "$log_src" ]; then
        cp "$log_src" "$out_dir/godot.log"
      else
        echo "==> No godot.log found at $log_src" >&2
      fi

      if [ -f "$out_dir/summary.txt" ]; then
        echo "==> summary.txt:"
        cat "$out_dir/summary.txt"
      elif [ -f "$out_dir/godot.log" ]; then
        echo "==> No summary.txt written by the runner (game exited without results). Last 40 log lines:" >&2
        tail -n 40 "$out_dir/godot.log"
      else
        echo "==> No summary.txt and no godot.log -- the runner never started." >&2
      fi

      if [ -f "$out_dir/godot.log" ]; then
        # On failure only, and only game-logger errors and runner verdicts: bare "ERROR:" lines are Godot engine noise
        # (leaks at exit, preloads cut short by the quick quit), and a passing run still logs the game's own error
        # for deleting a never-written multiplayer save when the runner abandons at floor 0.
        failing="$(grep -E "^\\[ERROR\\]|\\[CouchTest\\].*(FAIL|TIMEOUT)" "$out_dir/godot.log" | head -n 50 || true)"
        if [ -n "$failing" ] && [ "$game_exit" -ne 0 ]; then
          echo "==> Failing log lines (capped at 50):"
          echo "$failing"
        fi
      fi

      final_exit="$game_exit"
      if [ -f "$out_dir/.timed-out" ]; then
        echo "==> Killed by outer timeout (${timeout_s}s) -- the game hung, this is not a normal scenario result." >&2
        final_exit=1
      elif [ ! -f "$out_dir/results.json" ]; then
        echo "==> Game exited without writing results.json -- treating as failure." >&2
        final_exit=1
      fi

      echo "==> Exit: $final_exit (results: $out_dir)"
      exit "$final_exit"
      ;;
    *)
      sed -n '2,17p' "$0"
      exit 1
      ;;
  esac
fi
