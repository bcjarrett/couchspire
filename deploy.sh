#!/usr/bin/env bash
# Build CouchSpire and install it into a Slay the Spire 2 "mods" folder.
#
#   ./deploy.sh mac                  Install into this Mac's game (mods folder inside the .app bundle).
#   ./deploy.sh bazzite [user@host]  Copy to a Bazzite/Linux box over SSH (default host: $BAZZITE_HOST).
#   ./deploy.sh logs [user@host]     Follow the remote game log, couch lines only.
#
# Mod settings: CouchSpire.cfg in the repo root is installed next to the mod (see CouchSpire.cfg.example).
# Env overrides: CONFIG (Release|Debug), STS2_REMOTE_DIR (remote game dir).
set -euo pipefail

MOD_ID="CouchSpire"
CONFIG="${CONFIG:-Release}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
REMOTE_GAME_DIR="${STS2_REMOTE_DIR:-.local/share/Steam/steamapps/common/Slay the Spire 2}"
REMOTE_LOG=".local/share/SlayTheSpire2/logs/godot.log"

if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@9/bin/dotnet ]; then
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@9/libexec"
  export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

build() {
  echo "==> Building $MOD_ID ($CONFIG)"
  dotnet build "$ROOT/LocalMultiControl.csproj" -c "$CONFIG" -nologo -v quiet
  test -f "$ROOT/$MOD_ID.dll"
}

# Copy next to the destination, then rename over it. Overwriting a loaded DLL in place corrupts a running game
# (the runtime memory-maps assemblies; methods compiled afterwards read garbage: "Bad IL range").
install_atomic() {
  local src="$1" dir="$2" name
  name="$(basename "$1")"
  cp "$src" "$dir/.$name.tmp"
  mv -f "$dir/.$name.tmp" "$dir/$name"
}

remote_host() {
  local host="${1:-${BAZZITE_HOST:-}}"
  if [ -z "$host" ]; then
    echo "Give a host (./deploy.sh $2 user@host) or set BAZZITE_HOST." >&2
    exit 1
  fi
  echo "$host"
}

case "${1:-}" in
  mac)
    build
    dest="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/$MOD_ID"
    mkdir -p "$dest"
    install_atomic "$ROOT/$MOD_ID.dll" "$dest"
    install_atomic "$ROOT/$MOD_ID.json" "$dest"
    if [ -f "$ROOT/$MOD_ID.cfg" ]; then
      install_atomic "$ROOT/$MOD_ID.cfg" "$dest"
      echo "==> Settings: $(grep -E '^[a-z_]+ *=' "$ROOT/$MOD_ID.cfg" | tr -s ' ' | paste -sd, -)"
    else
      rm -f "$dest/$MOD_ID.cfg"
      echo "==> Settings: defaults (no $MOD_ID.cfg in repo root; see $MOD_ID.cfg.example)"
    fi
    echo "==> Installed to $dest"
    if pgrep -f "SlayTheSpire2.app/Contents/MacOS" >/dev/null; then
      echo "==> The game is running: it keeps the old build until you quit and relaunch it."
    fi
    ;;
  bazzite)
    host="$(remote_host "${2:-}" bazzite)"
    build
    dest="$REMOTE_GAME_DIR/mods/$MOD_ID"
    files=("$MOD_ID.dll" "$MOD_ID.json")
    if [ -f "$ROOT/$MOD_ID.cfg" ]; then
      files+=("$MOD_ID.cfg")
    fi
    # tar over ssh (the game path has spaces, and rsync/scp remote-path quoting varies by version), unpacked into a
    # staging folder next to the mod and renamed into place, so a running game keeps its loaded copy intact.
    tar -C "$ROOT" -cf - "${files[@]}" | ssh "$host" "set -e
      mkdir -p \"$dest/.staging\"
      tar -xf - -C \"$dest/.staging\"
      rm -f \"$dest/$MOD_ID.cfg\"
      for f in \"$dest/.staging\"/*; do mv -f \"\$f\" \"$dest/\"; done
      rmdir \"$dest/.staging\"
      if pgrep -f 'Slay the Spire 2' >/dev/null; then echo '==> The game is running: it keeps the old build until you restart it.'; fi"
    echo "==> Installed to $host:~/$dest"
    ;;
  logs)
    host="$(remote_host "${2:-}" logs)"
    ssh -t "$host" "tail -n 200 -F \"$REMOTE_LOG\" | grep --line-buffered -E '\\[Couch\\]|LocalMultiControl.*(Harmony|Mod |loaded)|ERROR|Exception'"
    ;;
  *)
    sed -n '2,8p' "$0"
    exit 1
    ;;
esac
