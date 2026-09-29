#!/usr/bin/env bash
# Run this locally after merging a release PR to master. The GitHub Actions release workflow
# (on the CI runner) already computed the version, tagged it, and created the GitHub Release —
# but it has no game install, so the DLL asset isn't attached yet. This builds it here and
# uploads it to that release.
#
# Usage:
#   tools/attach-release-asset.sh
set -euo pipefail

die() {
  printf 'attach-release-asset: %s\n' "$*" >&2
  exit 1
}

info() {
  printf 'attach-release-asset: %s\n' "$*"
}

need_command() {
  command -v "$1" >/dev/null 2>&1 || die "missing required command: $1"
}

need_command git
need_command gh
need_command python3

repo_root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
cd "$repo_root"

if ! gh auth status >/dev/null 2>&1; then
  die "GitHub CLI is not authenticated; run 'gh auth login'"
fi

current_branch="$(git rev-parse --abbrev-ref HEAD)"
[[ "$current_branch" == "master" ]] || die "run this from master, currently on '$current_branch'"

if [[ -n "$(git status --porcelain)" ]]; then
  die "worktree has uncommitted changes; commit or stash them first"
fi

info "pulling master (semantic-release's version-bump commit + tag)"
git pull --ff-only origin master

version="$(
  python3 - "CouchSpire.json" <<'PY'
import json
import sys

with open(sys.argv[1], "r", encoding="utf-8") as handle:
    print(json.load(handle).get("version", ""))
PY
)"
[[ -n "$version" ]] || die "CouchSpire.json has no version field"
tag="v$version"

if ! git ls-remote --exit-code --tags origin "refs/tags/$tag" >/dev/null 2>&1; then
  die "remote tag '$tag' not found; has the Release workflow finished for this merge? (check the Actions tab)"
fi

if ! gh release view "$tag" >/dev/null 2>&1; then
  die "no GitHub Release found for '$tag' yet; the Release workflow may still be running"
fi

info "packaging $tag (main and beta game branches)"
zip_paths=()
while IFS= read -r line; do
  zip_paths+=("$line")
done < <(tools/package.sh | tail -n 2)
[[ "${#zip_paths[@]}" -eq 2 && -f "${zip_paths[0]}" && -f "${zip_paths[1]}" ]] || die "tools/package.sh did not produce both zips"

info "uploading ${zip_paths[*]} to release $tag"
gh release upload "$tag" "${zip_paths[@]}" --clobber

release_url="$(gh release view "$tag" --json url --jq .url)"
info "done: $release_url"
