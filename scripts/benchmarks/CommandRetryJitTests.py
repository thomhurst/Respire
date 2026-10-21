import unittest
import json
import tempfile
from pathlib import Path
from unittest.mock import patch

from CommandRetryJit import BASELINE, CANDIDATE, HEAD, FAMILIES, DRIVER, FIXTURE, OPERATIONS, run, validate_dump, verify_pins


def dump(tier="Tier1", families=FAMILIES):
    return "\n".join(f"; Assembly listing for method Respire.RespireClient+<{family}>d__1:MoveNext():this ({tier})\n"
                     "; code\n; Total bytes of code 123\n" for family in families)


class CommandRetryJitTests(unittest.TestCase):
    def test_collection_records_inputs_and_reports_failed_dump_context(self):
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary)
            for name in ("baseline", "candidate", "infrastructure"):
                source = workspace / name
                (source / FIXTURE).parent.mkdir(parents=True)
                (source / FIXTURE).write_text("fixture")
                (source / DRIVER).parent.mkdir(parents=True)
                if name == "infrastructure":
                    (source / DRIVER).mkdir()
                for props in ("Directory.Build.props", "Directory.Packages.props"):
                    (source / props).write_text("<Project />")

            def check_output(command, **kwargs):
                if command == ["dotnet", "--info"]:
                    return "test SDK info"
                if "--format=%P" in command:
                    return f"{BASELINE} {HEAD}"
                return BASELINE if command[2].endswith("baseline") else CANDIDATE

            def execute(command, **kwargs):
                if command[1].endswith(".dll"):
                    self.assertEqual(command[-2:], ["Default", str(OPERATIONS)])
                    self.assertEqual(kwargs["env"]["DOTNET_TC_CallCountingDelayMs"], "0")
                    self.assertEqual(kwargs["env"]["DOTNET_TieredCompilation"], "1")
                    kwargs["stdout"].write(f"JIT_EXERCISE_COMPLETE policy=Default operations={OPERATIONS}")
                    Path(kwargs["env"]["DOTNET_JitStdOutFile"]).write_text(dump("Tier0"))

            with patch("CommandRetryJit.subprocess.check_output", side_effect=check_output), \
                    patch("CommandRetryJit.subprocess.run", side_effect=execute):
                with self.assertRaisesRegex(ValueError, r"tiered/baseline-a/Default: .*jit.asm: Missing Tier1"):
                    run(workspace)
            provenance = json.loads((workspace / "jit-results/provenance.json").read_text())
            self.assertEqual(provenance["dotnetInfo"]["baseline"], "test SDK info")
            self.assertEqual(len(provenance["buildInputs"]["baseline"]["Directory.Build.props"]), 64)
            failure = json.loads((workspace / "jit-results/tiered/baseline-a/Default/failure.json").read_text())
            self.assertIn("Missing Tier1", failure["error"])

    def test_exact_original_pair_is_required(self):
        verify_pins(BASELINE, CANDIDATE, [BASELINE, HEAD])
        for baseline, candidate, parents in (
            (HEAD, CANDIDATE, [BASELINE, HEAD]),
            (BASELINE, HEAD, [BASELINE, HEAD]),
            (BASELINE, CANDIDATE, [HEAD, BASELINE]),
            (BASELINE, CANDIDATE, [BASELINE]),
        ):
            with self.subTest(baseline=baseline, candidate=candidate, parents=parents):
                with self.assertRaises(ValueError):
                    verify_pins(baseline, candidate, parents)

    def test_optimized_script_state_machines_are_required(self):
        self.assertEqual(len(validate_dump(dump(), "tiered")), 2)
        self.assertEqual(len(validate_dump(dump("FullOpts"), "fullopts")), 2)
        for text in ("", dump("Tier0"), dump(families=FAMILIES[:1]),
                     dump().replace(":MoveNext(", ":Unrelated("),
                     dump().removesuffix("; Total bytes of code 123\n")):
            with self.subTest(text=text):
                with self.assertRaises(ValueError):
                    validate_dump(text, "tiered")

    def test_tiered_and_fullopts_are_distinct(self):
        for text, mode in ((dump(), "fullopts"), (dump("FullOpts"), "tiered")):
            with self.subTest(mode=mode):
                with self.assertRaises(ValueError):
                    validate_dump(text, mode)

    def test_interleaved_listings_and_unknown_modes_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "interleaved"):
            validate_dump(dump() + "; Total bytes of code 456\n", "tiered")
        with self.assertRaisesRegex(ValueError, "Unknown JIT mode"):
            validate_dump(dump(), "typo")


if __name__ == "__main__":
    unittest.main()
