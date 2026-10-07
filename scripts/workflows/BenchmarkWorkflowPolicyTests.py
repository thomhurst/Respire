import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from BenchmarkWorkflowPolicy import check_directory, check_workflow


class BenchmarkWorkflowPolicyTests(unittest.TestCase):
    def test_new_workflow_is_discovered_without_a_maintained_inventory(self):
        with TemporaryDirectory() as directory:
            workflows = Path(directory)
            (workflows / "benchmark-new.yaml").write_text("""
on: {pull_request: {types: [labeled]}}
concurrency: {group: fixed}
""", encoding="utf-8")
            errors = check_directory(workflows)
            self.assertEqual(len(errors), 1)
            self.assertIn("benchmark-new.yaml: concurrency:", errors[0])

    def test_unscoped_workflow_group_is_rejected(self):
        errors = check_workflow("""
on: {pull_request: {types: [labeled]}}
concurrency: {group: '${{ github.workflow }}-${{ github.ref }}'}
jobs: {}
""")
        self.assertEqual(len(errors), 1)
        self.assertIn("concurrency:", errors[0])

    def test_label_in_comment_or_literal_does_not_clear_group(self):
        for group in ("fixed", "github.event.label.name"):
            with self.subTest(group=group):
                errors = check_workflow("""
on:
  pull_request:
    types:
      - labeled
# ${{ github.event.label.name }}
concurrency:
  group: """ + group + "\n")
                self.assertEqual(len(errors), 1)

    def test_job_group_cannot_hide_behind_scoped_workflow_group(self):
        errors = check_workflow("""
on: {pull_request: {types: [labeled]}}
concurrency: '${{ github.workflow }}-${{ github.event.label.name }}'
jobs:
  compare:
    concurrency: {group: '${{ github.workflow }}-${{ matrix.framework }}'}
""")
        self.assertEqual(len(errors), 1)
        self.assertIn("jobs.compare.concurrency:", errors[0])

    def test_boolean_label_expression_does_not_keep_labels_independent(self):
        self.assertEqual(len(check_workflow("""
on: {pull_request: {types: [labeled]}}
concurrency: "${{ github.event.label.name == 'one-label' }}"
""")), 1)

    def test_scoped_workflow_and_job_groups_pass(self):
        self.assertEqual(check_workflow("""
on: {pull_request: {types: [labeled]}, workflow_dispatch: {}}
concurrency: '${{ github.workflow }}-${{ github.event.label.name }}'
jobs:
  compare:
    concurrency:
      group: >-
        ${{ github.workflow }}-${{ matrix.framework }}-${{ github.event.label.name }}
"""), [])

    def test_manual_reusable_and_scheduled_groups_are_exempt(self):
        for events in ("workflow_dispatch", "[workflow_dispatch, push]",
                       "{workflow_call: {}}", "{schedule: [{cron: '* * * * *'}]}"):
            with self.subTest(events=events):
                self.assertEqual(check_workflow(
                    f"on: {events}\nconcurrency: {{group: performance-docs-publish}}\n"), [])

    def test_no_cancellation_group_needs_no_label_scope(self):
        self.assertEqual(check_workflow("on: {pull_request: {types: [labeled]}}\njobs: {}\n"), [])


if __name__ == "__main__":
    unittest.main()
