import unittest

from CommandRetryJit import BASELINE, CANDIDATE, HEAD, FAMILIES, validate_dump, verify_pins


def dump(tier="Tier1", families=FAMILIES):
    return "\n".join(f"; Assembly listing for method Respire.RespireClient+<{family}>d__1:MoveNext():this ({tier})\n"
                     "; code\n; Total bytes of code 123\n" for family in families)


class CommandRetryJitTests(unittest.TestCase):
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
