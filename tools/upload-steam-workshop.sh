#!/usr/bin/env bash
# Upload CouchSpire to the Slay the Spire 2 Steam Workshop (app 2868840).
#
# There is no in-game Workshop uploader; this uses steamcmd directly. Run this locally —
# it is deliberately NOT wired into CI. SteamCMD needs a Steam Guard-authenticated login
# session, which is awkward and unreliable to keep alive on short-lived hosted CI runners.
#
# By default this downloads the already-built zip from the matching GitHub Release (created by
# CI, built and attached locally via tools/attach-release-asset.sh) so every distribution
# channel — GitHub, Steam, and later Nexus — ships the exact same bytes. Pass --local-build to
# build fresh instead (e.g. before CI/the release flow exists yet, or for a quick local test).
#
# Usage:
#   tools/upload-steam-workshop.sh [options] vX.Y.Z
#
# Options:
#   --dry-run                 Write the VDF and print the SteamCMD command without uploading.
#   --local-build              Build locally (tools/package.sh) instead of downloading the release asset.
#   --skip-package             Content folder is already staged; don't download or build.
#   --username USERNAME        Steam username for SteamCMD login. Defaults to STEAM_USERNAME.
#   --vdf PATH                 Workshop VDF path. Defaults to steam-workshop/couchspire.vdf.
#   --preview PATH             Preview image path. Defaults to steam-workshop/preview.jpg.
#   --content-folder PATH      Uploaded content folder. Defaults to artifacts/release/staging/CouchSpire.
#   --visibility VALUE         Steam visibility value. Defaults to 2 (private).
#
# Description: steam-workshop/description.md, if present, is used as the Workshop item's
# description instead of the manifest's (short, in-game) description — the Workshop page can
# afford a fuller pitch and links that don't belong in the in-game mod list.
#
# Compatibility: if steam-workshop/verified-versions.txt exists, its lines are appended to the
# Workshop item's description as "verified compatible with" game versions. Update that file (or
# description.md) and re-run with --skip-package (reusing the last staged/downloaded build) to
# refresh just that text on the Workshop page — no new mod release needed.
#
# Examples:
#   tools/upload-steam-workshop.sh --dry-run --local-build v0.1.0
#   STEAM_USERNAME=myname tools/upload-steam-workshop.sh v0.1.0
set -euo pipefail

MOD_ID="CouchSpire"
APP_ID="2868840"

usage() {
  cat <<'USAGE'
Usage:
  tools/upload-steam-workshop.sh [options] vX.Y.Z

Options:
  --dry-run                 Write the VDF and print the SteamCMD command without uploading.
  --local-build              Build locally (tools/package.sh) instead of downloading the release asset.
  --skip-package             Content folder is already staged; don't download or build.
  --username USERNAME        Steam username for SteamCMD login. Defaults to STEAM_USERNAME.
  --vdf PATH                 Workshop VDF path. Defaults to steam-workshop/couchspire.vdf.
  --preview PATH             Preview image path. Defaults to steam-workshop/preview.jpg.
  --content-folder PATH      Uploaded content folder. Defaults to artifacts/release/staging/CouchSpire.
  --visibility VALUE         Steam visibility value. Defaults to 2 (private).

Examples:
  tools/upload-steam-workshop.sh --dry-run --local-build v0.1.0
  STEAM_USERNAME=myname tools/upload-steam-workshop.sh v0.1.0
USAGE
}

die() {
  printf 'upload-steam-workshop: %s\n' "$*" >&2
  exit 1
}

warn() {
  printf 'upload-steam-workshop: warning: %s\n' "$*" >&2
}

info() {
  printf 'upload-steam-workshop: %s\n' "$*"
}

need_command() {
  command -v "$1" >/dev/null 2>&1 || die "missing required command: $1"
}

absolute_path() {
  python3 - "$1" <<'PY'
from pathlib import Path
import sys

print(Path(sys.argv[1]).expanduser().resolve(strict=False))
PY
}

read_published_file_id() {
  python3 - "$1" <<'PY'
from pathlib import Path
import re
import sys

path = Path(sys.argv[1])
if not path.exists():
    print("0")
    raise SystemExit

match = re.search(r'"publishedfileid"\s*"([^"]*)"', path.read_text(encoding="utf-8"))
print(match.group(1) if match else "0")
PY
}

