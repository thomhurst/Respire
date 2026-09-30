import itertools
import json
from pathlib import Path
import tempfile
import unittest

import value_codec_report


class ValueCodecReportTests(unittest.TestCase):
    def fixture(self, root):
        cases, sizes = [], []
        for length, pattern, threshold, codec in itertools.product(
            (64, 16384), ("RepeatedText", "RandomBytes"), (0, 1024), ("Raw", "Brotli", "Deflate", "Lz4", "Zstd")
        ):
            cases.append({"Method": codec + "Encode", "FullName": f"Respire.Benchmarks.ValueCodecBenchmarks.{codec}Encode(Length: {length}, Pattern: {pattern}, MinimumLength: {threshold})",
                          "Parameters": f"Length={length}&Pattern={pattern}&MinimumLength={threshold}",
                          "Statistics": {"Mean": 100, "StandardDeviation": 2},
                          "Memory": {"BytesAllocatedPerOperation": 0}})
            sizes.append({"Length": length, "Pattern": pattern, "MinimumLength": threshold, "Codec": codec,
                          "EncodedBytes": length, "RespBulkStringBytes": length + len(str(length)) + 5,
                          "Algorithm": None if codec == "Raw" else 0, "PayloadSha256": f"{length}-{pattern}"})
        report = root / "validation/results/ValueCodecBenchmarks-report-full-compressed.json"
        report.parent.mkdir(parents=True)
        report.write_text(json.dumps({"Benchmarks": cases}), encoding="utf-8")
        (root / "validation.log").write_text("\n".join("VALUE_CODEC_SIZE " + json.dumps(size) for size in sizes), encoding="utf-8")
        return report, cases, sizes

    def test_complete_matrix_joins_size_and_derives_exact_smoke_filter(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            value_codec_report.generate(root, "validation", "Encode")
            result = json.loads((root / "validation-summary.json").read_text(encoding="utf-8"))
            self.assertEqual(40, len(result))
            self.assertEqual(0, result[0]["AllocatedBytes"])
            self.assertEqual("Respire.Benchmarks.ValueCodecBenchmarks.BrotliEncode(Length: 16384, Pattern: RepeatedText, MinimumLength: 1024)",
                             (root / "representative-filter.txt").read_text(encoding="utf-8").strip())

    def test_missing_failed_or_duplicate_measurements_fail_the_gate(self):
        for problem in ("missing", "failed", "duplicate", "allocation"):
            with self.subTest(problem=problem), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                report, cases, _ = self.fixture(root)
                if problem == "missing":
                    cases.pop()
                elif problem == "failed":
                    cases[0]["Statistics"] = None
                elif problem == "duplicate":
                    cases.append(cases[0])
                else:
                    cases[0]["Memory"]["BytesAllocatedPerOperation"] = None
                report.write_text(json.dumps({"Benchmarks": cases}), encoding="utf-8")
                with self.assertRaises(ValueError):
                    value_codec_report.generate(root, "validation", "Encode")

    def test_missing_size_metadata_names_the_case(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            self.fixture(root)
            log = root / "validation.log"
            lines = log.read_text(encoding="utf-8").splitlines()
            log.write_text("\n".join(lines[1:]), encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Missing size metadata.*Raw"):
                value_codec_report.generate(root, "validation", "Encode")

    def test_changed_payload_between_launches_cannot_report_success(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            _, _, sizes = self.fixture(root)
            sizes[0]["PayloadSha256"] = "different"
            with (root / "validation.log").open("a", encoding="utf-8") as output:
                output.write("\nVALUE_CODEC_SIZE " + json.dumps(sizes[0]))
            with self.assertRaisesRegex(ValueError, "changed across launches"):
                value_codec_report.generate(root, "validation", "Encode")


if __name__ == "__main__":
    unittest.main()
