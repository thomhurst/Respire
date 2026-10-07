"""Reject cancellation groups that combine independently selected benchmark labels."""

import re
import sys
from pathlib import Path

import yaml


LABEL_EXPRESSION = re.compile(r"\$\{\{\s*github\.event\.label\.name\s*\}\}")


def check_workflow(text):
    # BaseLoader preserves GitHub's `on` key instead of treating it as a YAML 1.1 boolean.
    workflow = yaml.load(text, Loader=yaml.BaseLoader)
    events = workflow.get("on", {})
    if not isinstance(events, dict):
        return []
    pull_request = events.get("pull_request")
    if not isinstance(pull_request, dict) or "labeled" not in pull_request.get("types", []):
        return []

    scopes = [("concurrency", workflow)]
    scopes.extend((f"jobs.{name}.concurrency", job) for name, job in workflow.get("jobs", {}).items())
    errors = []
    for location, scope in scopes:
        if "concurrency" not in scope:
            continue
        concurrency = scope["concurrency"]
        group = concurrency.get("group", "") if isinstance(concurrency, dict) else concurrency
        if not isinstance(group, str) or not LABEL_EXPRESSION.search(group):
            errors.append(f"{location}: cancellation group must include ${{{{ github.event.label.name }}}}")
    return errors


def check_directory(workflows):
    errors = []
    for path in sorted(workflows.glob("benchmark-*")):
        if path.suffix not in (".yml", ".yaml"):
            continue
        errors.extend(f"{path.name}: {error}" for error in check_workflow(path.read_text(encoding="utf-8")))
    return errors


def main():
    workflows = Path(__file__).resolve().parents[2] / ".github/workflows"
    errors = check_directory(workflows)
    if errors:
        print("\n".join(errors), file=sys.stderr)
        return 1
    print("Benchmark label cancellation policy passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
