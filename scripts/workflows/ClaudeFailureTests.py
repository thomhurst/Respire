import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from ClaudeFailure import report_failure


class ClaudeFailureTests(unittest.TestCase):
    def report(self, messages=None, raw=None):
        with tempfile.TemporaryDirectory() as directory:
            execution = Path(directory) / "execution.json"
            summary = Path(directory) / "summary.md"
            if messages is not None:
                execution.write_text(json.dumps(messages), encoding="utf-8")
            elif raw is not None:
                execution.write_text(raw, encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()) as stdout:
                report_failure(execution, summary)
            heading, message = summary.read_text(encoding="utf-8").split("\n\n", maxsplit=1)
            self.assertEqual(heading, "### Claude Code failure")
            self.assertEqual(stdout.getvalue(), message)
            return message

    def test_api_status_identifies_authentication_failure(self):
        summary = self.report([
            {"type": "assistant", "error": "unknown"},
            {"type": "result", "subtype": "success", "is_error": True,
             "api_error_status": 401, "result": "secret token or prompt"},
        ])
        self.assertIn("Authentication failed", summary)
        self.assertIn("CLAUDE_CODE_OAUTH_TOKEN", summary)
        self.assertNotIn("secret token or prompt", summary)

    def test_assistant_error_identifies_rate_limit_without_http_status(self):
        summary = self.report([
            {"type": "assistant", "error": "rate_limit"},
            {"type": "result", "subtype": "success", "is_error": True},
        ])
        self.assertIn("Rate limit reached", summary)

    def test_assistant_error_can_refine_an_http_status(self):
        summary = self.report([
            {"type": "assistant", "error": "account_on_hold"},
            {"type": "result", "subtype": "success", "is_error": True,
             "api_error_status": 403},
        ])
        self.assertIn("Account access is restricted", summary)

    def test_unknown_fields_and_untrusted_message_content_are_never_printed(self):
        summary = self.report([
            {"type": "user", "error": "authentication_failed"},
            {"type": "assistant", "error": ["secret"], "message": {"error": "rate_limit"}},
            {"type": "result", "subtype": "secret", "is_error": True, "api_error_status": "secret",
             "result": "secret", "errors": ["secret"], "terminal_reason": "secret"},
        ])
        self.assertIn("Unclassified Claude failure", summary)
        self.assertNotIn("secret", summary)

    def test_missing_invalid_or_incomplete_transcript_points_to_action_setup(self):
        for arguments in ({}, {"raw": '["secret'}, {"messages": {"result": "secret"}},
                          {"messages": [{"type": "system", "subtype": "init"}]}):
            with self.subTest(arguments=arguments):
                summary = self.report(**arguments)
                self.assertIn("No Claude result", summary)
                self.assertIn("action log", summary)
                self.assertNotIn("secret", summary)

    def test_tool_denial_only_classifies_a_failed_result(self):
        summary = self.report([
            {"type": "result", "subtype": "success", "is_error": True,
             "permission_denials": [{"tool_input": {"command": "secret"}}]},
        ])
        self.assertIn("Tool permission denied", summary)
        self.assertNotIn("secret", summary)
        summary = self.report([
            {"type": "result", "subtype": "success", "is_error": False,
             "permission_denials": [{"tool_input": {"command": "secret"}}]},
        ])
        self.assertIn("Action failed after Claude completed", summary)
        self.assertNotIn("secret", summary)


if __name__ == "__main__":
    unittest.main()
