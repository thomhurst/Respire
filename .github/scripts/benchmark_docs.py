#!/usr/bin/env python3
"""Generate the published benchmark page from BenchmarkDotNet artifacts."""

from __future__ import annotations

import argparse
from collections import defaultdict
import json
from pathlib import Path
import re


ARTIFACT_PREFIX = "comparison-benchmarks-"
REPORT_SUFFIX = "-report-github.md"
CHART_IMPORT = "import ComparisonBarChart from '@site/src/components/ComparisonBarChart';"
CACHE_COMPARISON_NOTE = (
    "StackExchange.Redis has no built-in server-assisted client cache, so its values are "
    "ordinary server reads. Respire server reads are included for a like-for-like "
    "uncached comparison."
)
COMMON_OPERATION_CATEGORIES = {
    "GET",
    "GET x200 pipelined",
    "GET x50 concurrent",
    "HGET",
    "HSET",
    "LPUSH+LPOP",
    "SET 1KB",
}
DURATION_MULTIPLIERS_NS = {
    "ns": 1,
    "μs": 1_000,
    "us": 1_000,
    "ms": 1_000_000,
    "s": 1_000_000_000,
}


def _framework_for(report: Path, results_dir: Path) -> str:
    for part in report.relative_to(results_dir).parts:
        if part.startswith(ARTIFACT_PREFIX):
            return part.removeprefix(ARTIFACT_PREFIX)
    return "unknown framework"


def _report_name(report: Path) -> str:
    name = report.name.removesuffix(REPORT_SUFFIX)
    return name.rsplit(".", maxsplit=1)[-1]


def _table_rows(markdown: str) -> list[dict[str, str]]:
    lines = markdown.splitlines()
    for index, line in enumerate(lines):
        if not line.strip().startswith("| Method"):
            continue

        headers = [cell.strip() for cell in line.strip().strip("|").split("|")]
        rows: list[dict[str, str]] = []
        for row_line in lines[index + 2 :]:
            if not row_line.strip().startswith("|"):
                break
            cells = [cell.strip() for cell in row_line.strip().strip("|").split("|")]
            if len(cells) == len(headers) and any(cells):
                rows.append(dict(zip(headers, cells)))
        return rows

    return []


def _duration_ns(value: str) -> float | None:
    try:
        number, unit = value.replace(",", "").split()
        return float(number) * DURATION_MULTIPLIERS_NS[unit]
    except (KeyError, ValueError):
        return None


def _chart_rows(report_name: str, report: str) -> list[dict[str, str | float]]:
    rows = _table_rows(report)
    baselines: dict[str, float] = {}
    respire_server_reads: dict[str, float] = {}
    for row in rows:
        method = row.get("Method", "")
        duration = _duration_ns(row.get("Mean", ""))
        if duration is None:
            continue
        if method.startswith("StackExchange_"):
            baselines[row.get("Categories", "")] = duration
        elif method.startswith("Respire_") and method.endswith("_ServerRead"):
            respire_server_reads[row.get("Categories", "")] = duration

    chart_rows: list[dict[str, str | float]] = []
    for row in rows:
        method = row.get("Method", "")
        category = row.get("Categories", "")
        mean = _duration_ns(row.get("Mean", ""))
        baseline = baselines.get(category)
        if not method.startswith("Respire_") or mean is None or baseline is None:
            continue

        respire_server: float | None = None
        if report_name == "ClientSideCachingBenchmarks":
            if not method.endswith("_ClientCacheHit"):
                continue
            respire_server = respire_server_reads.get(category)
            if respire_server is None:
                continue
            label = category
        elif report_name == "CommonOperationsBenchmarks":
            if category not in COMMON_OPERATION_CATEGORIES:
                continue
            label = category
        else:
            continue

        chart_row: dict[str, str | float] = {
            "label": label,
            "other": baseline,
            "respire": mean,
        }
        if respire_server is not None:
            chart_row["respireServer"] = respire_server
        chart_rows.append(chart_row)
    return chart_rows


def _framework_key(framework: str) -> tuple[int, ...]:
    versions = re.findall(r"\d+", framework)
    return tuple(int(version) for version in versions)


def _summary_charts(grouped: dict[str, list[Path]]) -> list[str]:
    framework = max(grouped, key=_framework_key)
    charts = ["## Visual comparison", ""]
    for report in sorted(grouped[framework]):
        report_name = _report_name(report)
        rows = _chart_rows(report_name, report.read_text(encoding="utf-8"))
        if not rows:
            continue

        if report_name == "ClientSideCachingBenchmarks":
            title = f"Client-cache hit time — {framework}"
            description = (
                "StackExchange.Redis and Respire server reads vs Respire client-cache hit. "
                "Shorter bars are faster."
            )
            charts.extend(
                [
                    CACHE_COMPARISON_NOTE,
                    "",
                ]
            )
        else:
            title = f"Selected operation time — {framework}"
            description = "Mean time. Shorter bars are faster."
        chart = [
            "<ComparisonBarChart",
            f"  title={json.dumps(title, ensure_ascii=False)}",
            f"  description={json.dumps(description)}",
            '  format="duration-ns"',
        ]
        if report_name == "ClientSideCachingBenchmarks":
            chart.append('  respireLabel="Respire cache hit"')
        chart.extend(
            [
                '  scale="group"',
                "  showRatio",
                f"  data={{{json.dumps(rows, ensure_ascii=False, separators=(',', ':'))}}}",
                "/>",
                "",
            ]
        )
        charts.extend(chart)
    return charts if len(charts) > 2 else []


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
        CHART_IMPORT,
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

    lines.extend(_summary_charts(grouped))

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
