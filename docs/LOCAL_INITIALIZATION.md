# Local initialization (`SkipLocalsInit`) findings

Do not apply `SkipLocalsInit` at module, type or method scope. No Respire package suppresses local
initialization. `LocalInitializationAuditTests.ReviewedSkipLocalsInitScopes` enforces this and is
empty. Every `stackalloc` needs its own justification comment next to the allocation in `src/`.

This page records the evidence. Feature pull requests do not need to update it.

## Findings

- On the measured methods, the attribute removes at most one or two prologue stores per call.
  Nothing else changes deterministically.
- Timing across processes shows no benefit on either framework. Process-to-process spread within
  one build is far larger than any difference between builds.
- A narrower scope cannot help. `ResponseReader.StringOrNull` already zeroes no locals, and the JIT
  zeroes GC-tracked locals regardless of the attribute.
- Under Dynamic PGO, CoreLib UTF-8 callees differ between two runs of the same binary. Earlier
  listing differences fit that variation. These controls do not explain the earlier timing
  regressions; their cause is unresolved.

## Method

- Two builds of `src/Respire` from commit `6d666d82d`. The candidate added only
  `[module: System.Runtime.CompilerServices.SkipLocalsInit]`, confirmed by reflection.
- A console host named `Respire.Benchmarks` (using the existing `InternalsVisibleTo`) ran the
  `ProtocolBenchmarks` inputs through tier-up, calling methods through a delegate as
  BenchmarkDotNet does.
- Machine: Intel Core i7-12700K, Windows 11, runtimes 8.0.31 and 10.0.12.
- Listings: `DOTNET_JitDisasm` on the Respire methods and CoreLib callees
  (`String.CreateStringFromEncoding`, `UTF8Encoding.GetChars`, `Utf8Utility.TranscodeToUtf16`,
  `Utf8Utility.GetPointerToFirstInvalidByte`, `Ascii.WidenAsciiToUtf16`). Final Tier1 listings,
  labels and addresses normalized, captured in separate runs from timing.
- Controls: three PGO-on listing runs per variant plus one with `DOTNET_TieredPGO=0`. Timing: 10
  PGO-on and 6 PGO-off processes per variant, interleaved, each reporting a median of 15 samples.

On Windows, write a `DOTNET_JitDisasm` class as `*Class:Method` (or fully qualified) and set the
variable from PowerShell or cmd. Git Bash rewrites values with colons as path lists, and the JIT
prints nothing.

## Listings

### Dynamic PGO off: the attribute's own effect

Code size is baseline bytes / candidate bytes. "diff" counts differing instructions after
normalization.

| Method | net8.0 | net8.0 diff | net10.0 | net10.0 diff |
| --- | --- | --- | --- | --- |
| `ResponseReader.StringOrNull` | 15 / 15 | 0 | 15 / 15 | 0 |
| `Utf8String.GetString(ReadOnlyMemory<byte>)` | 690 / 690 | 0 | 678 / 678 | 0 |
| `Utf8String.GetString(ReadOnlySpan<byte>)` | 452 / 446 | 6 | 416 / 416 | 0 |
| `RespValue.AsString` | 319 / 314 | 1 | 282 / 277 | 1 |
| `RespValue.ToString` | 1,182 / 1,182 | 0 | 1,209 / 1,209 | 0 |
| All five CoreLib callees | identical | 0 | identical | 0 |

- `RespValue.AsString` drops one `mov qword ptr [rsp+...], rax` zeroing store on both frameworks.
- On net8.0, `Utf8String.GetString(ReadOnlySpan<byte>)` zeroes 24 bytes of frame instead of 40.

### Dynamic PGO on: run-to-run control

Differing instructions between pairs of runs. "Same build" compares two runs of one binary;
"Attribute" compares baseline and candidate runs from the same round.

| Method | Framework | Same build (baseline) | Same build (candidate) | Attribute (round 2) | Attribute (round 3) |
| --- | --- | --- | --- | --- | --- |
| `Utf8Utility.TranscodeToUtf16` | net8.0 | 499, 287 | 237 | 3 | 508 |
| `RespValue.ToString` | net8.0 | 377, 1 | 115 | 374 | 0 |
| `RespValue.AsString` | net8.0 | 1, 103 | 2 | 2 | 102 |
| `String.CreateStringFromEncoding` | net8.0 | 0, 32 | 0 | 0 | 32 |
| `Utf8Utility.TranscodeToUtf16` | net10.0 | 157, 94 | 94 | 0 | 0 |
| `Ascii.WidenAsciiToUtf16` | net10.0 | 39, 0 | 0 | 0 | 0 |
| `Utf8String.GetString(ReadOnlySpan<byte>)` | net10.0 | 52, 0 | 0 | 0 | 0 |

Baseline-to-candidate differences fall inside the same-build range. A single BenchmarkDotNet
process samples one layout; listing variation alone does not quantify its timing effect.

## Timing

Median ns/op, with the min–max across processes in brackets. Each cell is baseline / candidate.

| Row | Framework | PGO on | PGO off |
| --- | --- | --- | --- |
| `ReadUnicodeBulkString` | net8.0 | 16.87 [16.25–17.86] / 17.01 [15.58–18.50] | 21.51 [21.31–21.93] / 22.32 [21.60–22.70] |
| `DecodeUnicodeBulkString` | net8.0 | 16.26 [16.05–16.81] / 16.85 [15.95–18.86] | 22.18 [21.74–22.92] / 21.96 [21.63–22.13] |
| `FormatLargeUnicodeBulkString` | net8.0 | 356.4 [350.4–426.3] / 356.7 [344.8–418.5] | 378.6 [366.4–392.4] / 380.6 [371.8–426.7] |
| `FormatLargeAsciiBulkString` | net8.0 | 359.1 [350.7–429.0] / 352.4 [342.4–500.7] | 379.8 [370.8–392.4] / 419.0 [363.4–454.8] |
| `ReadUnicodeBulkString` | net10.0 | 14.13 [13.87–15.42] / 14.56 [13.79–16.81] | 19.90 [19.71–20.26] / 20.38 [20.00–23.52] |
| `DecodeUnicodeBulkString` | net10.0 | 14.10 [13.84–14.49] / 14.29 [13.91–15.12] | 20.22 [19.28–21.71] / 20.02 [19.74–20.48] |
| `FormatLargeUnicodeBulkString` | net10.0 | 378.2 [360.5–506.7] / 406.2 [361.7–427.9] | 374.5 [369.0–421.7] / 400.2 [367.6–482.9] |
| `FormatLargeAsciiBulkString` | net10.0 | 307.6 [297.3–375.1] / 343.0 [296.8–375.0] | 317.4 [311.1–366.3] / 315.9 [304.0–333.1] |

- The candidate's minimum never beats the baseline's minimum by more than the baseline's own noise.
- On the large rows, one build's processes range over 20 to 40 percent.
- The Linux probe run of the comparison workflow also showed no consistent improvement: with PGO on,
  all gates passed (candidate/control ratios 0.912 to 1.025); with PGO off, the report-only leg
  flagged four rows between 1.019 and 1.127.

## Reopening

A proposal needs a method whose removable zeroing is large compared with its work, such as a large
`stackalloc` buffer or a big non-GC struct local that is always written before it is read. None of
the audited `stackalloc` sites qualify. To propose one:

1. Add the scope to `ReviewedSkipLocalsInitScopes`.
2. Extend `benchmark-local-initialization.yml` with benchmarks covering every method in the scope.
3. Show a benefit larger than the process-to-process spread above, across several processes.
