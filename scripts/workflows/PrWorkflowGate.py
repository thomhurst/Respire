"""Wait for every other pull request workflow on a head, and fail if any of them failed.

The `CI Passed` job in ci.yml is the only required status check, so native auto-merge
merges as soon as it passes. Its `needs` cover ci.yml; this gate covers every other
workflow that a pull request event started for the same head: Modular Pipeline, the
path-filtered regression workflows, docs, agent guard, the Claude review, and an
opted-in full .NET 8 run. Without it, a failure in one of them would not stop the merge.

Per workflow file, the newest run that is not `skipped` decides. A run whose jobs were
all skipped (a draft, or an unrelated label) neither passes nor hides an earlier result.
Opt-in measurement workflows started by a label (benchmarks, server compatibility) are
excluded: they report numbers rather than a verdict, and some run for a long time.
"""

import argparse
import json
import os
import sys
import time
import urllib.request
from fnmatch import fnmatch

PR_EVENTS = {"pull_request", "pull_request_target"}
EXCLUDED_PATHS = (
    ".github/workflows/benchmark-*.yml",
    ".github/workflows/server-compatibility.yml",
)
PASSING = {"success", "neutral"}


def evaluate(runs, own_path):
    """Return (pending, failed) workflow descriptions for the given workflow runs."""
    latest = {}
    for run in runs:
        path = run.get("path") or ""
        if run.get("event") not in PR_EVENTS or path == own_path:
            continue
        if any(fnmatch(path, pattern) for pattern in EXCLUDED_PATHS):
            continue
        if run.get("status") == "completed" and run.get("conclusion") == "skipped":
            continue
        key = (run.get("created_at") or "", run.get("id") or 0)
        if path not in latest or key > latest[path][0]:
            latest[path] = (key, run)

    pending, failed = [], []
    for path, (_, run) in sorted(latest.items()):
        label = f"{run.get('name') or path} ({run.get('html_url') or run.get('id')})"
        if run.get("status") != "completed":
            pending.append(f"{label}: {run.get('status')}")
        elif run.get("conclusion") not in PASSING:
            failed.append(f"{label}: {run.get('conclusion')}")
    return pending, failed


def fetch_runs(repo, head_sha, token):
    runs, page = [], 1
    while True:
        request = urllib.request.Request(
            f"https://api.github.com/repos/{repo}/actions/runs?head_sha={head_sha}&per_page=100&page={page}",
            headers={
                "Accept": "application/vnd.github+json",
                "Authorization": f"Bearer {token}",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        with urllib.request.urlopen(request, timeout=30) as response:
            body = json.load(response)
        batch = body.get("workflow_runs", [])
        runs.extend(batch)
        if len(batch) < 100 or len(runs) >= body.get("total_count", 0):
            return runs
        page += 1


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repo", required=True)
    parser.add_argument("--head-sha", required=True)
    parser.add_argument("--own-path", required=True, help="workflow file of the calling run, e.g. .github/workflows/ci.yml")
    parser.add_argument("--timeout-minutes", type=float, default=60)
    parser.add_argument("--poll-seconds", type=float, default=30)
    args = parser.parse_args(argv)
    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        print("GITHUB_TOKEN is required.", file=sys.stderr)
        return 2

    deadline = time.monotonic() + args.timeout_minutes * 60
    while True:
        try:
            pending, failed = evaluate(fetch_runs(args.repo, args.head_sha, token), args.own_path)
        except OSError as error:  # Transient API failures retry until the deadline.
            pending, failed = [f"GitHub API: {error}"], []
        if failed:
            print("Pull request workflows failed:\n  " + "\n  ".join(failed), file=sys.stderr)
            return 1
        if not pending:
            print("Every other pull request workflow on this head passed.")
            return 0
        if time.monotonic() >= deadline:
            print("Timed out waiting for:\n  " + "\n  ".join(pending), file=sys.stderr)
            return 1
        print("Waiting for:\n  " + "\n  ".join(pending), flush=True)
        time.sleep(args.poll_seconds)


if __name__ == "__main__":
    sys.exit(main())
