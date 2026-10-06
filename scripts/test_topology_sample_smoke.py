import argparse
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

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
            self.assertEqual(commands[-1], smoke.compose + ["down", "--volumes", "--remove-orphans", "--timeout", "10"])


if __name__ == "__main__":
    unittest.main()
