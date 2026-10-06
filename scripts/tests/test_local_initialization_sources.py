"""Exercise comparison provenance with real Git checkouts, without measurements."""

import hashlib
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "derive_sources", Path(__file__).resolve().parents[1] / "Derive-LocalInitializationSources.py")
derive_sources = importlib.util.module_from_spec(spec)
spec.loader.exec_module(derive_sources)


class LocalInitializationSourceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        original = self.root / "original"
        original.mkdir()
        self.git(original, "init", "--quiet")
        self.git(original, "config", "user.name", "Comparison control")
        self.git(original, "config", "user.email", "comparison@example.invalid")
        self.git(original, "config", "core.autocrlf", "false")
        for path, text in {
            derive_sources.MODULE: "using System.Runtime.CompilerServices;\n\n[module: SkipLocalsInit]\n",
            derive_sources.DECODER: "// Unchanged decoder\n",
            derive_sources.FIXTURE: "[SimpleJob(warmupCount: 3, iterationCount: 10)]\nclass Fixture {}\n",
        }.items():
            destination = original / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_text(text)
        self.git(original, "add", ".")
        self.git(original, "commit", "--quiet", "-m", "Initial control")
        self.head = self.git(original, "rev-parse", "HEAD").decode().strip()
        for source in ("baseline", "candidate"):
            self.git(self.root, "clone", "--quiet", "--no-hardlinks", "-c", "core.autocrlf=false",
                     str(original), source)

    @staticmethod
    def git(directory, *arguments):
        return subprocess.check_output(["git", "-C", str(directory), *arguments], stderr=subprocess.STDOUT)

    def test_pinned_controls_keep_decoder_fixture_and_provenance(self):
        derive_sources.derive(self.root, self.head)
        manifest = json.loads((self.root / "source-manifest.json").read_text())
        self.assertEqual(manifest["head_sha"], self.head)
        self.assertEqual(manifest["only_source_difference"], derive_sources.MODULE)
        for path in (derive_sources.DECODER, derive_sources.FIXTURE):
            self.assertEqual((self.root / "baseline" / path).read_bytes(),
                             (self.root / "candidate" / path).read_bytes())
        for source, details in manifest["sources"].items():
            self.assertEqual(hashlib.sha256((self.root / f"{source}.patch").read_bytes()).hexdigest(),
                             details["patch_sha256"])
            for path, expected in details["files"].items():
                self.assertEqual(hashlib.sha256((self.root / source / path).read_bytes()).hexdigest(), expected)
        self.assertNotIn("SimpleJob", (self.root / "candidate" / derive_sources.FIXTURE).read_text())
        self.assertIn("SkipLocalsInit]", (self.root / "candidate" / derive_sources.MODULE).read_text())
        self.assertNotIn("SkipLocalsInit", (self.root / "baseline" / derive_sources.MODULE).read_text())

    def test_wrong_or_abbreviated_head_is_rejected(self):
        for head in ("0" * 40, self.head[:12]):
            with self.subTest(head=head), self.assertRaises(AssertionError):
                derive_sources.derive(self.root, head)

    def test_dirty_decoder_is_rejected_before_derivation(self):
        (self.root / "candidate" / derive_sources.DECODER).write_text("// Changed decoder\n")
        with self.assertRaises(AssertionError):
            derive_sources.derive(self.root, self.head)
        self.assertEqual((self.root / "baseline" / derive_sources.MODULE).read_bytes(),
                         (self.root / "candidate" / derive_sources.MODULE).read_bytes())

    def test_different_checkout_revision_is_rejected(self):
        candidate = self.root / "candidate"
        self.git(candidate, "-c", "user.name=Control", "-c", "user.email=control@example.invalid",
                 "commit", "--quiet", "--allow-empty", "-m", "Different revision")
        with self.assertRaises(AssertionError):
            derive_sources.derive(self.root, self.head)


if __name__ == "__main__":
    unittest.main()
