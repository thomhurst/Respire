"""Regression checks for the published server-latency evidence validator."""

import importlib.util
import json
import tempfile
import unittest
import zipfile
from pathlib import Path


ASSETS = Path(__file__).resolve().parents[2] / "website/static/benchmarks/2026-10-server-latency"
SPEC = importlib.util.spec_from_file_location("latency_analysis", ASSETS / "analyze.py")
ANALYSIS = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ANALYSIS)


class TraceValidationTests(unittest.TestCase):
    def test_rejects_missing_and_corrupt_measurement_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("measurements.zip", "traces.zip"):
                with zipfile.ZipFile(ASSETS / name) as archive:
                    archive.extractall(root)
            baseline = ASSETS.parent / "2026-10-comparison/38484247008.zip"
            inputs = [
                *sorted((root / "results").glob("*-report.csv")),
                *sorted((root / "results").glob("*-report-full-compressed.json")),
                root / "server-latency/cases.json",
                root / "server-latency/observations.json",
                next(root.rglob("stacks.json")),
            ]
            for path in inputs:
                original = path.read_bytes()
                for corruption in ("missing", "truncated", "same-size"):
                    with self.subTest(path=path.relative_to(root), corruption=corruption):
                        try:
                            if corruption == "missing":
                                path.unlink()
                                message = "Missing evidence file"
                            elif corruption == "truncated":
                                path.write_bytes(original[:-1])
                                message = "incorrect size"
                            else:
                                # A numerical edit preserves valid CSV/JSON and file length.
                                changed = original.replace(b"1", b"2", 1)
                                self.assertNotEqual(original, changed)
                                path.write_bytes(changed)
                                message = "SHA-256 mismatch"
                            with self.assertRaisesRegex(ValueError, message):
                                ANALYSIS.analyze(root, baseline)
                        finally:
                            path.write_bytes(original)
            changed_baseline = root / "baseline.zip"
            changed_baseline.write_bytes(baseline.read_bytes())
            with zipfile.ZipFile(changed_baseline, "a") as archive:
                archive.comment = b"Changed but still a valid ZIP"
            with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                ANALYSIS.analyze(root, changed_baseline)
            changed_baseline.unlink()
            with self.assertRaisesRegex(ValueError, "Missing evidence file"):
                ANALYSIS.analyze(root, changed_baseline)

    def test_published_evidence_and_missing_or_corrupt_traces(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            with zipfile.ZipFile(ASSETS / "measurements.zip") as archive:
                archive.extractall(root)
            baseline = ASSETS.parent / "2026-10-comparison/38484247008.zip"
            with self.assertRaisesRegex(ValueError, "Missing raw trace"):
                ANALYSIS.analyze(root, baseline)
            with zipfile.ZipFile(ASSETS / "traces.zip") as archive:
                archive.extractall(root)
            self.assertEqual(
                json.loads((ASSETS / "analysis.json").read_text()),
                ANALYSIS.analyze(root, baseline),
            )
            trace = next(root.rglob("cpu.nettrace"))
            original = trace.read_bytes()
            with self.subTest("one missing trace"):
                trace.unlink()
                with self.assertRaisesRegex(ValueError, "Missing raw trace"):
                    ANALYSIS.analyze(root, baseline)
            with self.subTest("truncated trace"):
                trace.write_bytes(original[:-1])
                with self.assertRaisesRegex(ValueError, "incorrect size"):
                    ANALYSIS.analyze(root, baseline)
            with self.subTest("same-size corruption"):
                trace.write_bytes(bytes([original[0] ^ 1]) + original[1:])
                with self.assertRaisesRegex(ValueError, "SHA-256 mismatch"):
                    ANALYSIS.analyze(root, baseline)


if __name__ == "__main__":
    unittest.main()
