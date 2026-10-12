import json
import unittest
from pathlib import Path
from tempfile import TemporaryDirectory

from VerifySelectionEvidence import verify_phase


class SelectionEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.phase = "candidate"
        self.evidence = self.root / "results" / self.phase / "selection"
        self.evidence.mkdir(parents=True)
        self.reports = self.evidence.parent / "results"
        self.reports.mkdir()
        self.create_fixture()

    def create_fixture(self, jobs=("MediumRun",), diagnostic=False):
        cases = []
        for job in jobs:
            for connections in (1, 2):
                name = f"Selection.SelectConnection: {job}(LaunchCount=2, UnrollFactor=16) [Connections={connections}]"
                samples = []
                for launch in (1, 2):
                    prefix = f"{job}-{connections}-{launch}"
                    before = dict(revision="a" * 40, fixtureRevision="b" * 40, phase=self.phase,
                                  metricMode="enabled", purpose="diagnostic" if diagnostic else "public",
                                  pid=100 + connections * 2 + launch, caseName=name, launch=launch,
                                  timestamp=1, jitFile=prefix + ".jit.txt",
                                  environment={"DOTNET_TieredCompilation": "0"}, files={},
                                  platform="linux", perfMap=prefix + ".perf.map", affinityMask=4)
                    before["files"][f"/proc/{before['pid']}/status"] = "Cpus_allowed_list:\t2\n"
                    before["files"][f"/proc/{before['pid']}/stat"] = "process scheduling record"
                    for tid in (before["pid"], before["pid"] + 1000):
                        task = f"/proc/{before['pid']}/task/{tid}"
                        for field in ("stat", "schedstat", "sched"):
                            before["files"][task + "/" + field] = "thread scheduling record"
                        before["files"][task + "/status"] = "Cpus_allowed_list:\t2\n"
                    self.write(self.evidence / (prefix + ".BeforeActualRun.json"), before)
                    self.write(self.evidence / (prefix + ".AfterActualRun.json"), dict(before, timestamp=2))
                    (self.evidence / before["jitFile"]).write_text("Assembly listing for method SelectConnection (FullOpts)")
                    (self.evidence / before["perfMap"]).write_text("1234 10 SelectConnection")
                    modes = ("Workload",) if job == "selection-no-subtraction" else ("Workload", "Overhead")
                    samples.extend(dict(LaunchIndex=launch, IterationMode=mode, IterationStage="Actual",
                                        Operations=16, Nanoseconds=160) for mode in modes)
                cases.append(dict(Method="SelectConnection", DisplayInfo=name, Parameters=f"Connections={connections}",
                                  Statistics={"Mean": 10}, Measurements=samples))
        self.report = self.reports / "Selection-report-full-compressed.json"
        self.write(self.report, {"Benchmarks": cases})

    @staticmethod
    def write(path, value):
        path.write_text(json.dumps(value))

    def verify(self):
        return verify_phase(self.root, self.phase, "a" * 40, "b" * 40, "enabled")

    def change_before(self, **changes):
        path = next(self.evidence.glob("*.BeforeActualRun.json"))
        self.write(path, dict(json.loads(path.read_text()), **changes))

    def test_every_measured_launch_is_bound(self):
        launches = self.verify()
        self.assertEqual(len(launches), 4)
        self.assertTrue(all(len(launch["samples"]) == 2 for launch in launches))

    def test_last_launch_only_is_rejected(self):
        next(self.evidence.glob("*-1.BeforeActualRun.json")).unlink()
        with self.assertRaisesRegex(AssertionError, "Missing launch evidence"):
            self.verify()

    def test_wrong_revision_or_metric_mode_is_rejected(self):
        for changes in ({"revision": "c" * 40}, {"metricMode": "disabled"}):
            with self.subTest(changes=changes):
                self.change_before(**changes)
                with self.assertRaisesRegex(AssertionError, "Wrong"):
                    self.verify()
                self.change_before(revision="a" * 40, metricMode="enabled")

    def test_separate_disassembly_cannot_replace_measured_code(self):
        path = next(self.evidence.glob("*.jit.txt"))
        path.unlink()
        extra = self.evidence / "disassembly"
        extra.mkdir()
        (extra / path.name).write_text("Assembly listing for method SelectConnection (FullOpts)")
        with self.assertRaisesRegex(AssertionError, "Missing process JIT capture"):
            self.verify()

    def test_changed_pid_is_rejected(self):
        path = next(self.evidence.glob("*.AfterActualRun.json"))
        self.write(path, dict(json.loads(path.read_text()), pid=999))
        with self.assertRaisesRegex(AssertionError, "Changed identity"):
            self.verify()

    def test_missing_raw_overhead_is_rejected(self):
        report = json.loads(self.report.read_text())
        report["Benchmarks"][0]["Measurements"] = [sample for sample in report["Benchmarks"][0]["Measurements"]
                                                    if sample["IterationMode"] != "Overhead"]
        self.write(self.report, report)
        with self.assertRaisesRegex(AssertionError, "Wrong overhead control"):
            self.verify()

    def test_linux_requires_code_addresses_and_scheduling(self):
        self.change_before(perfMap=None)
        with self.assertRaisesRegex(AssertionError, "Missing Linux code addresses"):
            self.verify()

    def test_missing_or_unavailable_scheduling_is_rejected_at_both_boundaries(self):
        for signal in ("BeforeActualRun", "AfterActualRun"):
            path = next(self.evidence.glob(f"*.{signal}.json"))
            original = path.read_text()
            record = json.loads(original)
            for name in tuple(record["files"]):
                for value in (None, "", "UNAVAILABLE: process exited"):
                    with self.subTest(signal=signal, name=name, value=value):
                        changed = json.loads(original)
                        if value is None:
                            del changed["files"][name]
                        else:
                            changed["files"][name] = value
                        self.write(path, changed)
                        # An exited non-main task is allowed, unlike a live task
                        # missing one of its scheduling records.
                        if name.endswith("/status") and "/task/" in name and f"/task/{record['pid']}/" not in name:
                            self.verify()
                        else:
                            with self.assertRaisesRegex(AssertionError, "scheduling evidence"):
                                self.verify()
                        path.write_text(original)

    def test_exited_thread_may_be_absent_after_measurement(self):
        path = next(self.evidence.glob("*.AfterActualRun.json"))
        record = json.loads(path.read_text())
        exited = f"/proc/{record['pid']}/task/{record['pid'] + 1000}/"
        record["files"] = {name: value for name, value in record["files"].items() if not name.startswith(exited)}
        self.write(path, record)
        self.assertEqual(len(self.verify()), 4)

    def create_controls(self):
        self.phase = "selection-candidate"
        self.evidence = self.root / "results" / self.phase / "selection"
        self.evidence.mkdir(parents=True)
        self.reports = self.evidence.parent / "results"
        self.reports.mkdir()
        self.create_fixture(("selection-reference", "selection-tiering-off", "selection-pinned", "selection-no-subtraction"), True)

    def test_all_live_threads_require_the_requested_affinity_at_both_boundaries(self):
        self.create_controls()
        for signal in ("BeforeActualRun", "AfterActualRun"):
            path = next(self.evidence.glob(f"selection-pinned-*.{signal}.json"))
            original = path.read_text()
            record = json.loads(original)
            for name in tuple(record["files"]):
                if not name.endswith("/status"):
                    continue
                for cpu_list in ("2-3", "3", "2,3"):
                    with self.subTest(signal=signal, name=name, cpu_list=cpu_list):
                        changed = json.loads(original)
                        changed["files"][name] = f"Cpus_allowed_list:\t{cpu_list}\n"
                        self.write(path, changed)
                        with self.assertRaisesRegex(AssertionError, "Affinity was not applied to every task"):
                            self.verify()
                        path.write_text(original)

    def test_missing_or_changed_affinity_target_is_rejected(self):
        self.create_controls()
        path = next(self.evidence.glob("selection-pinned-*.BeforeActualRun.json"))
        original = path.read_text()
        for mask in (None, 0, 3, 8):
            self.write(path, dict(json.loads(original), affinityMask=mask))
            with self.assertRaisesRegex(AssertionError, "affinity target"):
                self.verify()
        path.write_text(original)

    def test_workload_without_tier_evidence_is_rejected(self):
        next(self.evidence.glob("*.jit.txt")).write_text("Assembly listing for method SelectConnection")
        with self.assertRaisesRegex(AssertionError, "Missing tier evidence"):
            self.verify()

    def test_all_independent_controls_are_required(self):
        self.create_controls()
        self.assertEqual(len(self.verify()), 16)
        original = self.report.read_text()
        self.report.write_text(original.replace(", UnrollFactor=16", ""))
        with self.assertRaisesRegex(AssertionError, "Uncontrolled engine shape"):
            self.verify()
        self.report.write_text(original)
        path = next(self.evidence.glob("selection-pinned-*.BeforeActualRun.json"))
        self.write(path, dict(json.loads(path.read_text()), files={}))
        with self.assertRaisesRegex(AssertionError, "Missing Linux scheduling evidence"):
            self.verify()


if __name__ == "__main__":
    unittest.main()
