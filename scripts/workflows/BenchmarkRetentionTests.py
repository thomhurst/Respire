import json
import os
import subprocess
import sys
import textwrap
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory


PHASES = ('baseline-validation', 'candidate-validation', 'baseline-a', 'candidate', 'baseline-b')
STAGES = ('before-connect', 'connected-idle', 'after-first-reuse', 'after-dispose')


class BenchmarkRetentionTests(unittest.TestCase):
    def run_summary(self, missing=None, invalid=None, stages=STAGES, stress=False,
                    missing_stress=None, invalid_stress=None, dimensions=(),
                    missing_dimension=None, scope_drift=None, collapse_scopes=False):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-compare.yml'
        section = workflow.read_text().split('      - name: Require complete results and summarize both controls\n')[1]
        script = section.split('        run: |\n')[1].split('      - uses:')[0]
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'summary.py').write_text(textwrap.dedent(script))
            methods = ('ColdStringMiss', 'ColdMissAndFirstReuse', 'WarmStringHit')
            for phase in PHASES:
                candidate = phase.startswith('candidate')
                cases = []
                snapshots = []
                for coalesce in (False, True):
                    for method in methods:
                        cases.append({
                            'FullName': 'Publication.' + method,
                            'Parameters': f'Coalesce={coalesce}', 'Method': method,
                            'Statistics': {'Mean': 100, 'ConfidenceInterval': {'Lower': 99, 'Upper': 101}},
                            'Memory': {'BytesAllocatedPerOperation': 0 if method == 'WarmStringHit' else 10},
                        })
                        for index, stage in enumerate(STAGES):
                            if missing == (phase, stage):
                                continue
                            snapshot = {'stage': stage, 'managedBytes': 100 + index,
                                        'pohBytes': 10, 'pohFragmentedBytes': 0, 'workingSetBytes': 500}
                            if dimensions:
                                snapshot.update(owner=method, policy=str(coalesce))
                                snapshot['managedBytes'] += 1000 if coalesce else 0
                                if missing_dimension == (phase, stage):
                                    del snapshot['owner']
                                if scope_drift == (phase, stage) and coalesce:
                                    snapshot['policy'] = 'unexpected'
                                if collapse_scopes:
                                    snapshot['policy'] = 'same'
                            if invalid == (phase, stage):
                                snapshot['managedBytes'] = -1
                            snapshots.append('RECEIVE_MEMORY ' + json.dumps(snapshot))
                        if stress and phase != missing_stress:
                            counters = {'operations': 64, 'payloadBytes': 5 * 1024 * 1024,
                                        'gen2Collections': 0, 'allocatedBytes': 64}
                            if invalid_stress and phase == invalid_stress[0]:
                                counters[invalid_stress[1]] = invalid_stress[2]
                            snapshots.append('LARGE_SET_STRESS ' + json.dumps(counters))
                report = root / 'results' / phase / 'results'
                report.mkdir(parents=True)
                (report / 'Publication-report-full-compressed.json').write_text(json.dumps({'Benchmarks': cases}))
                (root / f'{phase}.log').write_text(('c' if candidate else 'b') * 40 + '\n'
                                                 + '\n'.join(snapshots) + '\n')
            environment = dict(os.environ, EXPECTED_METHODS=','.join(methods), EXPECTED_COUNT='6',
                               ZERO_ALLOCATION_METHODS='WarmStringHit', IMPROVEMENT_METHODS='',
                               HEAD_SHA='c' * 40, BASE_SHA='b' * 40, EXPECTED_HEAD_SHA='h' * 40,
                               EVENT_BASE_SHA='b' * 40, RETAINED_MEMORY='true',
                               RETAINED_MEMORY_STAGES=','.join(stages), GITHUB_STEP_SUMMARY=str(root / 'step.md'))
            environment['LARGE_SET_STRESS'] = str(stress).lower()
            environment['RETAINED_MEMORY_DIMENSIONS'] = ','.join(dimensions)
            result = subprocess.run([sys.executable, 'summary.py'], cwd=root, env=environment,
                                    capture_output=True, text=True)
            summary = root / 'comparison.md'
            return result, summary.read_text() if summary.exists() else ''

    def test_first_reuse_is_reported_for_every_phase(self):
        result, summary = self.run_summary()
        self.assertEqual(result.returncode, 0, result.stderr)
        for phase in PHASES:
            self.assertIn(f'| {phase} | after-first-reuse | 6 | 102 |', summary)

    def test_missing_first_reuse_in_any_phase_is_rejected(self):
        for phase in PHASES:
            with self.subTest(phase=phase):
                result, _ = self.run_summary(missing=(phase, 'after-first-reuse'))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(f'Missing retention snapshots: {phase}/after-first-reuse', result.stderr)

    def test_invalid_first_reuse_measurement_is_rejected(self):
        result, _ = self.run_summary(invalid=('candidate', 'after-first-reuse'))
        self.assertNotEqual(result.returncode, 0)

    def test_three_stage_fixtures_keep_their_existing_contract(self):
        stages = ('before-connect', 'connected-idle', 'after-dispose')
        result, summary = self.run_summary(missing=('candidate', 'after-first-reuse'), stages=stages)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn('| after-first-reuse |', summary)

    def test_bounded_set_stress_is_reported_for_every_phase(self):
        result, summary = self.run_summary(stress=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        for phase in PHASES:
            self.assertIn(f'| {phase} | 6 | 0 / 0 / 0 | 64 / 64 / 64 |', summary)

    def test_missing_set_stress_in_any_phase_is_rejected(self):
        for phase in PHASES:
            with self.subTest(phase=phase):
                result, _ = self.run_summary(stress=True, missing_stress=phase)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn(f'Missing SET stress counters: {phase}', result.stderr)

    def test_invalid_set_stress_counters_are_rejected(self):
        for field, value in (('operations', 63), ('payloadBytes', 1024),
                             ('gen2Collections', -1), ('allocatedBytes', True)):
            with self.subTest(field=field):
                result, _ = self.run_summary(stress=True, invalid_stress=('candidate', field, value))
                self.assertNotEqual(result.returncode, 0)

    def test_retention_dimensions_preserve_distinct_process_sizes(self):
        result, summary = self.run_summary(dimensions=('owner', 'policy'))
        self.assertEqual(result.returncode, 0, result.stderr)
        for phase in PHASES:
            self.assertIn(f'| {phase} | connected-idle | owner=ColdStringMiss, policy=False | 1 | 101 |', summary)
            self.assertIn(f'| {phase} | connected-idle | owner=ColdStringMiss, policy=True | 1 | 1101 |', summary)

    def test_missing_retention_dimension_is_rejected(self):
        result, _ = self.run_summary(dimensions=('owner', 'policy'),
                                     missing_dimension=('candidate', 'after-first-reuse'))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing retention dimension', result.stderr)

    def test_retention_scope_drift_is_rejected(self):
        result, _ = self.run_summary(dimensions=('owner', 'policy'),
                                     scope_drift=('candidate', 'after-first-reuse'))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Retention parameters changed: candidate/after-first-reuse', result.stderr)

    def test_collapsed_retention_scopes_are_rejected(self):
        result, _ = self.run_summary(dimensions=('owner', 'policy'), collapse_scopes=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Incomplete retention parameters', result.stderr)


if __name__ == '__main__':
    unittest.main()
