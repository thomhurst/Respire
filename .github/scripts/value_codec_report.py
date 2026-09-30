"""Validate focused BDN results and join measured costs to checked payload sizes."""

import argparse
import json
import math
from pathlib import Path
import re


def parameters(value):
    fields = dict(re.findall(r"(Length|Pattern|MinimumLength)=([^&,]+)", value))
    return (int(fields["Length"]), fields["Pattern"].strip(), int(fields["MinimumLength"]))


def generate(root, phase, direction):
    reports = list((root / phase / "results").glob("*-report-full-compressed.json"))
    if len(reports) != 1:
        raise ValueError(f"Expected one complete BDN JSON report for {phase}, got {len(reports)}")
    metadata = {}
    for line in (root / f"{phase}.log").read_text(encoding="utf-8").splitlines():
        if not line.startswith("VALUE_CODEC_SIZE "):
            continue
        item = json.loads(line.removeprefix("VALUE_CODEC_SIZE "))
        key = (item["Length"], item["Pattern"], item["MinimumLength"], item["Codec"])
        if key in metadata and metadata[key] != item:
            raise ValueError(f"Payload metadata changed across launches: {key}")
        metadata[key] = item

    # Setup emits every codec for each input independently of which method is timed.
    # Missing methods therefore remain detectable without a second parameter matrix.
    expected = set(metadata)
    if phase == "representative":
        expected = {(16384, "RepeatedText", 1024, "Brotli")}
    rows = {}
    representative_filter = None
    for case in json.loads(reports[0].read_text(encoding="utf-8"))["Benchmarks"]:
        method = case["Method"]
        if not method.endswith(direction):
            raise ValueError(f"Unexpected operation in {direction} results: {method}")
        key = (*parameters(case["Parameters"]), method.removesuffix(direction))
        if key in rows:
            raise ValueError(f"Duplicate benchmark case: {key}")
        statistics = case.get("Statistics")
        if not statistics or not math.isfinite(statistics["Mean"]) or statistics["Mean"] <= 0:
            raise ValueError(f"Benchmark did not produce a valid measurement: {key}")
        allocated = case["Memory"]["BytesAllocatedPerOperation"]
        if allocated is None or not math.isfinite(allocated) or allocated < 0:
            raise ValueError(f"Missing allocation measurement: {key}")
        raw_key = (*key[:3], "Raw")
        for required in (key, raw_key):
            if required not in metadata:
                raise ValueError(f"Missing size metadata for {phase}: {required}")
        item = metadata[key]
        raw = metadata[raw_key]
        if raw["EncodedBytes"] != key[0] or item["PayloadSha256"] != raw["PayloadSha256"]:
            raise ValueError(f"Codec and disabled baseline used different payloads: {key}")
        rows[key] = dict(item, Direction=direction, MeanNanoseconds=statistics["Mean"],
                         StandardDeviationNanoseconds=statistics.get("StandardDeviation"),
                         AllocatedBytes=allocated)
        if key == (16384, "RepeatedText", 1024, "Brotli"):
            representative_filter = case["FullName"]
    if phase != "representative" and len(expected) != 40:
        raise ValueError(f"Expected metadata for 40 cases in {phase}, got {len(expected)}")
    if rows.keys() != expected:
        raise ValueError(f"Incomplete benchmark matrix: missing={expected - rows.keys()}, extra={rows.keys() - expected}")
    if phase == "validation":
        (root / "representative-filter.txt").write_text(representative_filter + "\n", encoding="utf-8")

    lines = [f"## Value codec {direction.lower()} — {phase}", "",
             "Destination API; warmed reusable output. Serializer, RESP command framing, and network I/O are excluded.",
             "MiB/s uses original payload bytes. Managed allocations exclude native compression workspace.", "",
             "| Payload B | Pattern | Threshold B | Codec | Mean ns | StdDev ns | MiB/s | Allocated B/op | Stored B | RESP bulk B |",
             "| ---: | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for key, row in sorted(rows.items()):
        mean = row["MeanNanoseconds"]
        deviation = row["StandardDeviationNanoseconds"]
        deviation_text = "n/a" if deviation is None else f"{deviation:.2f}"
        lines.append(f"| {key[0]} | {key[1]} | {key[2]} | {key[3]} | {mean:.2f} | {deviation_text} | "
                     f"{key[0] * 1e9 / mean / 1048576:.2f} | {row['AllocatedBytes']:g} | "
                     f"{row['EncodedBytes']} | {row['RespBulkStringBytes']} |")
    (root / f"{phase}-summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (root / f"{phase}-summary.json").write_text(json.dumps(list(rows.values()), indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--phase", choices=("validation", "representative", "measurement"), required=True)
    parser.add_argument("--direction", choices=("Encode", "Decode"), required=True)
    args = parser.parse_args()
    generate(args.root, args.phase, args.direction)
