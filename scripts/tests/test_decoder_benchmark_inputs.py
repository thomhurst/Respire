import importlib.util
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location(
    "decoder_inputs", Path(__file__).parents[1] / "decoder_benchmark_inputs.py")
inputs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inputs)


class DecoderInputTests(unittest.TestCase):
    def test_decoder_and_maintenance_changes_are_isolated(self):
        self.assertEqual([inputs.DECODER], inputs.validate_changes(
            [inputs.DECODER, inputs.FIXTURE, *inputs.MAINTENANCE]))

    def test_maintenance_only_has_no_production_improvement_requirement(self):
        for path in [inputs.FIXTURE, *inputs.MAINTENANCE]:
            with self.subTest(path=path):
                self.assertEqual([], inputs.validate_changes([path]))
        self.assertEqual([], inputs.validate_changes([]))

    def test_other_build_inputs_fail_closed(self):
        for path in (
            "Directory.Packages.props", "global.json", "NuGet.Config",
            "Directory.Build.props", "Directory.Build.targets",
            "benchmarks/Respire.Benchmarks/Respire.Benchmarks.csproj",
            "benchmarks/Respire.Benchmarks/Program.cs",
            "benchmarks/Respire.Benchmarks/OtherBenchmark.cs",
            "src/Respire/Respire.csproj", "src/Shared/SkipLocalsInit.cs",
            "src/Respire.SourceGeneration/RespireCommandGenerator.cs",
            "tools/new-build-input.targets",
        ):
            with self.subTest(path=path):
                with self.assertRaisesRegex(ValueError, "Benchmark inputs must match"):
                    inputs.validate_changes([inputs.DECODER, path])


if __name__ == "__main__":
    unittest.main()
