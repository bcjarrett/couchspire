#!/usr/bin/env bash
# Build CouchSpire (Release) for both Steam game branches and stage/zip each for distribution:
#   artifacts/release/CouchSpire-vX.Y.Z.zip       main game branch (staged in artifacts/release/main/staging/)
#   artifacts/release/CouchSpire-beta-vX.Y.Z.zip  beta game branch (staged in artifacts/release/beta/staging/)
# Local-only: each build needs that branch's game assemblies, saved by tools/snapshot-game-ref.sh
# (AGENTS.md §5); the CI runner has neither.
#
# Version comes from CouchSpire.json, which semantic-release keeps in sync with the released
# git tag (see .releaserc.json's @semantic-release/exec step) — run this only against a master
# checkout that already has the version you intend to ship. Each staged manifest's
# min_game_version is set to the game version that build was compiled against.
#
# Usage:
#   tools/package.sh
#
# Prints the produced zip paths, one per line (main first), as the last lines of stdout.
set -euo pipefail

MOD_ID="CouchSpire"
PROJECT="CouchSpire.csproj"
MANIFEST="$MOD_ID.json"

die() {
  printf 'package: %s\n' "$*" >&2
  exit 1
}

info() {
  printf 'package: %s\n' "$*" >&2
}

need_command() {
  command -v "$1" >/dev/null 2>&1 || die "missing required command: $1"
}

if ! command -v dotnet >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@9/bin/dotnet ]; then
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@9/libexec"
  export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

need_command dotnet
need_command python3

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
cd "$repo_root"

[[ -f "$MANIFEST" ]] || die "missing $MANIFEST"
[[ -f "$PROJECT" ]] || die "missing $PROJECT"

version="$(
  python3 - "$MANIFEST" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    print(json.load(handle).get("version", ""))
PY
)"
[[ -n "$version" ]] || die "$MANIFEST has no version field"

ref_root="${STS2_REF_ROOT:-$HOME/sts2-ref}"
artifact_root="artifacts/release"
zips=()

for target in main beta; do
  release_info="$ref_root/$target/release_info.json"
  [[ -f "$release_info" ]] || die "no $target game snapshot in $ref_root/$target; run tools/snapshot-game-ref.sh $target with Steam on that branch"
  game_version="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"].lstrip("v"))' "$release_info")"

  info "building Release configuration ($MOD_ID $version, $target game branch, game v$game_version)"
  dotnet build "$PROJECT" -c Release -p:GameTarget="$target" -nologo -v quiet

  info "checking format ($target)"
  GameTarget="$target" dotnet format "$PROJECT" --verify-no-changes

  dll_path="$MOD_ID.dll"
  [[ -f "$dll_path" ]] || die "expected built DLL at repo root: $dll_path"

  stage_root="$artifact_root/$target/staging"
  payload_root="$stage_root/$MOD_ID"
  if [[ "$target" == "main" ]]; then
    zip_path="$artifact_root/$MOD_ID-v$version.zip"
  else
    zip_path="$artifact_root/$MOD_ID-$target-v$version.zip"
  fi

  rm -rf "$stage_root"
  mkdir -p "$payload_root"
  cp "$dll_path" "$payload_root/$MOD_ID.dll"

  python3 - "$MANIFEST" "$payload_root/$MANIFEST" "$target" "$game_version" <<'PY'
import json
import sys

source, dest, target, game_version = sys.argv[1:5]
with open(source, "r", encoding="utf-8") as handle:
    manifest = json.load(handle)
manifest["min_game_version"] = game_version
if target == "beta":
    manifest["name"] = f"{manifest['name']} - beta branch"
with open(dest, "w", encoding="utf-8") as handle:
    json.dump(manifest, handle, indent=4)
    handle.write("\n")
PY

  info "creating $zip_path"
  python3 - "$zip_path" "$stage_root" <<'PY'
from pathlib import Path
import sys
import zipfile

zip_path = Path(sys.argv[1])
stage_root = Path(sys.argv[2])

zip_path.parent.mkdir(parents=True, exist_ok=True)
if zip_path.exists():
    zip_path.unlink()

with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(stage_root.rglob("*")):
        if path.is_file():
            archive.write(path, path.relative_to(stage_root).as_posix())
PY
  zips+=("$zip_path")
done

printf '%s\n' "${zips[@]}"
