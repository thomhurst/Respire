#!/usr/bin/env python3
"""Generate the published benchmark page from BenchmarkDotNet artifacts."""

from __future__ import annotations

import argparse
from collections import defaultdict
from pathlib import Path


ARTIFACT_PREFIX = "comparison-benchmarks-"
REPORT_SUFFIX = "-report-github.md"


def _framework_for(report: Path, results_dir: Path) -> str:
    for part in report.relative_to(results_dir).parts:
        if part.startswith(ARTIFACT_PREFIX):
            return part.removeprefix(ARTIFACT_PREFIX)
    return "unknown framework"


def _report_name(report: Path) -> str:
    name = report.name.removesuffix(REPORT_SUFFIX)
    return name.rsplit(".", maxsplit=1)[-1]


def build_document(
    reports: list[Path],
    results_dir: Path,
    commit: str,
    generated_at: str,
    run_url: str,
) -> str:
    if not reports:
        raise ValueError(f"No BenchmarkDotNet reports found below {results_dir}")

    grouped: dict[str, list[Path]] = defaultdict(list)
    for report in reports:
        grouped[_framework_for(report, results_dir)].append(report)

    lines = [
        "---",
        "title: Benchmarks",
        "description: Latest automated Respire and StackExchange.Redis benchmark results.",
        "---",
        "",
        "# Benchmarks",
        "",
        ":::info Automated results",
        (
            f"Generated {generated_at} from commit `{commit[:12]}`. "
            f"See the [GitHub Actions run]({run_url}) for logs and downloadable artifacts."
        ),
        ":::",
        "",
        "StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.",
        "",
    ]

    for framework in sorted(grouped):
        lines.extend([f"## {framework}", ""])
        framework_reports = sorted(grouped[framework])
        for report in framework_reports:
            if len(framework_reports) > 1:
                lines.extend([f"### {_report_name(report)}", ""])
            lines.extend([report.read_text(encoding="utf-8").strip(), ""])

    lines.extend(
        [
            "## Reading the results",
            "",
            "Treat shared-runner measurements as directional evidence. Validate important decisions against your payload sizes, concurrency, Redis deployment, and network.",
            "",
        ]
    )
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", required=True, type=Path)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--generated-at", required=True)
    parser.add_argument("--run-url", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    reports = list(args.results_dir.rglob(f"*{REPORT_SUFFIX}"))
    document = build_document(
        reports,
        args.results_dir,
        args.commit,
        args.generated_at,
        args.run_url,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(document, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
