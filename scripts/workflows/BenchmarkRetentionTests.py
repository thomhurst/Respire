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
    def run_summary(self, missing=None, invalid=None, stages=STAGES):
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
                            if invalid == (phase, stage):
                                snapshot['managedBytes'] = -1
                            snapshots.append('RECEIVE_MEMORY ' + json.dumps(snapshot))
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


if __name__ == '__main__':
    unittest.main()
