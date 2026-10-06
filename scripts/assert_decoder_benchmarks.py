"""Validate complete BDN evidence and the decoder's bracketing-control gate."""

import argparse
import json
import math
from pathlib import Path


METHODS = {
    "DecodeAsciiBulkString", "ReadAsciiBulkString", "DecodeUnicodeBulkString",
    "ReadUnicodeBulkString", "BuildGetCommand", "BuildSetCommand",
    "FormatAsciiBulkString", "FormatLargeAsciiBulkString", "FormatUnicodeBulkString",
    "FormatLargeUnicodeBulkString",
}


def finite(value):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError("Missing or nonfinite benchmark statistic")
    return value


def index_cases(cases, measured=True):
    indexed = {}
    for case in cases:
        method = case["Method"]
        if method in indexed:
            raise ValueError(f"Duplicate method: {method}")
        statistics = case["Statistics"]
        mean = finite(statistics["Mean"])
        if mean <= 0:
            raise ValueError(f"Nonpositive mean: {method}")
        if measured:
            interval = statistics["ConfidenceInterval"]
            lower, upper = finite(interval["Lower"]), finite(interval["Upper"])
            if not lower <= mean <= upper or finite(statistics["N"]) < 2:
                raise ValueError(f"Invalid measured interval: {method}")
            if finite(case["Memory"]["BytesAllocatedPerOperation"]) < 0:
                raise ValueError(f"Negative allocation: {method}")
        indexed[method] = case
    if set(indexed) != METHODS:
        raise ValueError(f"Incomplete method set: {set(indexed)}")
    return indexed


def assess(baseline_a, candidate, baseline_b, require_improvement=False):
    phases = [index_cases(cases) for cases in (baseline_a, candidate, baseline_b)]
    errors = []
    for method in sorted(METHODS):
        a, c, b = [phase[method] for phase in phases]
        lower = c["Statistics"]["ConfidenceInterval"]["Lower"]
        if all(lower > control["Statistics"]["ConfidenceInterval"]["Upper"] for control in (a, b)):
            errors.append(f"{method}: candidate interval wholly above both controls")
        allocated = c["Memory"]["BytesAllocatedPerOperation"]
        if any(allocated > control["Memory"]["BytesAllocatedPerOperation"] for control in (a, b)):
            errors.append(f"{method}: candidate allocation exceeds a control")
    if require_improvement:
        a, c, b = [phase["FormatAsciiBulkString"] for phase in phases]
        upper = c["Statistics"]["ConfidenceInterval"]["Upper"]
        if not all(upper < control["Statistics"]["ConfidenceInterval"]["Lower"] for control in (a, b)):
            errors.append("FormatAsciiBulkString: no small-ASCII benefit against both controls")
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--require-ascii-improvement", action="store_true")
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--manifest", type=Path)
    args = parser.parse_args()
    phases = {}
    for phase in ("baseline-validation", "candidate-validation", "baseline-a", "candidate", "baseline-b"):
        reports = list((args.results / phase / "results").glob("*-report-full-compressed.json"))
        if len(reports) != 1:
            raise ValueError(f"Missing or duplicate report: {phase}")
        phases[phase] = json.loads(reports[0].read_text())["Benchmarks"]
        index_cases(phases[phase], measured="validation" not in phase)
    errors = assess(phases["baseline-a"], phases["candidate"], phases["baseline-b"], args.require_ascii_improvement)
    if args.summary:
        with args.summary.open("a") as summary:
            summary.write("## Isolated UTF-8 decoder comparison\n\n")
            summary.write("The production candidate is the pinned PR head. Both controls use its common base; fixtures are identical. "
                          "Local-initialization policy is unchanged. These ten operations do not measure network throughput.\n\n")
            if args.manifest:
                manifest = json.loads(args.manifest.read_text())
                summary.write(f"Base: `{manifest['comparison_base_sha']}`. Head: `{manifest['head_sha']}`.\n\n")
            for phase in ("baseline-a", "candidate", "baseline-b"):
                markdown = list((args.results / phase / "results").glob("*-report-github.md"))
                if len(markdown) != 1:
                    raise ValueError(f"Missing or duplicate Markdown report: {phase}")
                summary.write(f"### {phase}\n\n" + markdown[0].read_text() + "\n")
            summary.write("\n".join(errors) if errors else "All latency and allocation gates pass.\n")
    for error in errors:
        print(error)
    return 1 if errors else 0


if __name__ == "__main__":
    raise SystemExit(main())
