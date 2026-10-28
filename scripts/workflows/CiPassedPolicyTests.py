import unittest
from pathlib import Path

from CiPassedPolicy import check_workflow

GUARD = "github.event_name != 'pull_request' || github.event.pull_request.draft == false"
EXPRESSION_NAME = "${{ github.event.pull_request.draft && 'CI (draft)' || 'CI Passed' }}"


def workflow(name=EXPRESSION_NAME, condition=f"always() && ({GUARD})", needs="[unit, lint]", extra=""):
    return f"""
on: {{pull_request: {{}}}}
jobs:
  unit: {{if: "{GUARD}", runs-on: x, steps: []}}
  lint: {{if: "{GUARD}", runs-on: x, steps: []}}
  perf: {{needs: unit, if: "github.event_name == 'push' && github.ref == 'refs/heads/main'", runs-on: x, steps: []}}
{extra}
  ci-passed:
    name: "{name}"
    needs: {needs}
    if: "{condition}"
    runs-on: x
    steps: []
"""


class CiPassedPolicyTests(unittest.TestCase):
    def test_repository_ci_workflow_satisfies_policy(self):
        path = Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml"
        self.assertEqual(check_workflow(path.read_text(encoding="utf-8")), [])

    def test_valid_aggregator_passes_and_push_only_jobs_are_exempt(self):
        self.assertEqual(check_workflow(workflow()), [])

    def test_fixed_name_is_rejected_because_a_skipped_run_would_satisfy_it(self):
        errors = check_workflow(workflow(name="CI Passed"))
        self.assertTrue(any(".name" in error for error in errors), errors)

    def test_expression_must_still_produce_the_required_name(self):
        errors = check_workflow(workflow(name="${{ github.ref && 'Build' }}"))
        self.assertTrue(any(".name" in error for error in errors), errors)

    def test_aggregator_must_run_after_failures_but_not_on_drafts(self):
        self.assertTrue(check_workflow(workflow(condition=GUARD)))
        self.assertTrue(check_workflow(workflow(condition="always()")))

    def test_job_missing_from_needs_is_rejected(self):
        self.assertIn("jobs.ci-passed.needs must include lint", check_workflow(workflow(needs="[unit]")))

    def test_new_job_without_draft_guard_is_rejected(self):
        errors = check_workflow(workflow(needs="[unit, lint, docs]", extra="  docs: {runs-on: x, steps: []}"))
        self.assertIn("jobs.docs.if must skip drafts with `github.event.pull_request.draft == false`", errors)

    def test_missing_aggregator_is_rejected(self):
        self.assertEqual(check_workflow("on: {push: {}}\njobs: {a: {runs-on: x}}"), ["jobs.ci-passed is missing"])


if __name__ == "__main__":
    unittest.main()
