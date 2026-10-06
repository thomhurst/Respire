import argparse
import json
import os
from pathlib import Path
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import Mock, patch

from smoke_topology_samples import Smoke, validate_cluster, validate_sentinel


CLUSTER = """Discovered 3 shards from one seed.
PASS: respire:cluster-sample:{a}, slot 15495
PASS: respire:cluster-sample:{b}, slot 3300
PASS: respire:cluster-sample:{c}, slot 7365
Cluster sample completed; all three values round-tripped.
"""
SENTINEL = """PASS 1: primary 127.0.0.1:7100
Iteration 2 failed: RespireConnectionException: connection closed
PASS 2: primary 127.0.0.1:7101
Sentinel sample completed: 2 successful round trips.
"""


class OutputContracts(unittest.TestCase):
    def test_cluster_requires_each_range_and_completion(self):
        validate_cluster(CLUSTER)
        for bad in (CLUSTER.replace("7365", "3301"), CLUSTER.replace("15495", "16384"),
                    CLUSTER.replace("{c}", "{b}"), CLUSTER.replace("Discovered 3", "Discovered 2"),
                    CLUSTER.split("Cluster sample completed")[0]):
            with self.subTest(output=bad), self.assertRaises(RuntimeError):
                validate_cluster(bad)

    def test_sentinel_requires_both_endpoints_and_completion(self):
        validate_sentinel(SENTINEL, 7100, 7101)
        for bad in (SENTINEL.replace(":7101", ":7100"), SENTINEL.split("Sentinel sample completed")[0],
                    SENTINEL.replace(":7100", ":temporary").replace(":7101", ":7100").replace(":temporary", ":7101"),
                    "Sentinel sample completed: 2 successful round trips.\n"):
            with self.subTest(output=bad), self.assertRaises(RuntimeError):
                validate_sentinel(bad, 7100, 7101)

    def test_same_or_unknown_primary_is_not_a_promotion(self):
        for before, after in ((7100, 7100), (7100, 7102)):
            with self.subTest(before=before, after=after), self.assertRaises(RuntimeError):
                validate_sentinel(SENTINEL, before, after)


