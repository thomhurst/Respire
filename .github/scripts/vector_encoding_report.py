"""Validate each vector benchmark phase before starting the next one."""

import argparse
import itertools
import json
import math
from pathlib import Path


def validate(root, phase):
    reports = list((root / "results" / phase / "results").glob("*-report*.json"))
    if len(reports) != 1:
        raise ValueError(f"Expected one focused report: {phase}")
    expected = set(itertools.product(("DirectFp32", "IntermediateArrayFp32", "Values"), ("Dimensions=16", "Dimensions=1536")))
    seen = set()
    for case in json.loads(reports[0].read_text(encoding="utf-8"))["Benchmarks"]:
        key = (case["Method"], case["Parameters"])
        if key in seen or key not in expected:
            raise ValueError(f"Duplicate or unexpected case: {phase}/{key}")
        seen.add(key)
        statistics = case.get("Statistics")
        if not statistics or not math.isfinite(statistics["Mean"]) or statistics["Mean"] <= 0:
            raise ValueError(f"Failed case: {phase}/{key}")
        allocated = (case.get("Memory") or {}).get("BytesAllocatedPerOperation")
        if allocated is None or not math.isfinite(allocated) or allocated < 0:
            raise ValueError(f"Missing allocation measurement: {phase}/{key}")
    if seen != expected:
        raise ValueError(f"Incomplete matrix: {phase}")
    sizes = [json.loads(line.removeprefix("VECTOR_ENCODING_SIZE "))
             for line in (root / f"{phase}.log").read_text(encoding="utf-8").splitlines()
             if line.startswith("VECTOR_ENCODING_SIZE ")]
    distinct = sorted({(s["Dimensions"], s["Fp32Bytes"], s["ValuesBytes"]) for s in sizes})
    if len(distinct) != 2 or {s[0] for s in distinct} != {16, 1536} or any(min(s[1:]) <= 0 for s in distinct):
        raise ValueError(f"Missing or inconsistent encoded sizes: {phase}")
    evidence = root / "validated-vector-sizes.json"
    if phase == "validation":
        evidence.write_text(json.dumps(distinct), encoding="utf-8")
    elif json.loads(evidence.read_text(encoding="utf-8")) != [list(s) for s in distinct]:
        raise ValueError(f"Encoded sizes changed: {phase}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--phase", choices=("validation", "baseline-a", "candidate", "baseline-b"), required=True)
    args = parser.parse_args()
    validate(args.root, args.phase)
