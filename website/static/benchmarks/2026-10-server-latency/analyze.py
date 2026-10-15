"""Reproduce #1744's descriptive analysis; never recompute BDN classifications.

Extract measurements.zip and traces.zip into the same directory, then run:
  python analyze.py EXTRACTED ../2026-10-comparison/38484247008.zip > analysis.json

Uses only Python's standard library. Inclusive stack counts overlap and are not
CPU percentages or critical-path attribution. External samples are kept separate.
"""

import collections
import csv
import io
import json
import sys
import zipfile
from pathlib import Path


def reports(read_text):
    result = {}
    for suite in ("CommonOperationsBenchmarks", "ClientSideCachingBenchmarks"):
        prefix = f"results/Respire.ComparisonBenchmarks.{suite}-report"
        raw = json.loads(read_text(prefix + "-full-compressed.json"))
        stats = {b["Method"]: b for b in raw["Benchmarks"]}
        for row in csv.DictReader(io.StringIO(read_text(prefix + ".csv"))):
            method = row["Method"]
            if suite == "ClientSideCachingBenchmarks" and not method.endswith("_ServerRead"):
                continue
            benchmark = stats[method]
            assert benchmark["Statistics"] and benchmark["Measurements"]
            assert (row["LaunchCount"], row["IterationCount"], row["WarmupCount"]) == ("2", "15", "10")
            result[f"{suite}.{method}"] = {
                "category": row["Categories"],
                "classification": row["MannWhitney(5%)"],
                "meanUs": benchmark["Statistics"]["Mean"] / 1000,
                "errorUs": row["Error"],
                "allocatedBytes": benchmark["Memory"]["BytesAllocatedPerOperation"],
            }
    assert len(result) == 46
    return result


def analyze(root, baseline):
    current = reports(lambda name: (root / name).read_text(encoding="utf-8-sig"))
    with zipfile.ZipFile(baseline) as archive:
        previous = reports(lambda name: archive.read(name).decode("utf-8-sig"))
    assert set(previous) == set(current)
    cases = json.loads((root / "server-latency/cases.json").read_text())
    names = {c["name"] for c in cases}
    assert len(cases) == len(names) == 46 and names == set(current)
    observations = json.loads((root / "server-latency/observations.json").read_text())
    phases = ("bridge-a", "host", "bridge-b", "bridge-profile")
    indexed = {(o["caseName"], o["phase"]): o for o in observations}
    assert len(indexed) == len(observations) == 184
    assert set(indexed) == {(name, phase) for name in names for phase in phases}
    assembly_sets = {json.dumps(o["assemblies"], sort_keys=True) for o in observations}
    assert len(assembly_sets) == 1

    output = {"run": "38695907419", "revision": "8fd9ea63b49b6e13981c674f2f4d6414dc248fa0",
              "baselineRun": "38484247008", "cases": [], "profileTotals": {}}
    totals = collections.Counter()
    for case in sorted(cases, key=lambda c: c["name"]):
        name = case["name"]
        assert current[name]["category"] == previous[name]["category"] == case["category"]
        expected_ops = (100 if "x100 sequential" in case["category"] else
                        50 if case["category"] == "GET x50 concurrent" else
                        200 if case["category"] == "GET x200 pipelined" else 1)
        assert case["operationsPerInvoke"] == expected_ops
        diagnostics = {}
        for phase in phases:
            o = indexed[name, phase]
            assert o["revision"] == output["revision"] and o["runId"] == output["run"]
            assert o["operationsPerInvoke"] == expected_ops
            assert o["operations"] == o["invocations"] * expected_ops > 0
            assert o["mode"] == ("trace" if phase == "bridge-profile" else "plain")
            assert o["endpoint"] == ("127.0.0.1:6380" if phase == "host" else "127.0.0.1:6379")
            diagnostics[phase] = {
                "wallUs": o["wallNanosecondsPerOperation"] / 1000,
                "processCpuUs": o["processCpuNanosecondsPerOperation"] / 1000,
                "processAllocatedBytes": o["processAllocatedBytesPerOperation"],
            }
        stacks = json.loads((root / "server-latency/bridge-profile" / name / "stacks.json").read_text())
        assert stacks["starts"] == stacks["stops"] == 1
        assert stacks["missingStacks"] == stacks["eventsLost"] == 0
        counts = collections.Counter()
        managed = [s for s in stacks["stacks"] if s["type"] == "Managed"]
        for s in stacks["stacks"]:
            counts[s["type"]] += s["count"]
        assert sum(counts.values()) == stacks["samples"] > 0
        totals.update(counts)
        markers = {
            "threadPoolSemaphore": "System.Threading.LowLevelLifoSemaphore.",
            "socketEventLoop": "System.Net.Sockets.SocketAsyncEngine.EventLoop",
            "respireNetworkingFrames": "Respire.Networking.",
            "stackExchangeFrames": "StackExchange.Redis.",
            "diagnosticAdapters": "Adapters+",
            "diagnosticExercise": "g__Exercise|",
            "traceStream": "Microsoft.Diagnostics.NETCore.Client",
        }
        inclusive = {label: sum(s["count"] for s in managed if marker in s["stack"])
                     for label, marker in markers.items()}
        profile = {"sampleTypes": dict(counts), "managedInclusiveMarkers": inclusive,
                   "topManagedStacks": sorted(managed, key=lambda s: -s["count"])[:10]}
        output["cases"].append({"name": name, "category": case["category"],
                                "before": previous[name], "repeat": current[name],
                                "diagnostics": diagnostics, "profile": profile})
    output["profileTotals"] = dict(totals)
    original_ties = [c for c in output["cases"] if c["before"]["classification"] == "Same"]
    assert len(original_ties) == 21
    output["originalTieDispositions"] = dict(collections.Counter(
        c["repeat"]["classification"] for c in original_ties))
    return output


if __name__ == "__main__":
    print(json.dumps(analyze(Path(sys.argv[1]), Path(sys.argv[2])), indent=2))
