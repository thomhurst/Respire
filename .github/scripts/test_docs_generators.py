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

    def test_rejects_empty_report(self) -> None:
        with self.assertRaisesRegex(ValueError, "Stress report is empty"):
            stress_docs.build_document("", "abc", "now", "run")


if __name__ == "__main__":
    unittest.main()
