#!/usr/bin/env python3
"""Print the CHANGELOG.md section for a version. semantic-release heads minor/major releases with a level-1
heading ("# [0.3.0](...)") and patches with level 2 ("## [0.3.1](...)"); hand-written sections use "## 0.1.0"."""
import re
import sys


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: changelog-section.py <changelog-path> <version>", file=sys.stderr)
        return 2

    changelog_path, version = sys.argv[1], sys.argv[2]
    with open(changelog_path, "r", encoding="utf-8") as handle:
        lines = handle.readlines()

    # Match the version right after the heading marker, not anywhere in the line: a heading's compare link
    # ("compare/v0.2.0...v0.3.0") also contains the previous version.
    heading = re.compile(r"^#{1,2} \[?" + re.escape(version) + r"[\]\s(]")
    start = None
    for i, line in enumerate(lines):
        if heading.match(line):
            start = i
            break

    if start is None:
        return 1

    end = len(lines)
    for i in range(start + 1, len(lines)):
        if lines[i].startswith(("# ", "## ")):
            end = i
            break

    section = "".join(lines[start:end]).strip()
    print(section)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
