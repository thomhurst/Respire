import contextlib
import copy
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import MapperParity as gate


def cases(phase):
    return [{
        'Type': ('Generated' if phase.startswith('generated') else 'Handwritten') + 'MapperBenchmarks',
        'Method': method, 'Parameters': parameter,
        'Statistics': {'Mean': 100, 'Median': 100, 'StandardDeviation': 1, 'N': 15,
                       'ConfidenceInterval': {'Level': gate.CONFIDENCE_LEVEL_999, 'Lower': 99, 'Upper': 101}},
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
        for field, value in (('Mean', float('nan')), ('Mean', 0), ('Mean', -1), ('Median', float('inf')), ('Median', 0), ('StandardDeviation', -1), ('N', 1), ('N', 15.5)):
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

    def test_missing_or_wrong_confidence_level_fails_closed(self):
        for level in (None, 11, 0.999, 'L999', True):
            with self.subTest(level=level):
                evidence = cases('generated')
                if level is None:
                    del evidence[0]['Statistics']['ConfidenceInterval']['Level']
                else:
                    evidence[0]['Statistics']['ConfidenceInterval']['Level'] = level
                with self.assertRaises(ValueError):
                    gate.index_cases(evidence, 'generated')

    def test_extra_allocation_against_either_control_is_failure(self):
        self.phases['handwritten-b'][0]['Memory']['BytesAllocatedPerOperation'] = 16
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('FAIL: extra allocation', report)
        self.assertEqual(report.count('| PASS |'), 23)

    def test_clear_latency_regression_is_failure(self):
        self.phases['generated'][0]['Statistics'].update(Mean=120, Median=120, ConfidenceInterval={'Level': gate.CONFIDENCE_LEVEL_999, 'Lower': 119, 'Upper': 121})
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
        self.phases['handwritten-b'][0]['Statistics'].update(Mean=106, Median=106, ConfidenceInterval={'Level': gate.CONFIDENCE_LEVEL_999, 'Lower': 105, 'Upper': 107})
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('INCONCLUSIVE: control drift', report)

    def test_overlapping_wide_intervals_do_not_prove_parity(self):
        self.phases['generated'][0]['Statistics']['ConfidenceInterval']['Upper'] = 110
        report, accepted = self.evaluate()
        self.assertFalse(accepted)
        self.assertIn('INCONCLUSIVE: confidence bounds', report)

    def test_absolute_floor_applies_only_to_small_operations(self):
        a, generated, b = [cases(phase)[0] for phase in ('handwritten-a', 'generated', 'handwritten-b')]
        for case in (a, generated, b):
            case['Statistics'].update(Mean=0.2, Median=0.2, StandardDeviation=0.1,
                                      ConfidenceInterval={'Lower': -0.1, 'Upper': 0.5})
        b['Statistics'].update(Mean=0.4, Median=0.4)
        for method in gate.SMALL_METHODS:
            with self.subTest(method=method):
                self.assertEqual(gate.verdict(a, generated, b, method), 'PASS')
        for method in gate.METHODS - gate.SMALL_METHODS:
            with self.subTest(method=method):
                self.assertTrue(gate.verdict(a, generated, b, method).startswith('INCONCLUSIVE'))

    def test_absolute_floor_does_not_hide_regression_allocation_or_wide_intervals(self):
        a, generated, b = [cases(phase)[0] for phase in ('handwritten-a', 'generated', 'handwritten-b')]
        for case in (a, generated, b):
            case['Statistics'].update(Mean=0.2, Median=0.2, StandardDeviation=0.1,
                                      ConfidenceInterval={'Lower': 0.1, 'Upper': 0.3})
        generated['Statistics'].update(Mean=2, Median=2, ConfidenceInterval={'Lower': 1.9, 'Upper': 2.1})
        self.assertTrue(gate.verdict(a, generated, b, 'HashValidate').startswith('FAIL: latency'))
        generated['Statistics'].update(Mean=0.2, Median=0.2, ConfidenceInterval={'Lower': 0.1, 'Upper': 2.1})
        self.assertTrue(gate.verdict(a, generated, b, 'HashValidate').startswith('INCONCLUSIVE: confidence'))
        generated['Memory']['BytesAllocatedPerOperation'] = 33
        self.assertEqual(gate.verdict(a, generated, b, 'HashValidate'), 'FAIL: extra allocation')

    def test_absolute_drift_and_dispersion_still_block_above_floor(self):
        for mutation in ('drift', 'dispersion'):
            with self.subTest(mutation=mutation):
                a, generated, b = [cases(phase)[0] for phase in ('handwritten-a', 'generated', 'handwritten-b')]
                for case in (a, generated, b):
                    case['Statistics'].update(Mean=0.2, Median=0.2, StandardDeviation=0.1,
                                              ConfidenceInterval={'Lower': 0.1, 'Upper': 0.3})
                if mutation == 'drift':
                    b['Statistics'].update(Mean=1.3, Median=1.3, ConfidenceInterval={'Lower': 1.2, 'Upper': 1.4})
                else:
                    generated['Statistics']['StandardDeviation'] = 1.1
                self.assertTrue(gate.verdict(a, generated, b, 'HashValidate').startswith('INCONCLUSIVE'))

    def test_cli_uses_explicit_log_and_report_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            logs, results = root / 'logs', root / 'results'
            logs.mkdir()
            source = root / 'source.txt'
            source.write_text('immutable source')
            manifest = dict(self.manifest, phases=list(gate.PHASES), runner={'RUNNER_NAME': 'test'},
                            source_sha256={str(source): hashlib.sha256(source.read_bytes()).hexdigest()})
            manifest_path = root / 'manifest.json'
            manifest_path.write_text(json.dumps(manifest))
            report_path = root / 'explicit-report.md'
            for phase in gate.PHASES:
                (logs / f'{phase}.log').write_text(manifest['head_sha'] + '\n')
                phase_results = results / phase / 'results'
                phase_results.mkdir(parents=True)
                (phase_results / 'test-report-full-compressed.json').write_text(json.dumps({'Benchmarks': self.phases[phase]}))
            arguments = ['MapperParity.py', str(results), '--manifest', str(manifest_path),
                         '--logs', str(logs), '--report', str(report_path)]
            with patch('sys.argv', arguments), patch.object(gate.subprocess, 'check_output', return_value=manifest['head_sha']), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(gate.main(), 0)
            self.assertEqual(report_path.read_text().count('| PASS |'), 24)


if __name__ == '__main__':
    unittest.main()
