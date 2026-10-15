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
