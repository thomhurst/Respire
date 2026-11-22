"""Publish Claude's structured review through the trusted review helper."""

import os
import re
import subprocess
from pathlib import Path

from ClaudeFailure import last_result, read_execution_messages


REVIEW_HELPER = Path(__file__).resolve().parents[2] / ".github/scripts/pr-review-comment.sh"
VERDICT_MARKER = re.compile(r"<!--\s*REVIEW_VERDICT\s*:", re.IGNORECASE)


def post_review(execution):
    result = last_result(read_execution_messages(execution))
    if result is None or result.get("subtype") != "success" or result.get("is_error") is not False:
        print("::error::Claude did not complete a review.")
        return 1

    review = result.get("structured_output")
    if not isinstance(review, dict):
        print("::error::Claude did not return a structured review.")
        return 1

    body = review.get("body")
    verdict = review.get("verdict")
    if (not isinstance(body, str) or not body.strip() or "\0" in body
            or VERDICT_MARKER.search(body) or verdict not in ("CLEAR", "BLOCKING")):
        print("::error::Claude returned an invalid review body or verdict.")
        return 1

    # Pass the markdown as one argument. Shell syntax inside it is review text.
    try:
        completed = subprocess.run([str(REVIEW_HELPER), f"{body.rstrip()}\n\n<!-- REVIEW_VERDICT: {verdict} -->"])
    except (OSError, UnicodeError):
        print("::error::Unable to invoke the review helper.")
        return 1
    return completed.returncode


if __name__ == "__main__":
    raise SystemExit(post_review(Path(os.environ.get("CLAUDE_EXECUTION_FILE", ""))))