dry_run=false
skip_package=false
local_build=false
steam_username="${STEAM_USERNAME:-}"
vdf_path=""
preview_file=""
content_folder=""
visibility="2"
tag=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)
      dry_run=true
      shift
      ;;
    --skip-package)
      skip_package=true
      shift
      ;;
    --local-build)
      local_build=true
      shift
      ;;
    --username)
      [[ $# -ge 2 ]] || die "--username requires a value"
      steam_username="$2"
      shift 2
      ;;
    --vdf)
      [[ $# -ge 2 ]] || die "--vdf requires a value"
      vdf_path="$2"
      shift 2
      ;;
    --preview)
      [[ $# -ge 2 ]] || die "--preview requires a value"
      preview_file="$2"
      shift 2
      ;;
    --content-folder)
      [[ $# -ge 2 ]] || die "--content-folder requires a value"
      content_folder="$2"
      shift 2
      ;;
    --visibility)
      [[ $# -ge 2 ]] || die "--visibility requires a value"
      visibility="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --*)
      die "unknown option: $1"
      ;;
    *)
      [[ -z "$tag" ]] || die "unexpected extra argument: $1"
      tag="$1"
      shift
      ;;
  esac
done

[[ -n "$tag" ]] || {
  usage >&2
  exit 2
}

if [[ ! "$tag" =~ ^v[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  die "tag must match v<major>.<minor>.<patch>, got '$tag'"
fi

if [[ ! "$visibility" =~ ^[0-3]$ ]]; then
  die "visibility must be 0 (public), 1 (friends-only), 2 (private), or 3 (unlisted)"
fi

need_command git
need_command python3

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
cd "$repo_root"

version="${tag#v}"
manifest_path="$MOD_ID.json"
default_content_folder="artifacts/release/staging/$MOD_ID"
default_vdf_path="steam-workshop/couchspire.vdf"
default_preview_file="steam-workshop/preview.jpg"

content_folder="${content_folder:-$default_content_folder}"
vdf_path="${vdf_path:-$default_vdf_path}"
preview_file="${preview_file:-$default_preview_file}"

[[ -f "$manifest_path" ]] || die "missing $manifest_path"

if [[ "$skip_package" == false ]]; then
  if [[ "$local_build" == true ]]; then
    info "building locally for $tag"
    tools/package.sh >/dev/null
  else
    need_command gh
    info "downloading release asset for $tag"
    download_dir="artifacts/release/download"
    rm -rf "$download_dir"
    mkdir -p "$download_dir"
    gh release download "$tag" --pattern "$MOD_ID-*.zip" --dir "$download_dir" \
      || die "no release asset found for $tag; run tools/attach-release-asset.sh first, or pass --local-build"
    zip_file="$(find "$download_dir" -maxdepth 1 -name "$MOD_ID-*.zip" | head -n 1)"
    [[ -n "$zip_file" ]] || die "downloaded but found no $MOD_ID-*.zip in $download_dir"
    stage_root="artifacts/release/staging"
    rm -rf "$stage_root"
    mkdir -p "$stage_root/$MOD_ID"
    python3 - "$zip_file" "$stage_root/$MOD_ID" <<'PY'
from pathlib import Path
import sys
import zipfile

zip_path = Path(sys.argv[1])
dest = Path(sys.argv[2])

with zipfile.ZipFile(zip_path, "r") as archive:
    for member in archive.infolist():
        if member.is_dir():
            continue
        target = dest / Path(member.filename).name
        target.parent.mkdir(parents=True, exist_ok=True)
        with archive.open(member) as source, open(target, "wb") as out:
            out.write(source.read())
PY
  fi
fi

content_folder_abs="$(absolute_path "$content_folder")"
vdf_path_abs="$(absolute_path "$vdf_path")"
preview_file_abs="$(absolute_path "$preview_file")"
manifest_path_abs="$(absolute_path "$content_folder_abs/$MOD_ID.json")"

[[ -d "$content_folder_abs" ]] || die "missing content folder: $content_folder_abs"
[[ -f "$content_folder_abs/$MOD_ID.json" ]] || die "missing staged manifest: $content_folder_abs/$MOD_ID.json"
[[ -f "$content_folder_abs/$MOD_ID.dll" ]] || die "missing staged DLL: $content_folder_abs/$MOD_ID.dll"
[[ -f "$preview_file_abs" ]] || die "missing preview image: $preview_file_abs (see steam-workshop/README.md)"

manifest_version="$(
  python3 - "$content_folder_abs/$MOD_ID.json" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    data = json.load(handle)

print(data.get("version", ""))
PY
)"

[[ "$manifest_version" == "$version" ]] || die "staged manifest version '$manifest_version' does not match tag '$tag'"

dependency_summary="$(
  python3 - "$content_folder_abs/$MOD_ID.json" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    data = json.load(handle)

dependencies = data.get("dependencies", [])
if dependencies:
    print(", ".join(str(item) for item in dependencies))
PY
)"

if [[ -n "$dependency_summary" ]]; then
  warn "manifest declares dependencies: $dependency_summary"
  warn "SteamCMD VDF upload does not add Workshop required items; add required items on Steam or through ISteamUGC if these are hard requirements."
fi

changenote="$(python3 tools/changelog-section.py CHANGELOG.md "$version" 2>/dev/null || true)"
if [[ -z "$changenote" ]]; then
  warn "no CHANGELOG.md section found for version '$version'; using a generic changenote"
  changenote="Release $tag."
else
  # Steam's changenote is plain text (no Markdown rendering): drop the heading line and
  # strip Markdown syntax so the Workshop page doesn't show literal #/**/backticks.
  changenote="$(
    printf '%s' "$changenote" | tail -n +2 | sed -E -e 's/^#+[[:space:]]*//' -e 's/[`*]//g' | sed '/^$/N;/^\n$/D'
  )"
fi

verified_versions_file="steam-workshop/verified-versions.txt"
verified_versions=""
if [[ -f "$verified_versions_file" ]]; then
  verified_versions="$(grep -v '^[[:space:]]*#' "$verified_versions_file" | grep -v '^[[:space:]]*$' | paste -sd, - | sed 's/,/, /g')"
fi

description_file="steam-workshop/description.md"
description_override=""
if [[ -f "$description_file" ]]; then
  description_override="$(cat "$description_file")"
fi

mkdir -p "$(dirname "$vdf_path_abs")"

before_published_file_id="$(read_published_file_id "$vdf_path_abs")"

python3 - "$vdf_path_abs" "$manifest_path_abs" "$content_folder_abs" "$preview_file_abs" "$tag" "$visibility" "$APP_ID" "$changenote" "$verified_versions" "$description_override" <<'PY'
from pathlib import Path
import json
import re
import sys

vdf_path = Path(sys.argv[1])
manifest_path = Path(sys.argv[2])
content_folder = sys.argv[3]
preview_file = sys.argv[4]
tag = sys.argv[5]
visibility = sys.argv[6]
app_id = sys.argv[7]
changenote = sys.argv[8]
verified_versions = sys.argv[9]
description_override = sys.argv[10]

with manifest_path.open("r", encoding="utf-8") as handle:
    manifest = json.load(handle)

published_file_id = "0"
if vdf_path.exists():
    match = re.search(r'"publishedfileid"\s*"([^"]*)"', vdf_path.read_text(encoding="utf-8"))
    if match:
        published_file_id = match.group(1)

def esc(value):
    return str(value).replace("\\", "\\\\").replace('"', '\\"')

title = manifest.get("name") or manifest.get("id") or "CouchSpire"
description = description_override.strip() or manifest.get("description") or title
if verified_versions:
    description += f"\n\nVerified compatible with game version(s): {verified_versions}."

vdf_path.write_text(
    "\n".join([
        '"workshopitem"',
        "{",
        f'  "appid" "{esc(app_id)}"',
        f'  "publishedfileid" "{esc(published_file_id)}"',
        f'  "contentfolder" "{esc(content_folder)}"',
        f'  "previewfile" "{esc(preview_file)}"',
        f'  "visibility" "{esc(visibility)}"',
        f'  "title" "{esc(title)}"',
        f'  "description" "{esc(description)}"',
        f'  "changenote" "{esc(changenote)}"',
        "}",
        "",
    ]),
    encoding="utf-8",
)
PY

info "wrote Workshop VDF: $vdf_path_abs"

if [[ "$dry_run" == true ]]; then
  info "dry run; not invoking SteamCMD"
  info "command: steamcmd +login \${STEAM_USERNAME} +workshop_build_item '$vdf_path_abs' +quit"
  exit 0
fi

[[ -n "$steam_username" ]] || die "missing Steam username; pass --username or set STEAM_USERNAME"

steamcmd_bin="${STEAMCMD:-steamcmd}"
if ! command -v "$steamcmd_bin" >/dev/null 2>&1; then
  [[ -x "$steamcmd_bin" ]] || die "missing SteamCMD executable: $steamcmd_bin"
fi

info "uploading $tag to Steam Workshop app $APP_ID"
"$steamcmd_bin" +login "$steam_username" +workshop_build_item "$vdf_path_abs" +quit

after_published_file_id="$(read_published_file_id "$vdf_path_abs")"
if [[ "$before_published_file_id" != "$after_published_file_id" ]]; then
  info "publishedfileid changed from $before_published_file_id to $after_published_file_id"
  info "keep $vdf_path under version control so future uploads update the same Workshop item"
fi

info "Steam Workshop upload command finished"
