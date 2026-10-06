import copy
import importlib.util
import unittest
from pathlib import Path


spec = importlib.util.spec_from_file_location("decoder_gate", Path(__file__).parents[1] / "assert_decoder_benchmarks.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


def cases():
    return [{"Method": method, "Statistics": {"Mean": 10, "N": 15, "ConfidenceInterval": {"Lower": 9, "Upper": 11}},
             "Memory": {"BytesAllocatedPerOperation": 48}} for method in sorted(gate.METHODS)]


class DecoderGateTests(unittest.TestCase):
    def test_identical_evidence_passes_but_does_not_prove_improvement(self):
        evidence = cases()
        self.assertEqual([], gate.assess(evidence, evidence, evidence))
        self.assertTrue(gate.assess(evidence, evidence, evidence, require_improvement=True))

    def test_candidate_above_both_controls_fails(self):
        control, candidate = cases(), cases()
        candidate[0]["Statistics"] = {"Mean": 13, "N": 15, "ConfidenceInterval": {"Lower": 12, "Upper": 14}}
        self.assertEqual(1, len(gate.assess(control, candidate, control)))

    def test_overlapping_one_control_does_not_fail_latency_gate(self):
        control, candidate, later = cases(), cases(), cases()
        candidate[0]["Statistics"] = {"Mean": 13, "N": 15, "ConfidenceInterval": {"Lower": 12, "Upper": 14}}
        later[0]["Statistics"] = {"Mean": 12, "N": 15, "ConfidenceInterval": {"Lower": 11, "Upper": 13}}
        self.assertEqual([], gate.assess(control, candidate, later))

    def test_allocation_regression_against_either_control_fails(self):
        control, candidate, later = cases(), cases(), cases()
        later[0]["Memory"]["BytesAllocatedPerOperation"] = 40
        self.assertEqual(1, len(gate.assess(control, candidate, later)))

    def test_small_ascii_improvement_requires_separation_from_both_controls(self):
        control, candidate = cases(), cases()
        ascii_case = next(case for case in candidate if case["Method"] == "FormatAsciiBulkString")
        ascii_case["Statistics"] = {"Mean": 7, "N": 15, "ConfidenceInterval": {"Lower": 6, "Upper": 8}}
        self.assertEqual([], gate.assess(control, candidate, control, require_improvement=True))

    def test_missing_duplicate_and_invalid_evidence_fails(self):
        for mutation in ("missing", "duplicate", "nan", "interval", "allocation", "statistics", "count"):
            with self.subTest(mutation=mutation):
                evidence = copy.deepcopy(cases())
                if mutation == "missing": evidence.pop()
                elif mutation == "duplicate": evidence.append(copy.deepcopy(evidence[0]))
                elif mutation == "nan": evidence[0]["Statistics"]["Mean"] = float("nan")
                elif mutation == "interval": evidence[0]["Statistics"]["ConfidenceInterval"]["Lower"] = 12
                elif mutation == "allocation": evidence[0]["Memory"]["BytesAllocatedPerOperation"] = -1
                elif mutation == "statistics": evidence[0].pop("Statistics")
                else: evidence[0]["Statistics"]["N"] = 1
                with self.assertRaises((ValueError, KeyError)):
                    gate.index_cases(evidence)


if __name__ == "__main__":
    unittest.main()
