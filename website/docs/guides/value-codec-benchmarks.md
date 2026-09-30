# Measuring value codec costs

The focused `ValueCodecBenchmarks` fixture compares an unchanged raw payload with
Brotli quality 4, Deflate Fastest, and LZ4 level 0. It measures the destination overloads
of `IRespireValueCodec`, including frame validation, SHA-256 checksums, compression,
decompression, and the codecs' own scratch-buffer management.

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

Each direction has 32 cases: four implementations, two sizes, two patterns, and two
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
Each job first performs a Dry validation, then one representative Brotli case with the
Default job, then the complete directional matrix with two Default-job launches.
Warmup and measurement iteration counts remain BenchmarkDotNet's adaptive defaults;
the complete logs and JSON measurements retain the actual counts. A failed, missing,
duplicate, or incomplete measurement fails the workflow rather than publishing a
partial successful comparison.

The source is `benchmarks/Respire.Benchmarks/ValueCodecBenchmarks.cs`; the workflow is
`.github/workflows/benchmark-value-codecs.yml`. A focused command is:

```sh
dotnet run --project benchmarks/Respire.Benchmarks -c Release -f net10.0 -- \
  --filter '*ValueCodecBenchmarks*' --allCategories Encode --job Default --launchCount 2 \
  --exporters json markdown --artifacts ./codec-results
```

Use `--job Dry --launchCount 1` first, and replace `Encode` with `Decode` for reads.
Repository agents run measurements through CI and follow the shared performance-lock
workflow before any local build or diagnostic run. Do not run the full benchmark suite.

Each artifact retains the exact Git revision, SDK/runtime information, CPU description,
resolved packages, build log, all three benchmark phases, BDN reports, input digests,
and joined size/time/allocation tables. The generated table's MiB/s uses original bytes
divided by elapsed time. Its stored byte count includes the complete codec frame;
RESP bulk-string bytes additionally include `$length\r\n` and trailing `\r\n`.
Neither count includes command/key bytes, Redis object overhead, replication, or TLS.

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
The Zstandard package is a separate pending delivery and is not part of this initial matrix.

For framing, ownership, serializer integration, and migration rules, see
[value codecs](./value-codecs.md). Parent feature work remains tracked in
[#425](https://github.com/thomhurst/Respire/issues/425).
