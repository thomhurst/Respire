---
title: Measuring value codec costs
description: How the value codec benchmarks work, how to reproduce them, and recorded results.
---

# Measuring value codec costs

`ValueCodecBenchmarks` measures what each [value codec](./value-codecs.md) adds over a plain copy:
framing, checksums, compression, decompression, and scratch-buffer management. Use it to choose a
codec and threshold, not to predict Redis throughput.

It compares a raw copy with Brotli quality 4, Deflate Fastest, LZ4 level 0, and Zstandard level 3,
through the `IRespireValueCodec` destination overloads. Current runs use version 2 (XxHash3)
checksums. `ValueCodecChecksumBenchmarks` compares frame versions 1 and 2 with compression disabled,
isolating the checksum cost.

## Inputs and comparison

- Every codec receives identical already-serialized bytes. Serialization, command construction,
  sockets, Redis, and cache lookup are excluded.
- The raw baseline copies the bytes into the same kind of destination without framing.
- The matrix covers 64 B, 1 KiB, and 16 KiB values of repeated ASCII record text (high redundancy)
  or pseudorandom bytes from seed 527 (low redundancy). The source records a SHA-256 digest of every
  input.
- Thresholds are 0 (always try compression) and the default 1 KiB. Raw cases repeat in each
  threshold group as a baseline.
- Each direction has 60 cases (5 implementations × 3 sizes × 2 patterns × 2 thresholds). Encode and
  decode run as separate .NET 10 jobs with the default 8 MiB decoded-size ceiling.
- Output capacity is allocated at setup and reused with `ResetWrittenCount`, which does not clear
  it. Codecs still pay to clear their own scratch buffers.
- Setup checks array/destination equivalence, exact decoded bytes, unchanged raw output, and
  below-threshold framing before timing.
- MemoryDiagnoser reports managed bytes per operation after warmup. It excludes native compression
  workspace, peak memory, the retained destination, and the array-returning APIs.

## Reproduce and inspect

The **Value codec benchmarks** workflow (`.github/workflows/benchmark-value-codecs.yml`) runs only
this fixture. Start it with `workflow_dispatch` or by adding the `benchmark-value-codecs` label to a
pull request. Each direction job can take up to 50 minutes. A job runs a Dry validation, one
representative Brotli case, then the full matrix with two launches. It fails on any missing,
duplicate, or incomplete measurement.

To run locally (source: `benchmarks/Respire.Benchmarks/ValueCodecBenchmarks.cs`):

```sh
dotnet run --project benchmarks/Respire.Benchmarks -c Release -f net10.0 -- \
  --filter '*ValueCodecBenchmarks*' --allCategories Encode --job Default --launchCount 2 \
  --exporters json markdown --artifacts ./codec-results
```

Use `--job Dry --launchCount 1` first, and replace `Encode` with `Decode` for reads. Do not run the
full benchmark suite.

Artifacts keep the Git revision, SDK/runtime, CPU, packages, logs, BDN reports, input digests, and
joined size/time/allocation tables. In those tables:

- MiB/s is original bytes divided by elapsed time.
- Stored bytes include the codec frame. RESP bulk bytes add `$length\r\n` and the trailing `\r\n`.
- Neither includes command/key bytes, Redis object overhead, replication, or TLS.

## Measured trade-offs (2026-09-30)

These results predate Zstandard context pooling, the 1 KiB cases, direct-to-destination DEFLATE,
and the .NET 10-only workflow. They describe the recorded revision; DEFLATE allocations in
particular may differ in current code.

[Run 36702369606](https://github.com/thomhurst/Respire/actions/runs/36702369606) measured revision
`cbe996aad493afc41357ee78e75380c06a602738`: 40 cases per job, two Default launches each. All jobs used
BenchmarkDotNet 0.15.8, Ubuntu 24.04.5 and SDK 10.0.401. Runtime and CPU differ by job, so compare
codecs within a job only:

| Job | Runtime | CPU (2 physical / 4 logical cores exposed) |
| --- | --- | --- |
| .NET 8 encode and decode | 8.0.31 | AMD EPYC 9V45, 2.60 GHz |
| .NET 10 encode | 10.0.12 | AMD EPYC 7763, 2.45 GHz |
| .NET 10 decode | 10.0.12 | Intel Xeon Platinum 8573C, 2.30 GHz |

**16 KiB repeated text, default 1 KiB threshold.** Times are mean +/- standard deviation in
microseconds.

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

- Brotli stores the smallest frame (72 B).
- LZ4 encodes fastest with 0 managed B/op.
- Zstandard stores 87 B with small per-call managed allocations.
- Deflate sizes differ by runtime (198 B versus 259 B), so its size is not a format guarantee.

**16 KiB seeded random data.** Every codec falls back to a 16,402 B frame (16,412 B RESP), versus
raw 16,384/16,394 B. The failed compression still costs CPU:

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

The destination API is not allocation-free for every codec: Deflate allocates about 74-75 KB/op
on this input.

**64 B repeated text.** With the 1 KiB threshold, every codec skips compression and emits an 82 B
frame (89 B RESP), versus raw 64/71 B. Encoding takes 0.486-0.501 us on .NET 8 and 0.610-0.660 us
on .NET 10, with 0 managed B/op. With threshold 0:

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

Even the smallest compressed 64 B frame is larger than the raw value. Random 64 B input also yields
82 B frames.

The [complete joined results](https://github.com/thomhurst/Respire/blob/main/docs/benchmarks/value-codecs-2026-09-30.json)
include every case with means, deviations, allocations, payload hashes, and environments. These are
single-run, descriptive measurements; they do not establish Redis throughput or a regression
threshold.

## Interpreting the trade-off

- Below-threshold values still pay framing and checksum costs and add 18 bytes.
- A lower threshold saves storage only when compression saves more than the frame overhead, and it
  always costs CPU.
- Low-redundancy values may pay for a failed compression attempt.
- Decode cost depends on whether the stored frame is compressed, so compare algorithm and size with
  timing.
- Runner contention affects timings. Test your real payloads and concurrency before changing the
  threshold or level.
