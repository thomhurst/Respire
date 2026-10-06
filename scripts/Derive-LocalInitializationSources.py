"""Prepare identical pinned sources that differ only in the module attribute."""

import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

MODULE = "src/Shared/SkipLocalsInit.cs"
DECODER = "src/Respire/Internal/Utf8String.cs"
FIXTURE = "benchmarks/Respire.Benchmarks/ProtocolBenchmarks.cs"


def derive(root, head):
    assert re.fullmatch(r"[0-9a-f]{40}", head), "Expected an immutable full head SHA"

    def git(source, *arguments):
        return subprocess.check_output(["git", "-C", str(root / source), *arguments])

    for source in ("baseline", "candidate"):
        assert git(source, "rev-parse", "HEAD").decode().strip() == head
        assert not git(source, "status", "--porcelain").strip()
    assert (root / "candidate" / MODULE).read_text().strip() == "using System.Runtime.CompilerServices;\n\n[module: SkipLocalsInit]"
    (root / "baseline" / MODULE).write_text("// Bracketing control: retain implicit local initialization.\n")
    text = (root / "candidate" / FIXTURE).read_text()
    job = "[SimpleJob(warmupCount: 3, iterationCount: 10)]"
    assert text.count(job) == 1, "Review fixture jobs before modifying the comparison"
    for source in ("baseline", "candidate"):
        (root / source / FIXTURE).write_text(text.replace(job, ""))
    tracked = git("candidate", "ls-files", "-z").decode().split("\0")
    differences = [path for path in tracked if path and (root / "candidate" / path).is_file()
                   and (root / "baseline" / path).read_bytes() != (root / "candidate" / path).read_bytes()]
    assert differences == [MODULE], differences
    manifest = {"head_sha": head, "only_source_difference": MODULE, "sources": {}}
    for source in ("baseline", "candidate"):
        patch = git(source, "diff", "--binary")
        (root / f"{source}.patch").write_bytes(patch)
        manifest["sources"][source] = {
            "patch_sha256": hashlib.sha256(patch).hexdigest(),
            "files": {path: hashlib.sha256((root / source / path).read_bytes()).hexdigest()
                      for path in (MODULE, DECODER, FIXTURE)}}
    (root / "source-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")


if __name__ == "__main__":
    derive(Path.cwd(), sys.argv[1])
