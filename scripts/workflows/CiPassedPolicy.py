"""Check the contract of ci.yml's `CI Passed` job, the only required status check.

- It needs every other ci.yml job that runs for pull requests, so no job's failure is
  invisible to the merge.
- Its name is an expression. GitHub reports a job skipped by `if:` under its literal name
  when the name is fixed, and a skipped job satisfies a required check. With an
  expression name, a skipped draft run reports the expression text instead, so it cannot
  satisfy `CI Passed`.
- It runs with always() but not on drafts, and every other pull request job skips drafts.
"""

import sys
from pathlib import Path

import yaml

AGGREGATOR = "ci-passed"
REQUIRED_NAME = "CI Passed"
DRAFT_GUARD = "github.event.pull_request.draft == false"


def _push_only(job):
    condition = str(job.get("if", ""))
    return "github.event_name == 'push'" in condition and "pull_request" not in condition


def check_workflow(text):
    # BaseLoader keeps GitHub's `on` key and leaves every scalar a string.
    workflow = yaml.load(text, Loader=yaml.BaseLoader)
    jobs = workflow.get("jobs", {})
    aggregator = jobs.get(AGGREGATOR)
    if not isinstance(aggregator, dict):
        return [f"jobs.{AGGREGATOR} is missing"]

    errors = []
    name = str(aggregator.get("name", "")).strip()
    if not (name.startswith("${{") and name.endswith("}}") and f"'{REQUIRED_NAME}'" in name):
        errors.append(f"jobs.{AGGREGATOR}.name must be one expression that evaluates to '{REQUIRED_NAME}'")
    condition = str(aggregator.get("if", ""))
    if "always()" not in condition or DRAFT_GUARD not in condition:
        errors.append(f"jobs.{AGGREGATOR}.if must combine always() with `{DRAFT_GUARD}`")

    needs = aggregator.get("needs", [])
    needs = [needs] if isinstance(needs, str) else list(needs)
    for job_id, job in jobs.items():
        if job_id == AGGREGATOR or _push_only(job):
            continue
        if job_id not in needs:
            errors.append(f"jobs.{AGGREGATOR}.needs must include {job_id}")
        if DRAFT_GUARD not in str(job.get("if", "")):
            errors.append(f"jobs.{job_id}.if must skip drafts with `{DRAFT_GUARD}`")
    return errors


def main():
    path = Path(__file__).resolve().parents[2] / ".github/workflows/ci.yml"
    errors = check_workflow(path.read_text(encoding="utf-8"))
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("CI Passed aggregator policy passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
