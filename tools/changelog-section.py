#!/usr/bin/env python3
"""Print the CHANGELOG.md section (level-2 heading) whose title contains a given version string."""
import re
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: changelog-section.py <changelog-path> <version>", file=sys.stderr)
        return 2

    changelog_path, version = sys.argv[1], sys.argv[2]
    with open(changelog_path, "r", encoding="utf-8") as handle:
        lines = handle.readlines()

    start = None
    for i, line in enumerate(lines):
        if line.startswith("## ") and version in line:
            start = i
            break

    if start is None:
        return 1

    end = len(lines)
    for i in range(start + 1, len(lines)):
        if lines[i].startswith("## "):
            end = i
            break

    section = "".join(lines[start:end]).strip()
    print(section)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
