# Measuring value codec costs

The focused `ValueCodecBenchmarks` fixture compares an unchanged raw payload with
Brotli quality 4, Deflate Fastest, LZ4 level 0, and Zstandard level 3. It measures the destination overloads
of `IRespireValueCodec`, including frame validation, checksums, compression,
decompression, and the codecs' own scratch-buffer management.

Current runs use version 2 XxHash3 checksums by default. Historical results below used
version 1 SHA-256 checksums. `ValueCodecChecksumBenchmarks` compares both frame versions
through the same destination APIs with compression disabled, isolating the checksum
change while retaining framing, validation, and copying costs.

## Inputs and comparison

Every implementation receives identical already-serialized bytes. Serialization,
Redis command construction, sockets, server processing, and cache lookup are excluded
from every measured method. The disabled baseline copies the raw bytes into the same
kind of destination without framing or invoking a codec. This baseline isolates the
incremental transformation cost; it is not a complete client-operation benchmark.

The matrix contains 64-byte and 16 KiB values, each with either repeated ASCII record
text or pseudorandom bytes generated with seed 527. The source records a SHA-256 digest
of every input. Repeated text models high redundancy; random bytes model low redundancy,
not a universal distribution of application data. The two compression thresholds are
0 (try compression even for small values) and the default 1 KiB. Raw cases repeat across
threshold groups so each group has a measured baseline; raw behavior has no threshold.

Each direction has 40 cases: five implementations, two sizes, two patterns, and two
thresholds. Encode and decode run in separate jobs on .NET 8 and .NET 10. Codec options
keep the default 8 MiB decoded-size ceiling. All output capacity is allocated during
setup and reused with `ResetWrittenCount`, which does not clear the previous output.
This avoids charging raw copies for clearing more bytes than compressed copies.
Codecs still pay for clearing their own pooled scratch buffers.

Setup validates array/destination equivalence, exact decoded bytes, unchanged raw
output, and below-threshold framing before any timing. Decode inputs are prepared
outside timing. Methods return their written memory; BenchmarkDotNet controls repetition.
The MemoryDiagnoser reports managed bytes allocated per operation after warmup. Those
numbers exclude native compression workspace, peak memory, and the retained destination.
They do not describe the separately allocating array-returning codec APIs.

## Reproduce and inspect

The repository's **Value codec benchmarks** Actions workflow runs only this fixture.
Run it manually with `workflow_dispatch`, or add the `benchmark-value-codecs` label to
a pull request that changes the codec or benchmark paths. Full measurements are opt-in
because all four runtime/direction jobs can take up to 35 minutes each.
Each job first performs a Dry validation, then one representative Brotli case with the
Default job, then the complete directional matrix with two Default-job launches.
Warmup and measurement iteration counts remain BenchmarkDotNet's adaptive defaults;
the complete logs and JSON measurements retain the actual counts. A failed, missing,
duplicate, or incomplete measurement fails the job. The job summary is published only
after the complete matrix passes validation; partial diagnostic artifacts are still
uploaded on failure.

The source is `benchmarks/Respire.Benchmarks/ValueCodecBenchmarks.cs`; the workflow is
`.github/workflows/benchmark-value-codecs.yml`. A focused command is:

```sh
dotnet run --project benchmarks/Respire.Benchmarks -c Release -f net10.0 -- \
  --filter '*ValueCodecBenchmarks*' --allCategories Encode --job Default --launchCount 2 \
  --exporters json markdown --artifacts ./codec-results
```

Use `--job Dry --launchCount 1` first, and replace `Encode` with `Decode` for reads.
Do not run the full benchmark suite.

Each artifact retains the exact Git revision, SDK/runtime information, CPU description,
resolved packages, build log, all three benchmark phases, BDN reports, input digests,
and joined size/time/allocation tables. The generated table's MiB/s uses original bytes
divided by elapsed time. Its stored byte count includes the complete codec frame;
RESP bulk-string bytes additionally include `$length\r\n` and trailing `\r\n`.
Neither count includes command/key bytes, Redis object overhead, replication, or TLS.

## Measured trade-offs (2026-09-30)

