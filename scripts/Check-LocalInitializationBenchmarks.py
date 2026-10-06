"""Validate the attribute-only CI comparison; never rerun measurements for green."""

import hashlib
import json
import math
import os
from pathlib import Path

METHODS = {
    "DecodeAsciiBulkString", "ReadAsciiBulkString", "DecodeUnicodeBulkString",
    "ReadUnicodeBulkString", "FormatAsciiBulkString", "FormatLargeAsciiBulkString",
    "FormatUnicodeBulkString", "FormatLargeUnicodeBulkString", "BuildGetCommand", "BuildSetCommand",
}


def interval_regressed(before, candidate, after):
    return candidate["Lower"] > max(before["Upper"], after["Upper"])


def validate(root):
    manifest = json.loads((root / "source-manifest.json").read_text())
    for source, details in manifest["sources"].items():
        assert hashlib.sha256((root / f"{source}.patch").read_bytes()).hexdigest() == details["patch_sha256"]
        for path, expected in details["files"].items():
            assert hashlib.sha256((root / source / path).read_bytes()).hexdigest() == expected, (source, path)
    for path in ("src/Respire/Internal/Utf8String.cs", "benchmarks/Respire.Benchmarks/ProtocolBenchmarks.cs"):
        assert manifest["sources"]["baseline"]["files"][path] == manifest["sources"]["candidate"]["files"][path]

    phases = {}
    summary = ["## Attribute-only local initialization\n",
               f"Pinned head: `{manifest['head_sha']}`. Both controls disable only the module attribute. "
               "Decoder and fixtures are identical. Intervals are BDN's 99.9% confidence intervals. "
               "JIT listings are archived in phase logs and still require code-generation review.\n"]
    for phase in ("baseline-validation", "candidate-validation", "baseline-a", "candidate", "baseline-b"):
        log = (root / f"{phase}.log").read_text()
        assert log.splitlines()[0] == manifest["head_sha"], phase
        assert "Assembly listing for method" in log, f"Missing JIT evidence: {phase}"
        reports = list((root / "results" / phase / "results").glob("*-report-full-compressed.json"))
        assert len(reports) == 1, f"Missing or duplicate report: {phase}"
        cases = json.loads(reports[0].read_text())["Benchmarks"]
        assert len(cases) == 10 and {case["Method"] for case in cases} == METHODS, phase
        for case in cases:
            stats = case.get("Statistics")
            assert stats and math.isfinite(stats["Mean"]) and stats["Mean"] > 0, (phase, case["Method"])
            assert "BytesAllocatedPerOperation" in case.get("Memory", {}), (phase, case["Method"])
        phases[phase] = {case["Method"]: case for case in cases}

    failures = []
    summary.append("\n| Operation | Baseline A ns | Candidate ns | Baseline B ns | SD A/C/B ns | Allocated A/C/B | Gate |\n"
                   "| --- | --- | --- | --- | --- | --- | --- |\n")
    for method in sorted(METHODS):
        cases = [phases[phase][method] for phase in ("baseline-a", "candidate", "baseline-b")]
        stats = [case["Statistics"] for case in cases]
        intervals = [item["ConfidenceInterval"] for item in stats]
        for interval in intervals:
            assert all(math.isfinite(interval[bound]) for bound in ("Lower", "Upper")), method
            assert interval["Lower"] <= interval["Upper"], method
        allocated = [case["Memory"]["BytesAllocatedPerOperation"] for case in cases]
        assert all(math.isfinite(value) and value >= 0 for value in allocated), method
        regression = interval_regressed(*intervals)
        allocation_regression = allocated[1] > max(allocated[0], allocated[2])
        if regression or allocation_regression:
            failures.append(f"{method}: latency={regression}, allocations={allocation_regression}")
        timings = [f"{item['Mean']:.3f} [{ci['Lower']:.3f}, {ci['Upper']:.3f}]" for item, ci in zip(stats, intervals)]
        gate = "FAIL" if regression or allocation_regression else "PASS"
        dispersion = '/'.join(f"{item['StandardDeviation']:.3f}" for item in stats)
        summary.append(f"| {method} | {' | '.join(timings)} | {dispersion} | "
                       f"{'/'.join(str(value) for value in allocated)} | {gate} |\n")
    text = "".join(summary)
    print(text)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as destination:
            destination.write(text)
    assert not failures, "Performance acceptance failed; investigate without rerunning for green: " + "; ".join(failures)


if __name__ == "__main__":
    validate(Path.cwd())
