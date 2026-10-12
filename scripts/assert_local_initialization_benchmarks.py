"""Validate the attribute-only SkipLocalsInit comparison and apply the #1065 bracketing gate.

The gate is the one #1065 sets: no candidate confidence interval may lie wholly above both
bracketing controls on any of the ten single-operation rows, and no candidate allocation may
exceed a control. With --report-only the result is summarized but never fails the job.
"""

import argparse
import importlib.util
import json
from pathlib import Path

_spec = importlib.util.spec_from_file_location("decoder_gate", Path(__file__).with_name("assert_decoder_benchmarks.py"))
_gate = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_gate)

METHODS = _gate.METHODS
PHASES = ("baseline-validation", "candidate-validation", "baseline-a", "candidate", "baseline-b")


def assess(baseline_a, candidate, baseline_b):
    return _gate.assess(baseline_a, candidate, baseline_b)


def ratios(baseline_a, candidate, baseline_b):
    """Candidate mean over the mean of both controls, per method, for the summary table."""
    phases = [_gate.index_cases(cases) for cases in (baseline_a, candidate, baseline_b)]
    result = {}
    for method in sorted(METHODS):
        a, c, b = (phase[method]["Statistics"]["Mean"] for phase in phases)
        result[method] = c / ((a + b) / 2)
    return result


def load(results):
    phases = {}
    for phase in PHASES:
        reports = list((results / phase / "results").glob("*-report-full-compressed.json"))
        if len(reports) != 1:
            raise ValueError(f"Missing or duplicate report: {phase}")
        phases[phase] = json.loads(reports[0].read_text())["Benchmarks"]
        _gate.index_cases(phases[phase], measured="validation" not in phase)
    return phases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("results", type=Path)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--label", default="")
    parser.add_argument("--summary", type=Path)
    parser.add_argument("--report-only", action="store_true")
    args = parser.parse_args()
    phases = load(args.results)
    errors = assess(phases["baseline-a"], phases["candidate"], phases["baseline-b"])
    manifest = json.loads(args.manifest.read_text())
    if args.summary:
        with args.summary.open("a") as summary:
            summary.write(f"## Attribute-only local-initialization comparison {args.label}\n\n")
            summary.write(f"Head `{manifest['head_sha']}`, mode `{manifest['mode']}`; the only source difference is "
                          f"`SkipLocalsInit` in {', '.join(f'`{path}`' for path in manifest['changed'])}.\n\n")
            summary.write("| Method | Candidate / mean of controls |\n| --- | --- |\n")
            for method, ratio in ratios(phases["baseline-a"], phases["candidate"], phases["baseline-b"]).items():
                summary.write(f"| {method} | {ratio:.3f} |\n")
            summary.write("\n")
            for phase in ("baseline-a", "candidate", "baseline-b"):
                markdown = list((args.results / phase / "results").glob("*-report-github.md"))
                if len(markdown) != 1:
                    raise ValueError(f"Missing or duplicate Markdown report: {phase}")
                summary.write(f"### {phase}\n\n" + markdown[0].read_text() + "\n")
            summary.write(("\n".join(errors) if errors else "All latency and allocation gates pass.") + "\n")
    for error in errors:
        print(error)
    return 0 if args.report_only or not errors else 1


if __name__ == "__main__":
    raise SystemExit(main())
