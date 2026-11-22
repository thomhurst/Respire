import json
import os
import shutil
import subprocess
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

import yaml


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/claude-code-review.yml"

STUBS = r'''
git() { :; }
gh() {
  case "$*" in
    "pr view 42 --json baseRefOid --jq .baseRefOid") echo base ;;
    "pr view 42 --json title,body") echo '{"title":"Fix","body":"Description"}' ;;
    "api repos/example/repo/compare/base...head?per_page=1 --jq .merge_base_commit.sha") echo merge-base ;;
    "api --paginate repos/example/repo/issues/42/comments?per_page=100")
      echo '[{"id":1,"body":"Conversation finding","user":{"login":"reviewer"}}]'
      echo '[{"id":2,"body":"Conversation reply","user":{"login":"author"}}]'
      ;;
    "api --paginate repos/example/repo/pulls/42/reviews?per_page=100")
      echo '[{"id":3,"body":"Review finding","state":"CHANGES_REQUESTED","user":{"login":"reviewer"}}]'
      ;;
    "api --paginate repos/example/repo/pulls/42/comments?per_page=100")
      echo '[{"id":4,"node_id":"root","body":"Inline finding","user":{"login":"reviewer"}}]'
      echo '[{"id":5,"node_id":"reply","in_reply_to_id":4,"body":"Inline reply","user":{"login":"author"}}]'
      ;;
    "api graphql --paginate "*)
      echo '{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[{"isResolved":true,"isOutdated":false,"comments":{"nodes":[{"id":"root"}]}}]}}}}}'
      ;;
    *) echo "Unexpected gh call: $*" >&2; return 1 ;;
  esac
}
'''


@unittest.skipUnless(shutil.which("bash") and shutil.which("jq"), "bash and jq are required")
class ClaudeReviewContextTests(unittest.TestCase):
    def test_context_includes_all_review_surfaces_and_paginated_replies(self):
        workflow = yaml.load(WORKFLOW.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        steps = workflow["jobs"]["claude-review"]["steps"]
        step = next(step for step in steps if step["name"] == "Write pull request context and diff")

        with TemporaryDirectory() as directory:
            result = subprocess.run(
                ["bash", "-e", "-o", "pipefail", "-c", STUBS + step["run"]],
                cwd=directory, env=dict(os.environ, GH_REPO="example/repo", PR_NUMBER="42", REVIEW_HEAD_SHA="head"),
                capture_output=True, text=True, timeout=10,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            context = Path(directory) / "pr-diff"

            def read(name):
                return json.loads((context / name).read_text(encoding="utf-8"))

            self.assertEqual(read("context.json"), {"title": "Fix", "body": "Description"})
            self.assertEqual([comment["body"] for comment in read("conversation.json")],
                             ["Conversation finding", "Conversation reply"])
            self.assertEqual(read("reviews.json")[0]["body"], "Review finding")
            self.assertEqual(read("reviews.json")[0]["state"], "CHANGES_REQUESTED")
            root, reply = read("inline-comments.json")
            self.assertEqual((root["body"], reply["body"]), ("Inline finding", "Inline reply"))
            self.assertEqual(reply["in_reply_to_id"], root["id"])
            self.assertEqual(read("review-threads.json"),
                             [{"rootCommentNodeId": root["node_id"], "isResolved": True, "isOutdated": False}])
