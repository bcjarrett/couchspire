#!/usr/bin/env bash
# Build CouchSpire (Release) and stage/zip it for distribution. Local-only: the DLL needs the
# real game assemblies (see AGENTS.md §1), which don't exist on the CI runner.
#
# Version comes from CouchSpire.json, which semantic-release keeps in sync with the released
# git tag (see .releaserc.json's @semantic-release/exec step) — run this only against a main
# checkout that already has the version you intend to ship.
#
# Usage:
#   tools/package.sh
#
# Prints the produced zip path as the last line of stdout.
set -euo pipefail

MOD_ID="CouchSpire"
PROJECT="LocalMultiControl.csproj"
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

need_command dotnet
need_command python3

if ! command -v /opt/homebrew/opt/dotnet@9/bin/dotnet >/dev/null 2>&1 && [ -x /opt/homebrew/opt/dotnet@9/bin/dotnet ]; then
  export DOTNET_ROOT="/opt/homebrew/opt/dotnet@9/libexec"
  export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1

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

info "building Release configuration ($MOD_ID $version)"
dotnet build "$PROJECT" -c Release -nologo -v quiet

info "checking format"
dotnet format "$PROJECT" --verify-no-changes

dll_path="$MOD_ID.dll"
[[ -f "$dll_path" ]] || die "expected built DLL at repo root: $dll_path"

artifact_root="artifacts/release"
stage_root="$artifact_root/staging"
payload_root="$stage_root/$MOD_ID"
zip_path="$artifact_root/$MOD_ID-v$version.zip"

rm -rf "$stage_root"
mkdir -p "$payload_root"
cp "$dll_path" "$payload_root/$MOD_ID.dll"
cp "$MANIFEST" "$payload_root/$MANIFEST"

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

echo "$zip_path"
