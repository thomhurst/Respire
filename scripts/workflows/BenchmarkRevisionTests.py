import os
import textwrap
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

import yaml


class BenchmarkRevisionTests(unittest.TestCase):
    def run_revision_step(self, control='', actual=None, head=None):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-compare.yml'
        document = yaml.load(workflow.read_text(), Loader=yaml.BaseLoader)
        step = next(step for step in document['jobs']['compare']['steps']
                    if step.get('id') == 'revisions')
        merge, parent, expected_head = 'a' * 40, 'b' * 40, 'c' * 40
        with TemporaryDirectory() as directory:
            root = Path(directory)
            output, environment = root / 'output', root / 'environment'
            variables = dict(HEAD_SHA=merge, EXPECTED_HEAD_SHA=expected_head,
                             EVENT_BASE_SHA='d' * 40, CONTROL_SHA=control,
                             GITHUB_OUTPUT=str(output), GITHUB_ENV=str(environment))
            previous = Path.cwd()
            try:
                os.chdir(root)
                with patch.dict(os.environ, variables), patch('subprocess.check_output', side_effect=[
                    (actual or merge) + '\n', parent + ' ' + (head or expected_head) + '\n',
                ]):
                    exec(compile(textwrap.dedent(step['run']), '<revision-step>', 'exec'), {})
                return output.read_text(), environment.read_text(), (root / 'revisions.log').read_text()
            finally:
                os.chdir(previous)

    def test_default_control_remains_the_immutable_merge_parent(self):
        output, environment, provenance = self.run_revision_step()
        self.assertEqual(output, 'baseline-sha=' + 'b' * 40 + '\n')
        self.assertEqual(environment, 'BASE_SHA=' + 'b' * 40 + '\n')
        self.assertIn('merge-base=' + 'b' * 40, provenance)

    def test_historical_control_keeps_merge_parent_and_head_provenance(self):
        output, environment, provenance = self.run_revision_step(control='e' * 40)
        self.assertEqual(output, 'baseline-sha=' + 'e' * 40 + '\n')
        self.assertEqual(environment, 'BASE_SHA=' + 'e' * 40 + '\n')
        for field, revision in [('baseline', 'e'), ('merge-base', 'b'), ('head', 'c')]:
            self.assertIn(field + '=' + revision * 40, provenance)

    def test_moving_control_ref_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, 'pinned full control SHA'):
            self.run_revision_step(control='origin/main')

    def test_wrong_candidate_is_rejected_even_with_historical_control(self):
        with self.assertRaisesRegex(AssertionError, 'Wrong candidate merge'):
            self.run_revision_step(control='e' * 40, actual='f' * 40)

    def test_wrong_head_is_rejected_even_with_historical_control(self):
        with self.assertRaisesRegex(AssertionError, 'Wrong PR head'):
            self.run_revision_step(control='e' * 40, head='f' * 40)


if __name__ == '__main__':
    unittest.main()