class LifecycleContracts(unittest.TestCase):
    @staticmethod
    def smoke(folder):
        return Smoke(argparse.Namespace(artifacts=Path(folder), project="controlled-smoke", sample="Cluster"))

    @unittest.skipUnless(sys.platform == "linux", "Controller process supervision requires Linux")
    def test_timeout_stops_owned_process_and_keeps_log(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            handles = []
            start = smoke.start

            def record_start(*arguments):
                handle = start(*arguments)
                handles.append(handle)
                return handle

            smoke.start = record_start
            with self.assertRaises(subprocess.TimeoutExpired):
                smoke.run([sys.executable, "-c", "import time; time.sleep(60)"], "timeout-control", 0.1)
            self.assertIsNotNone(handles[0][0].poll())
            self.assertTrue(handles[0][1].closed)
            self.assertTrue((Path(folder) / "001-timeout-control.log").exists())

    def test_failed_diagnostics_do_not_prevent_owned_cleanup(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            commands = []

            def run(command, name, timeout=15):
                commands.append(command)
                if name != "compose-down":
                    raise RuntimeError("controlled diagnostic failure")

            smoke.run = run
            smoke.cleanup()
            self.assertEqual(commands, [])  # No ownership: no Docker operation, including cleanup.
            smoke.owned = True
            smoke.cleanup()
            self.assertEqual(commands[-1], smoke.compose + ["down", "--volumes", "--remove-orphans", "--rmi", "local", "--timeout", "10"])

    @unittest.skipUnless(sys.platform == "linux", "Controller process supervision requires Linux")
    def test_polling_keeps_only_latest_output(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            for value in range(3):
                self.assertEqual(smoke.run([sys.executable, "-c", f"print({value})"], "poll", keep_log=False), str(value))
            self.assertEqual([path.name for path in Path(folder).iterdir()], ["poll-latest.log"])
            self.assertEqual((Path(folder) / "poll-latest.log").read_text().strip(), "2")

    def test_promotion_poll_retries_transient_inspection_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            predicate = Mock(side_effect=[RuntimeError("connection lost"), False, True])
            with patch("smoke_topology_samples.time.sleep") as sleep:
                smoke.wait_for(predicate, 5, "controlled promotion", retry_errors=(RuntimeError,))
            self.assertEqual(predicate.call_count, 3)
            self.assertEqual([call.args for call in sleep.call_args_list], [(1,), (1,)])

    def test_expired_poll_retains_last_error_without_extra_sleep(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            failure = RuntimeError("last inspection failed")
            with patch("smoke_topology_samples.time.monotonic", side_effect=[0, 0, 2, 2]), \
                    patch("smoke_topology_samples.time.sleep") as sleep:
                with self.assertRaises(TimeoutError) as error:
                    smoke.wait_for(Mock(side_effect=failure), 1, "promotion", retry_errors=(RuntimeError,))
            self.assertIs(error.exception.__cause__, failure)
            sleep.assert_not_called()

    def test_promotion_retries_command_timeout_before_success(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            smoke.redis = Mock(return_value="OK")
            smoke.primary = Mock(side_effect=[subprocess.TimeoutExpired("redis-cli", 15), 7101])
            smoke.output = Mock(return_value=SENTINEL)
            with patch("smoke_topology_samples.time.sleep") as sleep:
                self.assertEqual(smoke.follow_promotion(7100), 7101)
            self.assertEqual(smoke.primary.call_count, 2)
            sleep.assert_called_once_with(1)
            self.assertEqual(json.loads((Path(folder) / "promotion.json").read_text()),
                             {"before": 7100, "after": 7101})

    def test_unsupported_platform_rejected_before_process_start(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            with patch("smoke_topology_samples.sys.platform", "win32"):
                with self.assertRaisesRegex(RuntimeError, "requires Linux"):
                    smoke.start(["unused"], smoke.sample_log)
            self.assertFalse(smoke.sample_log.exists())

    def test_failed_client_recovery_retains_observed_promotion(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            path = Path(folder) / "promotion.json"

            def request(*arguments):
                self.assertEqual(json.loads(path.read_text()), {"before": 7100, "after": None})
                return "OK"

            def wait(predicate, seconds, description, **options):
                if description == "Sentinel promotion":
                    self.assertTrue(predicate())
                else:
                    raise TimeoutError("client never reached promoted primary")

            smoke.redis = request
            smoke.primary = lambda: 7101
            smoke.wait_for = wait
            with self.assertRaises(TimeoutError):
                smoke.follow_promotion(7100)
            self.assertEqual(json.loads(path.read_text()), {"before": 7100, "after": 7101})

    def test_rejected_promotion_retains_initial_primary(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            smoke.redis = lambda *arguments: "ERR rejected"
            with self.assertRaises(RuntimeError):
                smoke.follow_promotion(7100)
            self.assertEqual(json.loads((Path(folder) / "promotion.json").read_text()), {"before": 7100, "after": None})

    def test_cleanup_ignores_repeated_signals_and_restores_handlers(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            original = {kind: signal.getsignal(kind) for kind in (signal.SIGINT, signal.SIGTERM)}
            observed = []
            smoke.owned = True

            def cleanup():
                observed.extend(signal.getsignal(kind) for kind in original)
                raise RuntimeError("controlled cleanup failure")

            smoke.cleanup_compose = cleanup
            with self.assertRaises(RuntimeError):
                smoke.cleanup()
            self.assertEqual(observed, [signal.SIG_IGN, signal.SIG_IGN])
            self.assertEqual({kind: signal.getsignal(kind) for kind in original}, original)

    def test_existing_image_prevents_cleanup_ownership(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            smoke.run = Mock(side_effect=lambda command, name: "existing-image-id" if name == "existing-image" else "")
            with self.assertRaisesRegex(RuntimeError, "image already exists"):
                smoke.execute()
            smoke.run.assert_any_call(
                ["docker", "image", "ls", "--format", "{{.ID}}", "controlled-smoke-cluster:latest"], "existing-image")
            self.assertFalse(smoke.owned)
            self.assertFalse((Path(folder) / "owned-project.txt").exists())

    @unittest.skipUnless(sys.platform == "linux" and shutil.which("pwsh"), "Requires Linux and PowerShell")
    def test_stop_terminates_actual_guard_wrapper_workload_and_descendant(self):
        with tempfile.TemporaryDirectory() as folder:
            smoke = self.smoke(folder)
            marker = Path(folder) / "workload.json"
            payload = (
                "import json,os,pathlib,subprocess,sys,time; "
                "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(60)']); "
                f"marker=pathlib.Path({str(marker)!r}); temporary=marker.with_suffix('.tmp'); "
                "temporary.write_text(json.dumps([os.getppid(),os.getpid(),child.pid])); "
                "print('controlled guarded workload started',flush=True); temporary.replace(marker); time.sleep(60)"
            )
            # Exercise Smoke.dotnet and the real guard. Only the workload executable is
            # substituted, as in Test-InvokeAgentDotNet.ps1; no SDK compilation is needed.
            command = smoke.dotnet(["-c", payload], 20, dotnet_path=sys.executable)
            # Reproduce a cold PowerShell startup exceeding the old ten-second readiness limit.
            # The delay precedes the guard, whose workload timeout remains twenty seconds.
            command[-1] = "Start-Sleep -Seconds 11; " + command[-1]
            handle = smoke.start(command, smoke.sample_log)
            smoke.sample = handle
            observers = []
            try:
                # Match the controller's thirty-second outer startup/cleanup margin.
                # wait_for also fails immediately if the guarded process exits.
                smoke.wait_for(marker.exists, 50, "guarded workload readiness")
                # pidfds pin identity before stopping; PID reuse cannot satisfy the check.
                for pid in json.loads(marker.read_text()):
                    observers.append(os.pidfd_open(pid))
                smoke.stop(handle)
                self.assertIsNotNone(handle[0].poll())
                for observer in observers:
                    self.assertTrue(select.select([observer], [], [], 5)[0], "Guarded process survived Smoke.stop")
                self.assertTrue(handle[1].closed)
                self.assertIn("controlled guarded workload started", smoke.sample_log.read_text())
            finally:
                smoke.stop(handle)
                for observer in observers:
                    os.close(observer)


if __name__ == "__main__":
    unittest.main()
