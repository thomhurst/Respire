import unittest

from PrWorkflowGate import evaluate

OWN = ".github/workflows/ci.yml"


def run(path, conclusion="success", status="completed", created="2026-01-01T00:00:00Z", run_id=1, event="pull_request"):
    return {"path": f".github/workflows/{path}", "name": path, "event": event, "status": status,
            "conclusion": conclusion, "created_at": created, "id": run_id}


class PrWorkflowGateTests(unittest.TestCase):
    def test_passing_runs_pass(self):
        runs = [run("modular-pipeline.yml"), run("claude-code-review.yml", event="pull_request_target"), run("docs.yml", "neutral")]
        self.assertEqual(evaluate(runs, OWN), ([], []))

    def test_failure_cancellation_and_timeout_fail(self):
        runs = [run("modular-pipeline.yml", "failure"), run("docs.yml", "cancelled"), run("agent-guard.yml", "timed_out")]
        pending, failed = evaluate(runs, OWN)
        self.assertEqual((pending, len(failed)), ([], 3))

    def test_queued_or_running_runs_are_pending(self):
        pending, failed = evaluate([run("modular-pipeline.yml", None, "in_progress"), run("docs.yml", None, "queued")], OWN)
        self.assertEqual((len(pending), failed), (2, []))

    def test_newest_run_per_workflow_decides(self):
        older_failure = run("writer-invariants.yml", "failure", created="2026-01-01T00:00:00Z", run_id=1)
        newer_success = run("writer-invariants.yml", "success", created="2026-01-01T01:00:00Z", run_id=2)
        self.assertEqual(evaluate([newer_success, older_failure], OWN), ([], []))
        newest_failure = run("writer-invariants.yml", "failure", created="2026-01-01T02:00:00Z", run_id=3)
        self.assertEqual(len(evaluate([older_failure, newer_success, newest_failure], OWN)[1]), 1)

    def test_skipped_label_run_does_not_hide_an_earlier_failure(self):
        runs = [run("test-full-net8.yml", "failure", created="2026-01-01T00:00:00Z", run_id=1),
                run("test-full-net8.yml", "skipped", created="2026-01-01T01:00:00Z", run_id=2)]
        self.assertEqual(len(evaluate(runs, OWN)[1]), 1)

    def test_skipped_draft_run_does_not_pass_for_the_ready_run(self):
        runs = [run("modular-pipeline.yml", "skipped", created="2026-01-01T00:00:00Z", run_id=1),
                run("modular-pipeline.yml", None, "in_progress", created="2026-01-01T01:00:00Z", run_id=2)]
        self.assertEqual(len(evaluate(runs, OWN)[0]), 1)

    def test_only_skipped_runs_mean_the_workflow_did_not_apply(self):
        self.assertEqual(evaluate([run("docs.yml", "skipped")], OWN), ([], []))

    def test_own_workflow_other_events_and_opt_in_measurements_are_ignored(self):
        runs = [run("ci.yml", "failure"), run("claude.yml", "failure", event="issue_comment"),
                run("benchmark-transport.yml", "failure"), run("server-compatibility.yml", None, "in_progress")]
        self.assertEqual(evaluate(runs, OWN), ([], []))


if __name__ == "__main__":
    unittest.main()
