import json
import os
import subprocess
import sys
import textwrap
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import Mock, patch


class BenchmarkProfileTests(unittest.TestCase):
    def portable_names_step(self):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-compare.yml'
        section = workflow.read_text().split('      - name: Prepare portable profile artifact names\n')[1]
        self.assertIn('if: always() && inputs.require-eventpipe-traces', section.split('        run: |\n')[0])
        return textwrap.dedent(section.split('        run: |\n')[1].split('      - name:')[0])

    def test_linux_parameterized_filenames_are_safe_for_upload(self):
        # These raw Linux filenames cannot be materialized on Windows. Exercise
        # the workflow's renames through Path doubles on either test platform.
        files = []
        for policy in ('Default', 'Disabled', 'Enabled'):
            for extension in ('.nettrace', '.speedscope.json'):
                path = Mock()
                path.name = f'Fixture.ScriptLength(Policy_ "{policy}")-20261008-103000' + extension
                path.is_file.return_value = True
                path.with_name.return_value.exists.return_value = False
                files.append(path)
        root = Mock()
        root.rglob.return_value = files
        script = self.portable_names_step()
        with patch('pathlib.Path', return_value=root):
            exec(compile(script, '<portable-names-step>', 'exec'), {})
        for path in files:
            path.with_name.assert_called_once_with(path.name.replace('"', '_'))
            path.rename.assert_called_once_with(path.with_name.return_value)

    def test_portable_filename_collision_cannot_overwrite_evidence(self):
        path = Mock()
        path.name = 'Fixture(Policy_ "Enabled").nettrace'
        path.is_file.return_value = True
        path.with_name.return_value.exists.return_value = True
        root = Mock()
        root.rglob.return_value = [path]
        script = self.portable_names_step()
        with patch('pathlib.Path', return_value=root):
            with self.assertRaisesRegex(AssertionError, 'Profile artifact name collision'):
                exec(compile(script, '<portable-names-step>', 'exec'), {})
        path.rename.assert_not_called()

    def run_profiles(self, change=None, parameterized=False):
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
                if parameterized:
                    cases = [{'FullName': f'Fixture.ScriptLength(Policy: "{policy}")'}
                             for policy in ('Default', 'Disabled', 'Enabled')]
                (reports / 'Fixture-report-full-compressed.json').write_text(json.dumps({'Benchmarks': cases}))
                for case in cases:
                    name = case['FullName'] + '-20261008-103000'
                    # Portable form of BDN's Linux filename: colon becomes an underscore;
                    # quotes survive BDN but cannot be created on Windows test hosts.
                    if parameterized:
                        name = name.replace(':', '_').replace('"', '_')
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

    def test_parameterized_case_names_match_portable_trace_names(self):
        result = self.run_profiles(parameterized=True)
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_missing_parameterized_case_cannot_be_hidden_by_another_policy(self):
        def change(artifacts):
            prefix = 'Fixture.ScriptLength(Policy_ _Enabled_)-20261008-103000'
            for extension in ('.nettrace', '.speedscope.json'):
                (artifacts / (prefix + extension)).rename(artifacts / ('unrelated' + extension))
        result = self.run_profiles(change, parameterized=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing or duplicate case trace: candidate/', result.stderr)

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
