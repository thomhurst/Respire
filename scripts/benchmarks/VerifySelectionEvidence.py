"""Bind selection samples to the measured process; reject missing or unrelated evidence."""

import hashlib
import json
import os
import re
import sys
from pathlib import Path


def verify_phase(root, phase, revision, fixture_revision, metric_mode):
    artifacts = root / "results" / phase
    reports = list((artifacts / "results").glob("*-report-full-compressed.json"))
    assert len(reports) == 1, f"Expected one full report: {phase}"
    cases = [case for case in json.loads(reports[0].read_text())["Benchmarks"]
             if case["Method"] == "SelectConnection"]
    diagnostic = phase.startswith("selection-")
    assert len(cases) == (8 if diagnostic else 2), f"Missing selection cases: {phase}"
    if diagnostic:
        assert all("UnrollFactor=16" in case["DisplayInfo"] for case in cases), f"Uncontrolled engine shape: {phase}"
        jobs = ("selection-reference", "selection-tiering-off", "selection-pinned", "selection-no-subtraction")
        for job in jobs:
            for connections in (1, 2):
                assert sum(job + "(" in case["DisplayInfo"] and
                           case["Parameters"] == f"Connections={connections}" for case in cases) == 1, \
                    f"Missing diagnostic control: {phase}, {job}, {connections}"

    evidence = artifacts / "selection"
    before_files = list(evidence.glob("*.BeforeActualRun.json"))
    expected_launches = 1 if phase.startswith("validation-") else 2
    assert len(before_files) == len(cases) * expected_launches, f"Missing launch evidence: {phase}"
    records = [(path, json.loads(path.read_text())) for path in before_files]
    launches = []
    identities = set()
    for case in cases:
        assert case["Statistics"] and case["Statistics"]["Mean"] > 0, f"Failed selection: {phase}"
        for launch in range(1, expected_launches + 1):
            matches = [(path, before) for path, before in records
                       if before["caseName"] == case["DisplayInfo"] and before["launch"] == launch]
            assert len(matches) == 1, f"Unbound launch: {phase}, {case['DisplayInfo']}, {launch}"
            path, before = matches[0]
            after = json.loads(path.with_name(path.name.replace("BeforeActualRun", "AfterActualRun")).read_text())
            for key, value in (("revision", revision), ("fixtureRevision", fixture_revision),
                               ("phase", phase), ("metricMode", metric_mode),
                               ("purpose", "diagnostic" if diagnostic else "public")):
                assert before[key] == after[key] == value, f"Wrong {key}: {path}"
            assert before["pid"] == after["pid"] and before["launch"] == after["launch"], f"Changed identity: {path}"
            assert before["caseName"] == after["caseName"], f"Changed case: {path}"
            assert before["timestamp"] < after["timestamp"], f"Reversed boundaries: {path}"
            identity = (before["pid"], before["caseName"], launch)
            assert identity not in identities, f"Duplicate process: {path}"
            identities.add(identity)
            jit = evidence / before["jitFile"]
            assert Path(before["jitFile"]).name == before["jitFile"] and jit.is_file(), f"Missing process JIT capture: {path}"
            code = jit.read_text()
            assert "Assembly listing for method" in code and "SelectConnection" in code, f"Missing selection code: {jit}"
            assert re.search(r"Tier[01]|FullOpts|MinOpts", code), f"Missing tier evidence: {jit}"
            if before.get("platform") == "linux":
                assert before.get("perfMap"), f"Missing Linux code addresses: {path}"
                assert f"/proc/{before['pid']}/stat" in before["files"], f"Missing Linux scheduling evidence: {path}"
            if before.get("perfMap"):
                perf_map = evidence / before["perfMap"]
                assert Path(before["perfMap"]).name == before["perfMap"] and perf_map.is_file(), f"Missing code addresses: {path}"
                assert "SelectConnection" in perf_map.read_text(), f"Missing selection addresses: {path}"
            samples = [sample for sample in case["Measurements"] if sample["LaunchIndex"] == launch]
            workload = [sample for sample in samples if sample["IterationStage"] == "Actual" and sample["IterationMode"] == "Workload"]
            overhead = [sample for sample in samples if sample["IterationStage"] == "Actual" and sample["IterationMode"] == "Overhead"]
            assert workload and all(sample["Operations"] > 0 for sample in samples), f"Missing raw workload: {path}"
            no_overhead = "selection-no-subtraction(" in case["DisplayInfo"] or phase.startswith("validation-")
            assert bool(overhead) != no_overhead, f"Wrong overhead control: {path}"
            if "selection-tiering-off(" in case["DisplayInfo"]:
                assert before["environment"]["DOTNET_TieredCompilation"] == "0", f"Missing tier control: {path}"
            if "selection-pinned(" in case["DisplayInfo"]:
                for boundary in (before, after):
                    status = boundary["files"].get(f"/proc/{boundary['pid']}/status", "")
                    allowed = re.search(r"Cpus_allowed_list:\s*(\d+)\s*\n", status)
                    assert allowed, f"Affinity was not applied: {path}"
            launches.append({"before": before, "after": after, "samples": samples,
                             "jitSha256": hashlib.sha256(jit.read_bytes()).hexdigest()})
    return launches


def main(root):
    root = Path(root)
    baseline = (root / "baseline.commit").read_text().strip()
    candidate = (root / "candidate.commit").read_text().strip()
    assert re.fullmatch(r"[0-9a-f]{40}", baseline) and re.fullmatch(r"[0-9a-f]{40}", candidate)
    mode = os.environ["RESPIRE_BENCH_ERRORS"]
    assert mode in ("disabled", "enabled")
    phases = ("validation-baseline", "validation-candidate", "baseline-a", "candidate", "baseline-b",
              "selection-baseline-a", "selection-candidate", "selection-baseline-b")
    launches = []
    for phase in phases:
        revision = candidate if phase in ("validation-candidate", "candidate", "selection-candidate") else baseline
        launches.extend(verify_phase(root, phase, revision, candidate, mode))
    (root / "results" / "selection-launches.json").write_text(json.dumps(launches, indent=2))
    print(f"Verified {len(launches)} selection processes ({mode}); raw samples and boundary evidence retained.")


if __name__ == "__main__":
    main(sys.argv[1])
