#!/usr/bin/env bash
# Copy the installed game's reference assemblies to ~/sts2-ref/<target>/ so CouchSpire can be built
# for that Steam branch after Steam switches to the other one (Steam only installs one branch at a
# time). Builds with -p:GameTarget=<target> compile against the snapshot when it exists; see
# Sts2Paths.props and AGENTS.md §5.
#
# Usage:
#   tools/snapshot-game-ref.sh main|beta
#
# Run it with Steam set to the matching branch, and again whenever that branch gets a game patch.
# Override the game install dir with STS2_DIR and the snapshot root with STS2_REF_ROOT.
set -euo pipefail

die() {
  printf 'snapshot-game-ref: %s\n' "$*" >&2
  exit 1
}

target="${1:-}"
[[ "$target" == "main" || "$target" == "beta" ]] || die "usage: tools/snapshot-game-ref.sh main|beta"

case "$(uname -s)" in
  Darwin)
    game_dir="${STS2_DIR:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2}"
    resources="$game_dir/SlayTheSpire2.app/Contents/Resources"
    data_dir="$resources/data_sts2_macos_arm64"
    ;;
  *)
    game_dir="${STS2_DIR:-$HOME/.local/share/Steam/steamapps/common/Slay the Spire 2}"
    resources="$game_dir"
    data_dir="$game_dir/data_sts2_linux_x86_64"
    ;;
esac

[[ -f "$data_dir/sts2.dll" ]] || die "no sts2.dll in $data_dir (set STS2_DIR?)"
[[ -f "$resources/release_info.json" ]] || die "no release_info.json in $resources"

dest="${STS2_REF_ROOT:-$HOME/sts2-ref}/$target"
rm -rf "$dest"
mkdir -p "$dest"
for file in sts2.dll 0Harmony.dll Steamworks.NET.dll; do
  cp "$data_dir/$file" "$dest/$file"
done
cp "$resources/release_info.json" "$dest/release_info.json"

version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$dest/release_info.json")"
printf 'snapshot-game-ref: saved %s (%s) to %s\n' "$target" "$version" "$dest"
