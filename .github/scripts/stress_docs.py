#!/usr/bin/env python3
"""Generate the published stress-test page from a complete stress report."""

from __future__ import annotations

import argparse
from pathlib import Path


REPORT_NAME = "stress-report.md"


def _without_title(report: str) -> str:
    lines = report.strip().splitlines()
    if lines and lines[0].startswith("# "):
        lines = lines[1:]
    while lines and not lines[0].strip():
        lines.pop(0)
    return "\n".join(lines)


def build_document(report: str, commit: str, generated_at: str, run_url: str) -> str:
    body = _without_title(report)
    if not body:
        raise ValueError("Stress report is empty")

    lines = [
        "---",
        "title: Stress tests",
        "description: Latest sustained Respire and StackExchange.Redis stress-test results.",
        "---",
        "",
        "# Stress tests",
        "",
        ":::info Automated results",
        (
            f"Generated {generated_at} from commit `{commit[:12]}`. "
            f"See the [GitHub Actions run]({run_url}) for logs, JSON results, and downloadable artifacts."
        ),
        ":::",
        "",
        body,
        "",
    ]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-dir", required=True, type=Path)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--generated-at", required=True)
    parser.add_argument("--run-url", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    reports = list(args.results_dir.rglob(REPORT_NAME))
    if len(reports) != 1:
        raise ValueError(
            f"Expected one {REPORT_NAME} below {args.results_dir}, found {len(reports)}"
        )

    document = build_document(
        reports[0].read_text(encoding="utf-8"),
        args.commit,
        args.generated_at,
        args.run_url,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(document, encoding="utf-8", newline="\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
