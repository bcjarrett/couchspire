#!/usr/bin/env python3
"""Set the "version" field in a mod manifest JSON file in place (used by semantic-release's
@semantic-release/exec prepare step — see .releaserc.json)."""
import json
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: bump-manifest-version.py <manifest.json> <version>", file=sys.stderr)
        return 2

    manifest_path, version = sys.argv[1], sys.argv[2]

    with open(manifest_path, "r", encoding="utf-8") as handle:
        data = json.load(handle)

    data["version"] = version

    with open(manifest_path, "w", encoding="utf-8") as handle:
        json.dump(data, handle, indent=4)
        handle.write("\n")

    print(f"bump-manifest-version: {manifest_path} version -> {version}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
