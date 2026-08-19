import tempfile
import unittest
from pathlib import Path

import benchmark_docs
import stress_docs


class BenchmarkDocsTests(unittest.TestCase):
    def test_groups_reports_by_framework(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = (
                root
                / "comparison-benchmarks-net10.0"
                / "results"
                / "Respire.Benchmarks.Sample-report-github.md"
            )
            report.parent.mkdir(parents=True)
            report.write_text("| Method | Mean |\n|---|---:|\n| Get | 1 ns |", encoding="utf-8")

            document = benchmark_docs.build_document(
                [report], root, "0123456789abcdef", "2026-08-08 12:00 UTC", "https://example.test/run"
            )

        self.assertIn("## net10.0", document)
        self.assertIn("| Get | 1 ns |", document)
        self.assertIn("`0123456789ab`", document)

    def test_adds_visual_comparison_from_latest_framework(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = (
                root
                / "comparison-benchmarks-net10.0"
                / "results"
                / "Respire.Benchmarks.CommonOperationsBenchmarks-report-github.md"
            )
            report.parent.mkdir(parents=True)
            report.write_text(
                "| Method | Categories | Mean | Ratio |\n"
                "|---|---|---:|---:|\n"
                "| StackExchange_Get | GET | 1.00 μs | 1.00 |\n"
                "| Respire_Get | GET | 750 ns | 0.75 |",
                encoding="utf-8",
            )

            document = benchmark_docs.build_document(
                [report], root, "abc", "now", "https://example.test/run"
            )

        self.assertIn("import ComparisonBarChart", document)
        self.assertIn("Selected operation time — net10.0", document)
        self.assertIn('format="duration-ns"', document)
        self.assertIn("showRatio", document)
        self.assertIn('"other":1000.0', document)
        self.assertIn('"respire":750.0', document)

    def test_cache_chart_includes_respire_server_read(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = (
                root
                / "comparison-benchmarks-net10.0"
                / "results"
                / "Respire.Benchmarks.ClientSideCachingBenchmarks-report-github.md"
            )
            report.parent.mkdir(parents=True)
            report.write_text(
                "| Method | Categories | Mean | Ratio |\n"
                "|---|---|---:|---:|\n"
                "| StackExchange_Get_ServerRead | GET hot | 150 μs | 1.00 |\n"
                "| Respire_Get_ServerRead | GET hot | 145 μs | 0.97 |\n"
                "| Respire_Get_ClientCacheHit | GET hot | 200 ns | 0.001 |",
                encoding="utf-8",
            )

            document = benchmark_docs.build_document(
                [report], root, "abc", "now", "https://example.test/run"
            )

        self.assertIn("has no built-in server-assisted client cache", document)
        self.assertIn('respireLabel="Respire cache hit"', document)
        self.assertIn('"respireServer":145000.0', document)

    def test_rejects_missing_reports(self) -> None:
        with self.assertRaisesRegex(ValueError, "No BenchmarkDotNet reports"):
            benchmark_docs.build_document([], Path("results"), "abc", "now", "run")


class StressDocsTests(unittest.TestCase):
    def test_replaces_report_title_with_docs_frontmatter(self) -> None:
        document = stress_docs.build_document(
            "# Raw stress title\n\n## Throughput\n\n| Scenario | Ops/s |",
            "fedcba9876543210",
            "2026-08-08 12:00 UTC",
            "https://example.test/run",
        )

        self.assertIn("title: Stress tests", document)
        self.assertIn("# Stress tests", document)
        self.assertNotIn("# Raw stress title", document)
        self.assertIn("## Throughput", document)

    def test_adds_throughput_chart(self) -> None:
        document = stress_docs.build_document(
            "# Raw stress title\n\n"
            "## Throughput\n\n"
            "| Scenario | StackExchange.Redis ops/s | Respire ops/s | Respire / StackExchange |\n"
            "|---|---:|---:|---:|\n"
            "| get | 100,000 | 150,000 | 1.50x |",
            "abc",
            "now",
            "https://example.test/run",
        )

        self.assertIn("import ComparisonBarChart", document)
        self.assertIn('"other":100000', document)
        self.assertIn('"respire":150000', document)

    def test_rejects_empty_report(self) -> None:
        with self.assertRaisesRegex(ValueError, "Stress report is empty"):
            stress_docs.build_document("", "abc", "now", "run")


if __name__ == "__main__":
    unittest.main()
