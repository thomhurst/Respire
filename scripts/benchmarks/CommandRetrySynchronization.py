"""Issue #2065: task/CLR events and Linux blocking syscalls on the exact timing pair.

This perturbs execution and does not measure or accept benchmark performance.
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
from pathlib import Path

BASELINE = "4042e2685f33ac6db413b4109bcb1255c5b19f5b"
CANDIDATE = "ffea094441a4542a248c83e0bb656300943f1f73"
HEAD = "e3b1734e4af2aa57bce1dab5a310301a3c95a244"
FIXTURE_HASH = "be13b8d8e428fd243a26268dc30efdbb751ac84d824695bc97854ae94a6d6fb9"
DRIVER = Path("tools/Respire.CommandRetrySynchronization")
FIXTURE = Path("benchmarks/Respire.Benchmarks/CommandRetryBenchmarks.cs")
POLICIES = ("Default", "Disabled", "Enabled")


def verify_pins(baseline, candidate, parents, fixture):
    if (baseline, candidate, parents) != (BASELINE, CANDIDATE, [BASELINE, HEAD]):
        raise ValueError("Sources/merge parents differ from the original timing pair")
    if hashlib.sha256(fixture).hexdigest() != FIXTURE_HASH:
        raise ValueError("Fixture differs from the original-pair JIT exercise")


def validate_summary(summary):
    if (summary.get("EventsLost") != 0 or summary.get("operations") != 512
            or summary.get("complete") is not True or summary.get("taskEvents", 0) <= 0):
        raise ValueError("Incomplete task/operation evidence")


def validate_syscalls(text, exercise, redis_port=6379):
    # -ff puts the native TID in each filename; -ttt uses Unix seconds, matching
    # the per-operation unixMicroseconds markers in events.jsonl. Keep unfinished
    # and resumed records intact: durations overlap and must never be summed.
    starts = re.findall(r"SYNC_RANGE_START unixMicroseconds=(\d+) pid=(\d+)", exercise)
    stops = re.findall(r"SYNC_RANGE_STOP unixMicroseconds=(\d+) pid=(\d+)", exercise)
    if len(starts) != 1 or len(stops) != 1 or starts[0][1] != stops[0][1]:
        raise ValueError("Missing/ambiguous syscall range")
    start, stop = int(starts[0][0]) / 1e6, int(stops[0][0]) / 1e6
    if stop <= start:
        raise ValueError("Invalid syscall range")
    rows = []
    for line in text.splitlines():
        match = re.match(r"(\d+\.\d+)\s+(.+)", line)
        if match and start <= float(match[1]) <= stop:
            rows.append(match[2])
    def successful(names):
        # -yy identifies the TCP peer. Do not mistake EventPipe's Unix-domain
        # diagnostic socket for Redis traffic. A resumed record may omit the fd;
        # keep the initial call in raw evidence, but require a complete successful
        # Redis call here rather than accepting only EAGAIN or IPC traffic.
        # IPv6 endpoints have their own brackets inside the TCP peer brackets.
        # Match both endpoints explicitly so a Redis source port cannot satisfy
        # the destination-port check on traffic to a different service.
        address = r"(?:\[[^\]\s]+\]|[^\[\]<>\s:]+)"
        peer = rf"<TCP(?:v6)?:\[{address}:\d+->{address}:{redis_port}\]>"
        return any(re.match(rf"(?:{names})\(", row) and re.search(peer, row)
                   and re.search(r" = [1-9]\d*\b", row) for row in rows)

    if not successful("sendmsg|sendto|writev"):
        raise ValueError("No socket sends in the marked range")
    if not successful("recvmsg|recvfrom|readv"):
        raise ValueError("No socket receives in the marked range")
    return {"rangeStartUnixSeconds": start, "rangeStopUnixSeconds": stop,
            "recordsInRange": len(rows),
            "warning": "strace changes scheduling; syscalls can overlap or resume across range boundaries. "
                       "These are diagnostic records, not latency scores or kernel run-queue attribution."}


def run(workspace):
    infrastructure = workspace / "infrastructure"
    sources = {name: workspace / name for name in ("baseline", "candidate")}

    def git(source, *args):
        return subprocess.check_output(["git", "-C", str(sources[source]), *args], text=True).strip()

    fixture = (sources["candidate"] / FIXTURE).read_bytes()
    verify_pins(git("baseline", "rev-parse", "HEAD"), git("candidate", "rev-parse", "HEAD"),
                git("candidate", "show", "-s", "--format=%P", "HEAD").split(), fixture)
    output = workspace / "synchronization-results"
    output.mkdir(exist_ok=True)
    (output / "provenance.json").write_text(json.dumps({
        "baseline": BASELINE, "candidate": CANDIDATE, "head": HEAD,
        "infrastructure": subprocess.check_output(
            ["git", "-C", str(infrastructure), "rev-parse", "HEAD"], text=True).strip(),
        "fixtureSha256": FIXTURE_HASH, "framework": "net10.0", "operationsPerProcess": 512,
        "purpose": "Synchronization diagnostics only; no timing/allocation/performance acceptance",
    }, indent=2) + "\n")
    (output / "strace-version.txt").write_text(subprocess.check_output(["strace", "--version"], text=True))
    for name, source in sources.items():
        (source / FIXTURE).write_bytes(fixture)
        shutil.copytree(infrastructure / DRIVER, source / DRIVER,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        with (output / f"{name}-build.log").open("w") as log:
            subprocess.run(["dotnet", "--info"], cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run(["dotnet", "build", str(DRIVER), "-c", "Release", "-f", "net10.0"],
                           cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=240)
    manifests = []
    for phase in ("baseline-a", "candidate", "baseline-b"):
        source = sources["candidate" if phase == "candidate" else "baseline"]
        dll = source / DRIVER / "bin/Release/net10.0/Respire.CommandRetrySynchronization.dll"
        for policy in POLICIES:
            directory = output / phase / policy
            directory.mkdir(parents=True)
            with (directory / "exercise.log").open("w") as log:
                subprocess.run([
                    "strace", "-ff", "-ttt", "-T", "-yy", "-s", "128", "-o", str(directory / "syscalls"),
                    "-e", "trace=futex,epoll_wait,epoll_pwait,recvmsg,sendmsg,recvfrom,sendto,readv,writev",
                    "dotnet", str(dll), policy, str(directory),
                ], cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=120)
                subprocess.run(["dotnet", str(dll), "--summarize",
                                str(directory / "synchronization.nettrace"), str(directory)],
                               cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=120)
            summary = json.loads((directory / "summary.json").read_text())
            validate_summary(summary)
            syscalls = validate_syscalls("\n".join(path.read_text() for path in directory.glob("syscalls.*")),
                                         (directory / "exercise.log").read_text())
            (directory / "syscall-summary.json").write_text(json.dumps(syscalls, indent=2) + "\n")
            manifests.append({"phase": phase, "policy": policy, "summary": summary, "syscalls": syscalls})
    (output / "manifest.json").write_text(json.dumps(manifests, indent=2) + "\n")
    print("All nine original-pair synchronization exercises are complete; analyze raw evidence before attribution.")


if __name__ == "__main__":
    run(Path(os.environ["GITHUB_WORKSPACE"]).resolve())
