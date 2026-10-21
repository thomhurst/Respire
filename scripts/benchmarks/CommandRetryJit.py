"""Issue #2065: collect code generation for the original timing pair, not new timings.

Runtime switches: https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/viewing-jit-dumps.md
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
FAMILIES = ("ExecuteScriptCoreAsync", "ExecuteScriptOnConnectionCoreAsync")
POLICIES = ("Default", "Disabled", "Enabled")
DRIVER = Path("tools/Respire.CommandRetryJit")
FIXTURE = Path("benchmarks/Respire.Benchmarks/CommandRetryBenchmarks.cs")


def verify_pins(baseline, candidate, parents):
    if (baseline, candidate, parents) != (BASELINE, CANDIDATE, [BASELINE, HEAD]):
        raise ValueError("Sources must match the original #2065 timing pair and merge parents")


def validate_dump(text, mode):
    if mode not in ("tiered", "fullopts"):
        raise ValueError(f"Unknown JIT mode: {mode}")
    blocks = re.split(r"(?m)^; Assembly listing for method ", text)[1:]
    methods = []
    for block in blocks:
        sizes = re.findall(r"(?m)^; Total bytes of code (\d+)", block)
        if len(sizes) != 1:
            raise ValueError("Incomplete or interleaved assembly listing")
        methods.append({"method": block.splitlines()[0], "bytes": int(sizes[0])})
    for family in FAMILIES:
        matching = [m for m in methods if family in m["method"] and ":MoveNext(" in m["method"]]
        if not matching:
            raise ValueError(f"Missing script state machine: {family}")
        required = "Tier1" if mode == "tiered" else "FullOpts"
        if not any(required in m["method"] for m in matching):
            raise ValueError(f"Missing {required} script state machine: {family}")
    return methods


def run(workspace):
    infrastructure = workspace / "infrastructure"
    sources = {name: workspace / name for name in ("baseline", "candidate")}

    def git(source, *args):
        return subprocess.check_output(["git", "-C", str(sources[source]), *args], text=True).strip()

    verify_pins(git("baseline", "rev-parse", "HEAD"), git("candidate", "rev-parse", "HEAD"),
                git("candidate", "show", "-s", "--format=%P", "HEAD").split())
    output = workspace / "jit-results"
    output.mkdir(exist_ok=True)
    fixture = (sources["candidate"] / FIXTURE).read_bytes()
    (sources["baseline"] / FIXTURE).write_bytes(fixture)
    provenance = {"baseline": BASELINE, "candidate": CANDIDATE, "head": HEAD,
                  "infrastructure": subprocess.check_output(
                      ["git", "-C", str(infrastructure), "rev-parse", "HEAD"], text=True).strip(),
                  "fixtureSha256": hashlib.sha256(fixture).hexdigest(),
                  "framework": "net10.0", "operationsPerProcess": 16384,
                  "purpose": "JIT diagnostics only; no timing or performance acceptance"}
    (output / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n")
    for name, source in sources.items():
        shutil.copytree(infrastructure / DRIVER, source / DRIVER,
                        ignore=shutil.ignore_patterns("bin", "obj"))
        with (output / f"{name}-build.log").open("w") as log:
            subprocess.run(["dotnet", "--info"], cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
            subprocess.run(["dotnet", "build", str(DRIVER), "-c", "Release", "-f", "net10.0"],
                           cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)

    manifests = []
    for mode in ("tiered", "fullopts"):
        for phase in ("baseline-a", "candidate", "baseline-b"):
            source = sources["candidate" if phase == "candidate" else "baseline"]
            for policy in POLICIES:
                directory = output / mode / phase / policy
                directory.mkdir(parents=True)
                environment = dict(os.environ, DOTNET_TieredCompilation="1" if mode == "tiered" else "0",
                                   # Qualify both wrappers and compiler-generated script types.
                                   # JitDisasmAssemblies is unavailable in release runtimes;
                                   # broad MoveNext matching also interleaves unrelated JIT output.
                                   DOTNET_JitDisasm="Respire!*:*ExecuteScript* Respire!*ExecuteScript*:*",
                                   DOTNET_JitDisasmDiffable="1",
                                   DOTNET_JitStdOutFile=str(directory / "jit.asm"))
                with (directory / "exercise.log").open("w") as log:
                    subprocess.run(["dotnet", str(DRIVER / "bin/Release/net10.0/Respire.CommandRetryJit.dll"), policy],
                                   cwd=source, env=environment, stdout=log, stderr=subprocess.STDOUT, check=True)
                completion = f"JIT_EXERCISE_COMPLETE policy={policy} operations=16384"
                if completion not in (directory / "exercise.log").read_text():
                    raise ValueError(f"Incomplete exercise: {mode}/{phase}/{policy}")
                methods = validate_dump((directory / "jit.asm").read_text(), mode)
                manifest = {"mode": mode, "phase": phase, "policy": policy, "methods": methods}
                (directory / "methods.json").write_text(json.dumps(manifest, indent=2) + "\n")
                manifests.append(manifest)
    (output / "manifest.json").write_text(json.dumps(manifests, indent=2) + "\n")
    print("All 18 JIT exercises and required optimized script state machines are complete.")


if __name__ == "__main__":
    run(Path(os.environ["GITHUB_WORKSPACE"]).resolve())