[Run 36702369606](https://github.com/thomhurst/Respire/actions/runs/36702369606) measured revision
`cbe996aad493afc41357ee78e75380c06a602738`. All four jobs passed: 40 cases each,
two Default launches per case, preceded by Dry and representative validation. Jobs took
24m49s to 29m15s including build and reporting, within the 35-minute bound. The report
and workflow follow-ups do not change timed methods or codec implementations.

All jobs used BenchmarkDotNet 0.15.8, Ubuntu 24.04.5 and SDK 10.0.401. Runtime and CPU
differ by job; compare codecs within a job, not absolute encode/decode or runtime speed:

| Job | Runtime | CPU (2 physical / 4 logical cores exposed) |
| --- | --- | --- |
| .NET 8 encode and decode | 8.0.31 | AMD EPYC 9V45, 2.60 GHz |
| .NET 10 encode | 10.0.12 | AMD EPYC 7763, 2.45 GHz |
| .NET 10 decode | 10.0.12 | Intel Xeon Platinum 8573C, 2.30 GHz |

The following comparison uses **16 KiB repeated text, default 1 KiB threshold**.
Times are mean +/- standard deviation in microseconds. MiB/s uses original bytes;
managed B/op excludes retained output and native workspace. Stored/RESP sizes include
codec framing; RESP sizes additionally include the bulk-string envelope.

| Job | Codec | Mean +/- StdDev (us) | MiB/s | Managed B/op | Stored B | RESP bulk B |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| net8.0-Encode | Raw | 0.114 +/- 0.002 | 137562 | 0 | 16384 | 16394 |
| net8.0-Encode | Brotli | 9.024 +/- 0.205 | 1731 | 0 | 72 | 79 |
| net8.0-Encode | Deflate | 5.407 +/- 0.061 | 2890 | 760 | 198 | 206 |
| net8.0-Encode | Lz4 | 1.664 +/- 0.034 | 9389 | 0 | 142 | 150 |
| net8.0-Encode | Zstd | 3.479 +/- 0.079 | 4491 | 64 | 87 | 94 |
| net8.0-Decode | Raw | 0.115 +/- 0.002 | 136415 | 0 | 16384 | 16394 |
| net8.0-Decode | Brotli | 9.854 +/- 0.151 | 1586 | 32 | 72 | 79 |
| net8.0-Decode | Deflate | 3.129 +/- 0.033 | 4993 | 528 | 198 | 206 |
| net8.0-Decode | Lz4 | 1.722 +/- 0.017 | 9076 | 0 | 142 | 150 |
| net8.0-Decode | Zstd | 2.038 +/- 0.014 | 7667 | 56 | 87 | 94 |
| net10.0-Encode | Raw | 0.199 +/- 0.0002 | 78368 | 0 | 16384 | 16394 |
| net10.0-Encode | Brotli | 19.905 +/- 0.201 | 785 | 0 | 72 | 79 |
| net10.0-Encode | Deflate | 6.100 +/- 0.107 | 2561 | 816 | 259 | 267 |
| net10.0-Encode | Lz4 | 3.153 +/- 0.006 | 4955 | 0 | 142 | 150 |
| net10.0-Encode | Zstd | 6.356 +/- 0.298 | 2458 | 64 | 87 | 94 |
| net10.0-Decode | Raw | 0.082 +/- 0.001 | 189952 | 0 | 16384 | 16394 |
| net10.0-Decode | Brotli | 14.978 +/- 0.181 | 1043 | 32 | 72 | 79 |
| net10.0-Decode | Deflate | 2.277 +/- 0.010 | 6862 | 584 | 259 | 267 |
| net10.0-Decode | Lz4 | 2.431 +/- 0.008 | 6427 | 0 | 142 | 150 |
| net10.0-Decode | Zstd | 2.940 +/- 0.011 | 5315 | 56 | 87 | 94 |

Brotli stores the smallest repeated large frame here (72 B), while LZ4 has lower measured
encoding cost and 0 managed B/op in these cases. Zstandard uses 87 B and has per-call
managed context allocations. Deflate sizes differ between runtimes (198 B versus 259 B);
its implementation and runtime are part of the measured configuration, not a universal
format-size guarantee. All codecs add validation/checksum work relative to the raw copy.

For **16 KiB seeded random data**, every codec falls back to a 16,402 B frame (16,412 B
as a RESP bulk string), versus raw 16,384/16,394 B. Failed compression still costs CPU:

| Runtime | Codec | Encode mean (us) | Managed B/op |
| --- | --- | ---: | ---: |
| net8.0 | Raw | 0.114 | 0 |
| net8.0 | Brotli | 76.900 | 0 |
| net8.0 | Deflate | 71.668 | 74104 |
| net8.0 | Lz4 | 10.928 | 0 |
| net8.0 | Zstd | 16.862 | 64 |
| net10.0 | Raw | 0.267 | 0 |
| net10.0 | Brotli | 136.383 | 0 |
| net10.0 | Deflate | 121.818 | 75000 |
| net10.0 | Lz4 | 15.414 | 0 |
| net10.0 | Zstd | 26.386 | 64 |

The warmed destination API therefore does not imply allocation-free compression for
every codec: Deflate allocates roughly 74-75 KB/op on this low-redundancy input. The full
JSON retains variability and allocation measurements for every case.

For **64 B repeated text**, threshold 1 KiB avoids compression and emits an 82 B frame
(89 B RESP) for every codec, versus raw 64/71 B. Encoding takes 0.486-0.501 us on the
.NET 8 runner and 0.610-0.660 us on the .NET 10 encode runner, all with 0 managed B/op.
Lowering the threshold to zero gives the following additional work and sizes:

| Runtime | Codec | Encode mean (us) | Managed B/op | Stored B |
| --- | --- | ---: | ---: | ---: |
| net8.0 | Raw | 0.007 | 0 | 64 |
| net8.0 | Brotli | 5.198 | 0 | 69 |
| net8.0 | Deflate | 3.072 | 632 | 73 |
| net8.0 | Lz4 | 1.035 | 0 | 78 |
| net8.0 | Zstd | 1.179 | 64 | 82 |
| net10.0 | Raw | 0.006 | 0 | 64 |
| net10.0 | Brotli | 9.147 | 0 | 69 |
| net10.0 | Deflate | 4.354 | 624 | 72 |
| net10.0 | Lz4 | 1.739 | 0 | 78 |
| net10.0 | Zstd | 1.575 | 64 | 82 |

Even the smallest compressed 64 B frame remains larger than the original raw value.
The random 64 B input also yields 82 B codec frames. In these small cases the default
threshold avoids compression attempts and their allocations, while framing/checksum costs
remain. Threshold selection is a CPU/storage trade-off on actual payloads.

The [complete joined results](https://github.com/thomhurst/Respire/blob/main/docs/benchmarks/value-codecs-2026-09-30.json) include all sizes, thresholds, patterns,
means, standard deviations, allocations, payload hashes, and per-job environments.
The Actions artifacts retain full BDN observations, adaptive iteration counts, logs, packages and CPU metadata
for their configured retention period. Scheduling priority could not be elevated on
the shared runners. These single-run measurements are descriptive; they do not establish
end-to-end Redis throughput or a regression threshold.

## Interpreting the trade-off

Below-threshold values still pay framing and checksum costs and add 18 bytes. Lowering
the threshold only helps storage when compression saves more than the complete frame
overhead; encoding still consumes CPU. Low-redundancy values may pay a failed compression
attempt before falling back to an uncompressed frame. Decode costs also depend on whether
the stored frame is compressed, so compare the recorded algorithm and size with timing.

Measurements on a GitHub-hosted runner describe that revision, runtime, and synthetic
input. Runner contention and microbenchmark variation can affect timings. The raw baseline
in each parameter group provides context, not a throughput promise for Redis operations.
Evaluate real application payloads and concurrency before changing thresholds or quality.

For framing, ownership, serializer integration, and migration rules, see
[value codecs](./value-codecs.md). Parent feature work remains tracked in
[#425](https://github.com/thomhurst/Respire/issues/425).
