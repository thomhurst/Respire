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


ARTIFACT_PATTERN = re.compile(r"^(?P<framework>.+?)(?:-round-(?P<round>\d+))?(?:-seredis-.+)?$")


def _artifact_suffix(report: Path, results_dir: Path) -> str | None:
    for part in report.relative_to(results_dir).parts:
        if part.startswith(ARTIFACT_PREFIX):
            return part.removeprefix(ARTIFACT_PREFIX)
    return None


def _round_for(report: Path, results_dir: Path) -> int:
    suffix = _artifact_suffix(report, results_dir)
    match = ARTIFACT_PATTERN.match(suffix) if suffix else None
    return int(match.group("round")) if match and match.group("round") else 1


def _framework_for(report: Path, results_dir: Path) -> str:
    suffix = _artifact_suffix(report, results_dir)
    if suffix:
        match = ARTIFACT_PATTERN.match(suffix)
        return match.group("framework") if match else suffix
    # download-artifact extracts a lone matching artifact without its folder, so fall back
    # to the runtime BenchmarkDotNet records in the report header.
    runtime = re.search(r"\.NET (\d+\.\d+)", report.read_text(encoding="utf-8"))
    if runtime:
        return f"net{runtime.group(1)}"
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


def _median_index(values: list[float]) -> int:
    """Index of the median value; the lower middle value when the count is even."""
    order = sorted(range(len(values)), key=lambda index: values[index])
    return order[(len(order) - 1) // 2]


def _median_chart_rows(
    report_name: str, runs: list[tuple[int, Path]]
) -> list[dict[str, str | float]]:
    """Chart rows taken from the run whose Respire/StackExchange ratio is the median.

    Each run measures both clients on the same machine, so every published pair is one
    real measurement rather than numbers mixed from different machines.
    """
    per_run = [
        {row["label"]: row for row in _chart_rows(report_name, path.read_text(encoding="utf-8"))}
        for _, path in runs
    ]
    if not per_run:
        return []

    labels = list(dict.fromkeys(label for rows in per_run for label in rows))
    chart_rows: list[dict[str, str | float]] = []
    for label in labels:
        candidates = [rows[label] for rows in per_run if label in rows]
        ratios = [float(row["respire"]) / float(row["other"]) for row in candidates]
        chart_rows.append(candidates[_median_index(ratios)])
    return chart_rows


def _runner_cpu(report: str) -> str | None:
    match = re.search(r"^(?:AMD|Intel|Apple|Ampere|ARM|Neoverse)[^\n,]*", report, re.MULTILINE)
    return match.group(0).strip() if match else None


def _ratios(report: str) -> dict[tuple[str, str], float]:
    rows = _table_rows(report)
    baselines: dict[str, float] = {}
    for row in rows:
        duration = _duration_ns(row.get("Mean", ""))
        if duration is not None and row.get("Method", "").startswith("StackExchange_"):
            baselines[row.get("Categories", "")] = duration

    ratios: dict[tuple[str, str], float] = {}
    for row in rows:
        method = row.get("Method", "")
        category = row.get("Categories", "")
        mean = _duration_ns(row.get("Mean", ""))
        baseline = baselines.get(category)
        if method.startswith("Respire_") and mean is not None and baseline:
            ratios[(method, category)] = mean / baseline
    return ratios


def _ratio_summary(report_name: str, runs: list[tuple[int, Path]]) -> list[str]:
    per_run = [_ratios(path.read_text(encoding="utf-8")) for _, path in runs]
    keys = list(dict.fromkeys(key for ratios in per_run for key in ratios))
    if not keys:
        return []

    has_categories = any(category for _, category in keys)
    run_headers = [f"Run {run}" for run, _ in runs]
    header = ["Method", *(["Categories"] if has_categories else []), *run_headers, "Median"]
    alignment = ["---"] * (2 if has_categories else 1) + ["---:"] * (len(run_headers) + 1)
    lines = [
        f"### {report_name}",
        "",
        "| " + " | ".join(header) + " |",
        "|" + "|".join(alignment) + "|",
    ]
    for method, category in keys:
        values = [ratios.get((method, category)) for ratios in per_run]
        present = [value for value in values if value is not None]
        median = present[_median_index(present)]
        cells = [method, *([category] if has_categories else [])]
        cells.extend("-" if value is None else f"{value:.2f}" for value in values)
        cells.append(f"**{median:.2f}**")
        lines.append("| " + " | ".join(cells) + " |")
    lines.append("")
    return lines


def _summary_charts(framework: str, by_report: dict[str, list[tuple[int, Path]]]) -> list[str]:
    charts = ["## Visual comparison", ""]
    if any(len(runs) > 1 for runs in by_report.values()):
        charts.extend(
            [
                "Each pair of bars comes from the run whose Respire / StackExchange.Redis ratio is the median of all runs.",
                "",
            ]
        )
    for report_name in sorted(by_report):
        rows = _median_chart_rows(report_name, by_report[report_name])
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


def _group(reports: list[Path], results_dir: Path) -> dict[str, dict[str, list[tuple[int, Path]]]]:
    grouped: dict[str, dict[str, list[tuple[int, Path]]]] = defaultdict(lambda: defaultdict(list))
    for report in reports:
        framework = _framework_for(report, results_dir)
        grouped[framework][_report_name(report)].append((_round_for(report, results_dir), report))
    for by_report in grouped.values():
        for runs in by_report.values():
            runs.sort()
    return grouped


def build_document(
    reports: list[Path],
    results_dir: Path,
    commit: str,
    generated_at: str,
    run_url: str,
) -> str:
    if not reports:
        raise ValueError(f"No BenchmarkDotNet reports found below {results_dir}")

    grouped = _group(reports, results_dir)
    run_count = max(len(runs) for by_report in grouped.values() for runs in by_report.values())

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
            "These results are refreshed weekly."
        ),
        ":::",
        "",
        "StackExchange.Redis is the baseline. A ratio below `1.00` means Respire completed the operation faster.",
        "",
    ]

    if run_count > 1:
        lines.extend(
            [
                (
                    f"Every benchmark ran {run_count} times, each time on a separate machine. "
                    "Both clients in a run share that machine, so each run gives its own ratio. "
                    "Read the median ratio: a single run on a shared machine can move by several percent."
                ),
                "",
            ]
        )

    latest = max(grouped, key=_framework_key)
    lines.extend(_summary_charts(latest, grouped[latest]))

    if run_count > 1:
        lines.extend(["## Ratio across runs", ""])
        first_report = sorted(grouped[latest])[0]
        for run, path in grouped[latest][first_report]:
            cpu = _runner_cpu(path.read_text(encoding="utf-8"))
            lines.append(f"- Run {run}: {cpu or 'unknown CPU'}")
        lines.append("")
        for report_name in sorted(grouped[latest]):
            lines.extend(_ratio_summary(report_name, grouped[latest][report_name]))

    for framework in sorted(grouped):
        lines.extend([f"## {framework}", ""])
        by_report = grouped[framework]
        for report_name in sorted(by_report):
            runs = by_report[report_name]
            if len(by_report) > 1:
                lines.extend([f"### {report_name}", ""])
            for run, path in runs:
                if len(runs) > 1:
                    lines.extend([f"#### Run {run}", ""])
                lines.extend([path.read_text(encoding="utf-8").strip(), ""])

    lines.extend(
        [
            "## Reading the results",
            "",
            "These runs use shared Linux machines and a local Redis container, so expect some variation between runs. Before making an important decision, measure with your own payload sizes, concurrency, Redis deployment, and network.",
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
