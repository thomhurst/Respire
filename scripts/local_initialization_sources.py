"""Derive an attribute-only SkipLocalsInit comparison from one pinned source tree (#1065).

Both trees start as the same commit. The candidate adds a probe module attribute to the
core Respire assembly and the baseline is unchanged. Shipped attributes are rejected:
the protocol probe cannot establish timing coverage for every method in a proposed scope.
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

ATTRIBUTE_LINE = re.compile(
    r"^[ \t]*\[(?:module:[ \t]*)?(?:global::)?(?:System\.Runtime\.CompilerServices\.)?SkipLocalsInit(?:Attribute)?(?:\(\))?\][ \t]*\r?\n",
    re.MULTILINE)
MENTION = re.compile(r"^(?![ \t]*//).*\bSkipLocalsInit", re.MULTILINE)
PROBE_PATH = Path("src/Respire/LocalInitializationProbe.cs")
PROBE = "// Benchmark-only probe written by benchmark-local-initialization.yml; never committed.\n" \
        "[module: System.Runtime.CompilerServices.SkipLocalsInit]\n"


def strip_attributes(text):
    """Removes whole-line SkipLocalsInit attributes and rejects any other mention outside a line comment."""
    stripped, count = ATTRIBUTE_LINE.subn("", text)
    if MENTION.search(stripped):
        raise ValueError("SkipLocalsInit appears outside a standalone attribute line; isolate it before measuring")
    return stripped, count


def source_files(root):
    return sorted(path for path in (root / "src").rglob("*.cs")
                  if not {"bin", "obj"} & set(path.relative_to(root).parts))


def derive(baseline, candidate):
    """Rewrites the baseline or candidate tree in place and returns the mode and changed paths."""
    for path in source_files(baseline):
        with path.open(encoding="utf-8", newline="") as source:
            text = source.read()
        if not MENTION.search(text):
            continue
        _, count = strip_attributes(text)
        if count:
            raise ValueError(f"Uncovered shipped SkipLocalsInit scope in {path.relative_to(baseline)}; "
                             "select benchmarks covering every affected method before measuring a proposal")
    (candidate / PROBE_PATH).write_text(PROBE, encoding="utf-8", newline="")
    return "probe", [PROBE_PATH.as_posix()]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("candidate", type=Path)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    args = parser.parse_args()
    for tree in (args.baseline, args.candidate):
        head = subprocess.check_output(["git", "-C", str(tree), "rev-parse", "HEAD"], text=True).strip()
        if head != args.head_sha:
            raise SystemExit(f"{tree} is at {head}, expected {args.head_sha}")
    mode, changed = derive(args.baseline, args.candidate)
    diff = subprocess.run(["git", "diff", "--no-index", "--", str(args.baseline / "src"), str(args.candidate / "src")],
                          capture_output=True, text=True).stdout
    manifest = {
        "head_sha": args.head_sha,
        "mode": mode,
        "changed": changed,
        "source_diff_sha256": hashlib.sha256(diff.encode()).hexdigest(),
    }
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")
    Path(args.manifest.with_suffix(".patch")).write_text(diff)
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
