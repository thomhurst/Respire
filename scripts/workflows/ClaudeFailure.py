"""Write a credential-safe job summary for a failed Claude Code action."""

import json
import os
from pathlib import Path


AUTHENTICATION = "Authentication failed. Verify the repository's CLAUDE_CODE_OAUTH_TOKEN."
ACCESS = "Account access is restricted. Check the Claude account and organization settings."
BILLING = "Billing error. Check the Claude account's plan and usage."
RATE_LIMIT = "Rate limit reached. Check Claude usage limits and retry when the limit resets."
UNAVAILABLE = "Claude service error. Check status.claude.com before retrying."
MODEL = "Model unavailable. Check the model name and the Claude account's model access."
INVALID_REQUEST = "Invalid API request. Check the Claude model and action inputs."

ASSISTANT_ERRORS = {
    "authentication_failed": AUTHENTICATION,
    "oauth_org_not_allowed": ACCESS,
    "account_on_hold": ACCESS,
    "verification_required": ACCESS,
    "billing_error": BILLING,
    "rate_limit": RATE_LIMIT,
    "overloaded": UNAVAILABLE,
    "server_error": UNAVAILABLE,
    "model_not_found": MODEL,
    "invalid_request": INVALID_REQUEST,
}

API_STATUSES = {
    400: INVALID_REQUEST,
    401: AUTHENTICATION,
    402: BILLING,
    403: ACCESS,
    404: MODEL,
    429: RATE_LIMIT,
    500: UNAVAILABLE,
    502: UNAVAILABLE,
    503: UNAVAILABLE,
    504: UNAVAILABLE,
    529: UNAVAILABLE,
}

RESULT_ERRORS = {
    "error_max_turns": "Turn limit reached. Check the task and the workflow's Claude turn limit.",
    "error_max_budget_usd": "Budget limit reached. Check the workflow's Claude budget.",
    "error_max_structured_output_retries": "Structured output failed. Check the workflow's Claude output schema.",
}


def failure_message(messages):
    if isinstance(messages, list):
        messages = [item for item in messages if isinstance(item, dict)]
    else:
        messages = []
    result = next((item for item in reversed(messages) if item.get("type") == "result"), None)
    if result is None:
        return "No Claude result was available. Check the action log for setup or SDK startup errors."

    subtype = result.get("subtype")
    if subtype == "success" and result.get("is_error") is False:
        return "Action failed after Claude completed. Check the action log for the failing step."
    if isinstance(subtype, str) and subtype in RESULT_ERRORS:
        return RESULT_ERRORS[subtype]

    assistant_error = next(
        (item["error"] for item in reversed(messages)
         if item.get("type") == "assistant" and item.get("parent_tool_use_id") is None
         and isinstance(item.get("error"), str)),
        None,
    )
    if assistant_error in ASSISTANT_ERRORS:
        return ASSISTANT_ERRORS[assistant_error]

    status = result.get("api_error_status")
    if type(status) is int and status in API_STATUSES:
        return API_STATUSES[status]
    if isinstance(result.get("permission_denials"), list) and result["permission_denials"]:
        return "Tool permission denied. Check the task against the workflow's approved tools."
    if result.get("terminal_reason") == "api_error":
        return "Claude API request failed. Check status.claude.com and the Claude account before retrying."
    return "Unclassified Claude failure. Check the action log and report recurring errors to Claude Code Action."


def report_failure(execution, summary):
    try:
        messages = json.loads(execution.read_text(encoding="utf-8"))
    except (OSError, ValueError, RecursionError):
        messages = None

    # Publish only fixed messages. The transcript can contain credentials and untrusted text.
    message = failure_message(messages)
    with summary.open("a", encoding="utf-8") as output:
        output.write(f"### Claude Code failure\n\n{message}\n")
    print(message)


if __name__ == "__main__":
    report_failure(Path(os.environ.get("CLAUDE_EXECUTION_FILE", "")), Path(os.environ["GITHUB_STEP_SUMMARY"]))
