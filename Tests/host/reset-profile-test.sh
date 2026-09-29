#!/usr/bin/env bash
# Exercises deploy.sh's reset_test_profile (the test-profile reset/seed logic and its safety refusals; see
# docs/testing.md) against a
# throwaway fake tree in mktemp -d. Never touches the real user-data root or launches the game.
#
# Run: bash Tests/host/reset-profile-test.sh
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

# shellcheck source=/dev/null
source "$repo_root/deploy.sh"

fail() {
  echo "FAIL: $*" >&2
  exit 1
}

pass() {
  echo "ok: $*"
}

fake_home="$(mktemp -d)"
trap 'rm -rf "$fake_home"' EXIT

root="$fake_home/SlayTheSpire2"
steam_id_dir="$root/steam/76500000000000001"
mkdir -p "$steam_id_dir"
echo "fake-settings-v1" > "$steam_id_dir/settings.save"

target="$root/default/1"
err_file="$fake_home/err.txt"

# 1. Seed: settings.save is missing, gets copied from steam/<id>/.
reset_test_profile "$target" "$root"
[ -f "$target/settings.save" ] || fail "settings.save was not seeded"
[ "$(cat "$target/settings.save")" = "fake-settings-v1" ] || fail "seeded settings.save has wrong content"
pass "seeds settings.save from steam/<id>/ when missing"

# 2. Deletion: junk under default/1/ is removed, settings.save survives, and re-running is a no-op re: seeding.
mkdir -p "$target/modded/profile1"
echo "junk" > "$target/modded/profile1/save.dat"
echo "junk" > "$target/other-file.save"
reset_test_profile "$target" "$root"
[ -f "$target/settings.save" ] || fail "settings.save lost on reset"
[ "$(cat "$target/settings.save")" = "fake-settings-v1" ] || fail "settings.save was re-seeded/overwritten unexpectedly"
[ ! -e "$target/modded" ] || fail "modded/ survived the reset"
[ ! -e "$target/other-file.save" ] || fail "other-file.save survived the reset"
pass "reset deletes everything except settings.save, and doesn't touch an existing settings.save"

# 3. Refusal: wrong path (target isn't <root>/default/1).
if reset_test_profile "$root/default/2" "$root" 2>"$err_file"; then
  fail "wrong-path target was not refused"
fi
grep -q "not exactly" "$err_file" || fail "wrong-path refusal message unexpected: $(cat "$err_file")"
pass "refuses a target that isn't <root>/default/1"

# 4. Refusal: empty root.
if reset_test_profile "$target" "" 2>"$err_file"; then
  fail "empty root was not refused"
fi
grep -q "empty" "$err_file" || fail "empty-root refusal message unexpected: $(cat "$err_file")"
pass "refuses an empty root"

# 5. Refusal: a target under steam/, and it's left untouched.
if reset_test_profile "$root/steam/76500000000000001" "$root" 2>"$err_file"; then
  fail "steam/ target was not refused"
fi
grep -q "steam" "$err_file" || fail "steam/ refusal message unexpected: $(cat "$err_file")"
[ -f "$root/steam/76500000000000001/settings.save" ] || fail "steam/ settings.save was touched"
pass "refuses a target under steam/ and leaves it untouched"

echo "All reset_test_profile checks passed."
