#!/usr/bin/env python3
"""Bump the "CouchSpire X.Y.Z" prefix in Entry.cs's BuildMarker string in place, leaving the
rest of the marker (game version) untouched. Used by semantic-release's @semantic-release/exec
prepare step — see .releaserc.json.

BuildMarker looks like:
    private const string BuildMarker = "CouchSpire 0.1.0 loaded (game v0.111.0)";
"""
import re
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: bump-build-marker.py <Entry.cs> <version>", file=sys.stderr)
        return 2

    entry_path, version = sys.argv[1], sys.argv[2]

    with open(entry_path, "r", encoding="utf-8") as handle:
        text = handle.read()

    pattern = re.compile(r'(BuildMarker = "CouchSpire )\d+\.\d+\.\d+( loaded)')
    new_text, count = pattern.subn(rf"\g<1>{version}\g<2>", text)

    if count != 1:
        print(
            f"bump-build-marker: expected exactly one BuildMarker match in {entry_path}, found {count}",
            file=sys.stderr,
        )
        return 1

    with open(entry_path, "w", encoding="utf-8") as handle:
        handle.write(new_text)

    print(f"bump-build-marker: {entry_path} BuildMarker version -> {version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
