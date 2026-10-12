import json
import os
import textwrap
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory
from unittest.mock import patch

import yaml

GUIDE = 'website/docs/guides/transport-write-evidence.md'
MERGE, PARENT, HEAD = 'a' * 40, 'b' * 40, 'c' * 40
BASELINE, CANDIDATE = 'd' * 40, 'e' * 40


def guide(request=None):
    text = '# Transport write evidence\n\nNarrative.\n'
    if request is None:
        return text
    body = request if isinstance(request, str) else json.dumps(request, indent=2)
    return text + '\n<!-- selection-comparison-request -->\n```json\n' + body + '\n```\n'


def pinned(**overrides):
    request = {'issue': 1520, 'baseline': BASELINE, 'candidate': CANDIDATE, 'fixture': CANDIDATE}
    request.update(overrides)
    return request


class TransportSelectionRequestTests(unittest.TestCase):
    def run_revision_step(self, current, previous, actual=None, head=None):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-transport.yml'
        document = yaml.load(workflow.read_text(), Loader=yaml.BaseLoader)
        step = next(step for step in document['jobs']['transport-comparison']['steps']
                    if step.get('id') == 'revisions')
        with TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / 'output'
            checkout = root / 'request' / GUIDE
            checkout.parent.mkdir(parents=True)
            checkout.write_text(current)
            variables = dict(MERGE_SHA=MERGE, EXPECTED_HEAD_SHA=HEAD, EVENT_BASE_SHA='f' * 40,
                             WORKFLOW_SHA='9' * 40, GITHUB_OUTPUT=str(output))
            calls = []

            def git(arguments, **_):
                calls.append(arguments)
                if arguments[3:] == ['rev-parse', 'HEAD']:
                    return (actual or MERGE) + '\n'
                if arguments[3:] == ['show', '-s', '--format=%P', 'HEAD']:
                    return PARENT + ' ' + (head or HEAD) + '\n'
                if arguments[3:] == ['show', PARENT + ':' + GUIDE]:
                    return previous
                raise AssertionError('Unexpected git call: ' + repr(arguments))

            cwd = Path.cwd()
            try:
                os.chdir(root)
                with patch.dict(os.environ, variables), patch('subprocess.check_output', side_effect=git):
                    exec(compile(textwrap.dedent(step['run']), '<transport-revision-step>', 'exec'), {})
                outputs = dict(line.split('=', 1) for line in output.read_text().splitlines())
                provenance = json.loads((root / 'selection-request.log').read_text())
                return outputs, provenance, (root / 'revisions.log').read_text()
            finally:
                os.chdir(cwd)

    def assert_default_revisions(self, outputs, provenance):
        self.assertEqual(outputs, {'baseline-sha': PARENT, 'candidate-sha': MERGE, 'fixture-sha': MERGE})
        self.assertIsNone(provenance['selectionRequest'])

    def test_absent_request_compares_merge_against_its_parent(self):
        outputs, provenance, log = self.run_revision_step(guide(), guide())
        self.assert_default_revisions(outputs, provenance)
        self.assertIn('head=' + HEAD, log)

    def test_new_request_pins_all_three_revisions(self):
        request = pinned()
        outputs, provenance, log = self.run_revision_step(guide(request), guide())
        self.assertEqual(outputs, {'baseline-sha': BASELINE, 'candidate-sha': CANDIDATE, 'fixture-sha': CANDIDATE})
        self.assertEqual(provenance['selectionRequest'], request)
        self.assertEqual(provenance['requestSource'], MERGE)
        for field, revision in [('request-source', MERGE), ('baseline', BASELINE), ('candidate', CANDIDATE),
                                ('fixture', CANDIDATE), ('head', HEAD)]:
            self.assertIn(field + '=' + revision, log)

    def test_changed_request_replaces_inherited_request(self):
        request = pinned(baseline='1' * 40)
        outputs, provenance, _ = self.run_revision_step(guide(request), guide(pinned()))
        self.assertEqual(outputs['baseline-sha'], '1' * 40)
        self.assertEqual(provenance['selectionRequest'], request)

    def test_unchanged_inherited_request_does_not_redirect_later_prs(self):
        outputs, provenance, _ = self.run_revision_step(guide(pinned()), guide(pinned()))
        self.assert_default_revisions(outputs, provenance)

    def test_reformatted_inherited_request_is_still_unchanged(self):
        compact = json.dumps(pinned(), separators=(',', ':'))
        outputs, provenance, _ = self.run_revision_step(guide(compact), guide(pinned()))
        self.assert_default_revisions(outputs, provenance)

    def test_removed_request_restores_default_revisions(self):
        outputs, provenance, _ = self.run_revision_step(guide(), guide(pinned()))
        self.assert_default_revisions(outputs, provenance)

    def test_moving_ref_pin_is_rejected(self):
        for key in ('baseline', 'candidate', 'fixture'):
            with self.subTest(key=key), self.assertRaisesRegex(AssertionError, 'pinned full SHA'):
                self.run_revision_step(guide(pinned(**{key: 'origin/main'})), guide())

    def test_abbreviated_or_uppercase_pin_is_rejected(self):
        for value in (CANDIDATE[:12], CANDIDATE.upper()):
            with self.subTest(value=value), self.assertRaisesRegex(AssertionError, 'pinned full SHA'):
                self.run_revision_step(guide(pinned(baseline=value)), guide())

    def test_unknown_or_missing_fields_are_rejected(self):
        extra = pinned(control='1' * 40)
        missing = pinned()
        del missing['fixture']
        for request in (extra, missing):
            with self.subTest(request=request), self.assertRaisesRegex(AssertionError, 'Unknown request fields'):
                self.run_revision_step(guide(request), guide())

    def test_other_issue_is_rejected(self):
        with self.assertRaisesRegex(AssertionError, 'Unsupported selection request'):
            self.run_revision_step(guide(pinned(issue=1521)), guide())

    def test_mismatched_candidate_and_fixture_are_rejected(self):
        with self.assertRaisesRegex(AssertionError, 'candidate and fixture at the same revision'):
            self.run_revision_step(guide(pinned(fixture='1' * 40)), guide())

    def test_duplicate_request_blocks_are_rejected(self):
        twice = guide(pinned()) + guide(pinned(baseline='1' * 40))
        with self.assertRaisesRegex(AssertionError, 'Ambiguous selection request'):
            self.run_revision_step(twice, guide())

    def test_wrong_candidate_merge_is_rejected_before_reading_request(self):
        with self.assertRaisesRegex(AssertionError, 'Wrong candidate merge'):
            self.run_revision_step(guide(pinned()), guide(), actual='1' * 40)

    def test_wrong_head_is_rejected_before_reading_request(self):
        with self.assertRaisesRegex(AssertionError, 'Wrong PR head'):
            self.run_revision_step(guide(pinned()), guide(), head='1' * 40)

    def test_request_checkouts_do_not_persist_credentials(self):
        workflow = Path(__file__).resolve().parents[2] / '.github/workflows/benchmark-transport.yml'
        document = yaml.load(workflow.read_text(), Loader=yaml.BaseLoader)
        checkouts = [step['with'] for step in document['jobs']['transport-comparison']['steps']
                     if step.get('uses', '').startswith('actions/checkout@')]
        paths = {checkout['path'] for checkout in checkouts}
        self.assertTrue({'request', 'candidate', 'fixture', 'baseline'} <= paths)
        for checkout in checkouts:
            with self.subTest(path=checkout['path']):
                self.assertEqual(checkout.get('persist-credentials'), 'false')


if __name__ == '__main__':
    unittest.main()
