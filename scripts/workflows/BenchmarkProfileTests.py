import json
import os
import subprocess
import sys
import textwrap
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory


class BenchmarkProfileTests(unittest.TestCase):
    def run_profiles(self, change=None):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-compare.yml'
        section = workflow.read_text().split('      - name: Require separate profile traces\n')[1]
        script = section.split('        run: |\n')[1].split('      - name:')[0]
        with TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'profiles.py').write_text(textwrap.dedent(script))
            for phase in ('baseline-a', 'candidate', 'baseline-b'):
                artifacts = root / 'results' / phase
                reports = artifacts / 'results'
                reports.mkdir(parents=True)
                cases = [{'FullName': 'Fixture.' + method} for method in ('Write', 'WriteSameKey', 'FailedWrite')]
                (reports / 'Fixture-report-full-compressed.json').write_text(json.dumps({'Benchmarks': cases}))
                for case in cases:
                    name = case['FullName'] + '-20261008-103000'
                    (artifacts / (name + '.nettrace')).write_bytes(b'trace')
                    (artifacts / (name + '.speedscope.json')).write_text('{}')
            if change is not None:
                change(root / 'results' / 'candidate')
            environment = dict(os.environ, EXPECTED_COUNT='3')
            return subprocess.run([sys.executable, 'profiles.py'], cwd=root, env=environment,
                                  capture_output=True, text=True)

    def test_every_case_requires_its_matching_pair(self):
        result = self.run_profiles()
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_duplicate_other_case_cannot_hide_a_missing_case(self):
        def change(artifacts):
            for extension in ('.nettrace', '.speedscope.json'):
                (artifacts / ('Fixture.Write-20261008-103000' + extension)).unlink()
                (artifacts / ('Fixture.WriteSameKey-20261008-103001' + extension)).write_text('duplicate')
        result = self.run_profiles(change)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing or duplicate case trace: candidate/', result.stderr)

    def test_an_unmatched_profile_is_rejected(self):
        def change(artifacts):
            (artifacts / 'Fixture.Write-20261008-103000.speedscope.json').rename(artifacts / 'unrelated.speedscope.json')
        result = self.run_profiles(change)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing matching profile: candidate/', result.stderr)

    def test_empty_artifacts_are_rejected(self):
        for extension in ('.nettrace', '.speedscope.json'):
            with self.subTest(extension=extension):
                result = self.run_profiles(lambda artifacts: (artifacts / ('Fixture.Write-20261008-103000' + extension)).write_bytes(b''))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Empty profile artifact: candidate/', result.stderr)


if __name__ == '__main__':
    unittest.main()
