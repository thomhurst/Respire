import importlib.util
import tempfile
import unittest
from pathlib import Path


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).parents[1] / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


sources = load("local_initialization_sources", "local_initialization_sources.py")
gate = load("local_initialization_gate", "assert_local_initialization_benchmarks.py")


def write(root, relative, text):
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="")
    return path


class SourceDerivationTests(unittest.TestCase):
    def test_tree_without_attribute_gets_core_probe_only(self):
        with tempfile.TemporaryDirectory() as temp:
            baseline, candidate = Path(temp, "b"), Path(temp, "c")
            for root in (baseline, candidate):
                write(root, "src/Respire/A.cs", "// SkipLocalsInit is discussed here only.\nclass A { }\n")
            mode, changed = sources.derive(baseline, candidate)
            self.assertEqual("probe", mode)
            self.assertEqual(["src/Respire/LocalInitializationProbe.cs"], changed)
            self.assertIn("[module: System.Runtime.CompilerServices.SkipLocalsInit]", (candidate / changed[0]).read_text())
            self.assertFalse((baseline / changed[0]).exists())

    def test_shipped_attributes_fail_without_mutating_either_tree(self):
        with tempfile.TemporaryDirectory() as temp:
            baseline, candidate = Path(temp, "b"), Path(temp, "c")
            text = "class A\n{\n    [SkipLocalsInit]\n    void M() { }\n    [System.Runtime.CompilerServices.SkipLocalsInitAttribute()]\n    void N() { }\n}\n"
            for root in (baseline, candidate):
                write(root, "src/Respire/A.cs", text)
                write(root, "src/Respire.Json/B.cs", "[module: SkipLocalsInit]\n")
            with self.assertRaisesRegex(ValueError, "Uncovered shipped"):
                sources.derive(baseline, candidate)
            for root in (baseline, candidate):
                self.assertEqual(text.encode(), (root / "src/Respire/A.cs").read_bytes())

    def test_core_module_scope_also_requires_coverage(self):
        with tempfile.TemporaryDirectory() as temp:
            baseline, candidate = Path(temp, "b"), Path(temp, "c")
            text = b"[module: SkipLocalsInit]\r\nclass A { }\r\n"
            for root in (baseline, candidate):
                write(root, "src/Respire/A.cs", text.decode())
            with self.assertRaisesRegex(ValueError, "Uncovered shipped"):
                sources.derive(baseline, candidate)
            for root in (baseline, candidate):
                self.assertEqual(text, (root / "src/Respire/A.cs").read_bytes())

    def test_crlf_strip_preserves_remaining_bytes(self):
        text = "class A\r\n{\r\n    [SkipLocalsInit]\r\n    void M() { }\r\n}\r\n"
        stripped, count = sources.strip_attributes(text)
        self.assertEqual(1, count)
        self.assertEqual(text.replace("    [SkipLocalsInit]\r\n", ""), stripped)

    def test_attribute_sharing_a_line_fails_closed(self):
        with self.assertRaises(ValueError):
            sources.strip_attributes("[SkipLocalsInit, MethodImpl(MethodImplOptions.NoInlining)]\nvoid M() { }\n")
        with self.assertRaises(ValueError):
            sources.strip_attributes("[SkipLocalsInit] void M() { }\n")

    def test_build_output_is_ignored(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            write(root, "src/Respire/obj/Generated.cs", "[module: SkipLocalsInit]\n")
            write(root, "src/Respire/A.cs", "class A { }\n")
            self.assertEqual([root / "src/Respire/A.cs"], sources.source_files(root))


def cases(mean=10.0):
    return [{"Method": method, "Statistics": {"Mean": mean, "N": 15, "ConfidenceInterval": {"Lower": mean - 1, "Upper": mean + 1}},
             "Memory": {"BytesAllocatedPerOperation": 48}} for method in sorted(gate.METHODS)]


class GateTests(unittest.TestCase):
    def test_same_evidence_passes(self):
        evidence = cases()
        self.assertEqual([], gate.assess(evidence, evidence, evidence))

    def test_regression_above_both_controls_fails(self):
        control, candidate = cases(), cases()
        candidate[3]["Statistics"] = {"Mean": 14, "N": 15, "ConfidenceInterval": {"Lower": 13, "Upper": 15}}
        self.assertEqual(1, len(gate.assess(control, candidate, control)))

    def test_ratio_uses_mean_of_both_controls(self):
        ratios = gate.ratios(cases(10), cases(15), cases(20))
        self.assertTrue(all(abs(value - 1.0) < 1e-9 for value in ratios.values()))


if __name__ == "__main__":
    unittest.main()
