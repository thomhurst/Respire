# Local initialization (`SkipLocalsInit`) findings

This page records the code-generation evidence behind #1065. It explains why no Respire package
suppresses local initialization. It is a record of findings, not a policy or status list, and
feature pull requests do not need to update it. The enforced policy is
`LocalInitializationAuditTests.ReviewedSkipLocalsInitScopes`, which is empty. The per-site
`stackalloc` justifications sit next to each allocation in `src/`.

## Conclusion

Do not apply `SkipLocalsInit`, at module, type or method scope, to the measured paths.

- On the methods that regressed in #1076, the attribute removes one or two prologue stores per
  call. That is the most it can save.
- No other instruction in those methods changes deterministically. The CoreLib UTF-8 callees
  that differed in #1076 also differ between two runs of the same unchanged binary under
  Dynamic PGO. The #1076 listing differences are consistent with that run-to-run variation,
  but these controls do not establish the cause of the earlier timing regressions.
- Timing across separate processes shows no benefit on either framework. The spread between
  processes of the same build is much larger than any difference between the builds.

A targeted method-level scope has nothing to target. `ResponseReader.StringOrNull` already
compiles with no locals to zero. With the attribute, `Utf8String.GetString` and
`RespValue.AsString`/`ToString` keep every prologue store but one or two. The JIT still zeroes
GC-tracked locals whatever the attribute says, so a narrower scope cannot remove more than the
module attribute does.

## Method

The probe built `src/Respire` twice from the same commit (`6d666d82d`). The candidate added only
`[module: System.Runtime.CompilerServices.SkipLocalsInit]`. Reflection confirmed that only the
candidate's module carries the attribute. A console host named `Respire.Benchmarks`, which
reaches the internals through the existing `InternalsVisibleTo`, ran the `ProtocolBenchmarks`
inputs through tier-up. It called the methods through a delegate, as BenchmarkDotNet does.

- Machine: Intel Core i7-12700K, Windows 11, runtimes 8.0.31 and 10.0.12.
- Listings: `DOTNET_JitDisasm` on the Respire methods and their CoreLib callees
  (`String.CreateStringFromEncoding`, `UTF8Encoding.GetChars`, `Utf8Utility.TranscodeToUtf16`,
  `Utf8Utility.GetPointerToFirstInvalidByte`, `Ascii.WidenAsciiToUtf16`). Comparisons use each
  method's final Tier1 listing, with labels and embedded addresses normalized. The listings
  were captured in separate runs, never during timing.
- Controls: three PGO-on listing runs per variant, plus one run with `DOTNET_TieredPGO=0`.
  Timing used 10 PGO-on processes and 6 PGO-off processes per variant, interleaved. Each
  process reports a median of 15 samples per row.

On Windows, a class in `DOTNET_JitDisasm` must be written as `*Class:Method` (or with its full
namespace), and the variable must be set from PowerShell or cmd. Git Bash rewrites values that
contain colons as path lists, and the JIT then prints nothing.

## Listings

### With Dynamic PGO off: the attribute's own effect

The code-size column shows baseline bytes, then candidate bytes. The last column counts
instructions that differ after normalization.

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
  One 8-byte and two 16-byte stores become one 16-byte and one 8-byte store.
- Nothing else changes.

### With Dynamic PGO on: run-to-run control

The table counts instructions that differ between pairs of runs. "Same build" compares two runs
of one unchanged binary. "Attribute" compares a baseline run with a candidate run captured in
the same round.

| Method | Framework | Same build (baseline) | Same build (candidate) | Attribute (round 2) | Attribute (round 3) |
| --- | --- | --- | --- | --- | --- |
| `Utf8Utility.TranscodeToUtf16` | net8.0 | 499, 287 | 237 | 3 | 508 |
| `RespValue.ToString` | net8.0 | 377, 1 | 115 | 374 | 0 |
| `RespValue.AsString` | net8.0 | 1, 103 | 2 | 2 | 102 |
| `String.CreateStringFromEncoding` | net8.0 | 0, 32 | 0 | 0 | 32 |
| `Utf8Utility.TranscodeToUtf16` | net10.0 | 157, 94 | 94 | 0 | 0 |
| `Ascii.WidenAsciiToUtf16` | net10.0 | 39, 0 | 0 | 0 | 0 |
| `Utf8String.GetString(ReadOnlySpan<byte>)` | net10.0 | 52, 0 | 0 | 0 | 0 |

Under Dynamic PGO, the same binary produces different Tier1 layouts for the library methods and
the CoreLib transcoders from one process to the next. The baseline-to-candidate differences fall
inside that range, and some baseline/candidate pairs are identical. The #1076 listings showed
`TranscodeToUtf16` hot/cold moves and the net10.0 `WidenAsciiToUtf16` tail moving below `ret`.
Similar moves occur in these unchanged-binary controls. A single BenchmarkDotNet process per
measurement samples one layout; listing variation alone does not quantify its timing effect.

## Timing

Median ns/op, with the minimum and maximum across processes in brackets. Each cell is baseline,
then candidate.

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

The candidate's minimum never beats the baseline's minimum by more than the noise in the
baseline's own range. On the large rows, one build's processes range over 20 to 40 percent.
For net10.0 `FormatLargeUnicodeBulkString`, that row's spread exceeds the +10 percent
regression reported in #1076. For net8.0 `ReadUnicodeBulkString`, the widest same-build
range is 15.58–18.50 ns with PGO on (about 19 percent), or 21.60–22.70 ns with PGO off
(about 5 percent). Neither range covers its earlier +35 percent regression. These measurements
therefore do not explain that regression; its cause remains unresolved. Variation in other
rows cannot establish the cause of a slowdown in this row.

The Linux probe also produced different results with PGO on and off in
[run 38278398897](https://github.com/thomhurst/Respire/actions/runs/38278398897), the probe run
of the merged comparison workflow on Linux. With PGO on, all of that run's gates passed, with
candidate/control ratios from 0.912 to 1.025. With PGO off, its report-only leg flagged four
rows between 1.019 and 1.127. No pair of rows showed a consistent improvement.

## Reopening

A future proposal needs a method whose removable zeroing is large compared with its work. An
example is a large `stackalloc` buffer or a big non-GC struct local that is always overwritten
before it is read. The 39 audited sites are not such cases. A proposal adds its scope to
`ReviewedSkipLocalsInitScopes` and extends `benchmark-local-initialization.yml` with
benchmarks that cover every method in the scope. It must show a benefit larger than the
process-to-process spread above, measured across several processes.
