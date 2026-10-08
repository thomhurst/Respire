import copy
import unittest

import MapperParity as gate


def cases(phase):
    return [{
        'Type': ('Generated' if phase.startswith('generated') else 'Handwritten') + 'MapperBenchmarks',
        'Method': method, 'Parameters': parameter,
        'Statistics': {'Mean': 100, 'Median': 100, 'StandardDeviation': 1, 'N': 15,
                       'ConfidenceInterval': {'Lower': 99, 'Upper': 101}},
        'Memory': {'BytesAllocatedPerOperation': 32},
    } for method in sorted(gate.METHODS) for parameter in sorted(gate.PARAMETERS)]


class MapperParityTests(unittest.TestCase):
    def setUp(self):
        self.phases = {phase: cases(phase) for phase in gate.PHASES}
        self.manifest = {'head_sha': 'a' * 40, 'event_base_sha': 'b' * 40}

    def evaluate(self):
        return gate.summarize(self.phases, self.manifest)

    def test_all_rows_must_pass(self):
        report, accepted = self.evaluate()
        self.assertTrue(accepted)
        self.assertEqual(report.count('| PASS |'), 24)

    def test_incomplete_duplicate_and_wrong_control_fail_closed(self):
        for mutation in ('missing', 'duplicate', 'type', 'parameters', 'method'):
            with self.subTest(mutation=mutation):
                evidence = cases('handwritten-a')
                if mutation == 'missing': evidence.pop()
                elif mutation == 'duplicate': evidence.append(copy.deepcopy(evidence[0]))
                elif mutation == 'type': evidence[0]['Type'] = 'GeneratedMapperBenchmarks'
                elif mutation == 'parameters': evidence[0]['Parameters'] = 'Populated=unknown'
                else: evidence[0]['Method'] = 'Unknown'
                with self.assertRaises(ValueError): gate.index_cases(evidence, 'handwritten-a')

    def test_invalid_numeric_evidence_and_sample_count_fail_closed(self):
        for field, value in (('Mean', float('nan')), ('Median', float('inf')), ('StandardDeviation', -1), ('N', 1), ('N', 15.5)):
            with self.subTest(field=field, value=value):
                evidence = cases('generated')
                evidence[0]['Statistics'][field] = value
                with self.assertRaises(ValueError): gate.index_cases(evidence, 'generated')
        evidence = cases('generated')
        evidence[0]['Memory']['BytesAllocatedPerOperation'] = -1
        with self.assertRaises(ValueError): gate.index_cases(evidence, 'generated')
        evidence = cases('generated')
        evidence[0]['Statistics']['ConfidenceInterval']['Lower'] = 101
        with self.assertRaises(ValueError): gate.index_cases(evidence, 'generated')

    def test_dry_cannot_substitute_for_measurement(self):
        evidence = cases('generated-validation')
        for case in evidence:
            case['Statistics']['N'] = 1
            case['Statistics']['ConfidenceInterval'] = {'Lower': '', 'Upper': ''}
        self.assertEqual(len(gate.index_cases(evidence, 'generated-validation')), 24)
        with self.assertRaises(ValueError): gate.index_cases(evidence, 'generated')

    def test_extra_allocation_against_either_control_is_failure(self):
        self.phases['handwritten-b'][0]['Memory']['BytesAllocatedPerOperation'] = 16
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('FAIL: extra allocation', report)
        self.assertEqual(report.count('| PASS |'), 23)

    def test_clear_latency_regression_is_failure(self):
        self.phases['generated'][0]['Statistics'].update(Mean=120, Median=120, ConfidenceInterval={'Lower': 119, 'Upper': 121})
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('FAIL: latency exceeds 10% margin', report)

    def test_noisy_control_or_generated_evidence_is_inconclusive(self):
        for phase in ('handwritten-a', 'generated', 'handwritten-b'):
            with self.subTest(phase=phase):
                phases = copy.deepcopy(self.phases)
                phases[phase][0]['Statistics']['StandardDeviation'] = 11
                report, accepted = gate.summarize(phases, self.manifest)
                self.assertFalse(accepted)
                self.assertIn('INCONCLUSIVE: dispersion', report)

    def test_control_drift_is_inconclusive(self):
        self.phases['handwritten-b'][0]['Statistics'].update(Mean=106, Median=106, ConfidenceInterval={'Lower': 105, 'Upper': 107})
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('INCONCLUSIVE: control drift', report)

    def test_overlapping_wide_intervals_do_not_prove_parity(self):
        self.phases['generated'][0]['Statistics']['ConfidenceInterval']['Upper'] = 110
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('INCONCLUSIVE: confidence bounds', report)


if __name__ == '__main__':
    unittest.main()
