# Local initialization (`SkipLocalsInit`)

No Respire package suppresses local initialization. This page records why, how every stack
allocation is audited, and how to measure a future proposal. It is the reference for #1065.

## Current policy

The policy is empty. No package assembly carries `SkipLocalsInitAttribute` on its module, a
type or a method. `LocalInitializationAuditTests.ReviewedSkipLocalsInitScopes` holds the
reviewed scopes, and the compiled-assembly test fails on any attribute it does not list. It
also fails when a method that executes `localloc` has local initialization turned off.

A scope can be added only with:

1. a justification in the audit for every stack allocation it covers (see below),
2. a run of the measurement workflow that passes its gate, and
3. a JIT listing that shows which initialization work the scope removes.

## Why the package-wide attribute was withdrawn (#1076)

PR #1076 applied `[module: SkipLocalsInit]` to every package. Two attribute-only comparisons
on GitHub's Linux runners (AMD EPYC 7763) failed the gate. In
[run 37438429684](https://github.com/thomhurst/Respire/actions/runs/37438429684):

| Row | Framework | Controls (ns) | Candidate (ns) | Allocation |
| --- | --- | --- | --- | --- |
| `ReadUnicodeBulkString` | net8.0 | 31.780 / 31.788 | 42.909 (+35%) | 48 B, unchanged |
| `FormatLargeUnicodeBulkString` | net10.0 | 600.938 / 602.982 | 660.750 (+10%) | 8,216 B, unchanged |

### What the attribute changed in code generation

The attribute affects only methods with locals that the JIT must zero in the prologue. JIT
listings of the regressing paths, with and without the attribute, show:

- **Library methods lost only prologue zeroing.** On .NET 8, `Utf8String.GetString(ReadOnlyMemory<byte>)`
  went from 899 to 888 bytes. Three 16-byte zeroing stores (`vmovdqa xmmword ptr [rbp-0x80/-0x70/-0x60]`)
  became one 8-byte store. `RespValue.AsString` lost the same three stores (1,065 to 1,054 bytes).
  On .NET 10, `Utf8String.GetString(ReadOnlySpan<byte>)` (606 to 596 bytes) and
  `RespValue.ToString` (1,645 to 1,637 bytes) lost one 32-byte `vmovdqu ymmword` store each.
  After normalizing branch labels, every other instruction matched.
- **No decoder work was added.** `ResponseReader.StringOrNull` was identical (15 bytes) and
  already had `InitLocals=false`, because it has no locals to zero.
- **Unchanged runtime methods compiled differently.** The runtime UTF-8 code is in CoreLib,
  which the attribute does not touch. Its Tier1 code still differed between the two variants
  under Dynamic PGO. On .NET 8, `UTF8Encoding.GetChars` changed its register allocation (215
  to 219 bytes), and `Utf8Utility.TranscodeToUtf16` moved about 100 instructions between hot
  and cold blocks (1,805 to 1,839 bytes). On .NET 10, `Ascii.WidenAsciiToUtf16` moved its
  4-byte loop-tail block below the method's `ret`, and `Utf8Utility.TranscodeToUtf16` and
  `GetPointerToFirstInvalidByte` reordered blocks.
- **With Dynamic PGO off, the runtime methods matched.** With `DOTNET_TieredPGO=0`, every
  captured runtime method had the same instruction sequence in both variants. Only the
  library prologues still differed.

These listings came from a probe that called the real methods with the benchmark inputs, on
Linux x64 with the same runtimes (8.0.31 and 10.0.12) on an Intel i7-12700K. Windows listings
with tiering off (full optimization, no PGO) differed even less: only `RespValue.AsString`
changed, by one 8-byte zeroing store.

### Explanation

The removed work is a few stores per call, well under a nanosecond. It cannot cost the
measured 11 ns (.NET 8) or 60 ns (.NET 10) by itself. The added time is in code the attribute
did not change, and that code differs between the variants only when Dynamic PGO is on. The
best-supported explanation:

> The attribute changes the size and timing of the library callers. That changes the profile
> Dynamic PGO collects for the shared CoreLib UTF-8 transcoder and ASCII widener, and with it
> their Tier1 block layout. For these two inputs, the new layout puts hot paths out of line
> (the .NET 10 widening tail moves below `ret`). The regression is a side effect of profile
> and layout, not of the removed zeroing.

This is a hypothesis that the evidence supports, not a proven cause. The original runs kept
no runtime-callee listings, native code maps or profiles. The listings above came from a
different CPU, outside BenchmarkDotNet. Code alignment can also contribute. The measurement
workflow tests the hypothesis directly: its `tiered-pgo: disabled` leg repeats the comparison
without Dynamic PGO. If the regression appears with PGO on and not with PGO off, the
explanation holds. A PGO-off result is diagnostic only. It cannot satisfy the gate, because
users run with PGO on.

### What this means for a scope

- The attribute removes real but small work. On these paths it is a few stores per call.
- The cost is unpredictable and can be much larger than that saving, because it depends on how
  the change shifts PGO and layout in callees the attribute does not touch.
- A shorter listing is not evidence of a benefit. Every scope needs its own timed comparison.
- Plausible targets are methods whose prologue zeroing is large compared with their work:
  large `stackalloc` buffers that are always overwritten, or big struct locals in hot loops.
  The 39 sites below are all small or are overwritten before they are read. None has shown a
  measured benefit.

## Stack allocation audit

`tests/Respire.Analyzers.Tests/LocalInitializationAuditTests.cs` checks every stack
allocation in every package, in two layers.

### Source layer: one justification per site

Each `stackalloc` in `src/` (excluding the analyzer project) needs a comment directly above
its statement:

```csharp
// stackalloc-audit[20a525cc283d1c24]: init: Utf8Formatter.TryFormat writes the long digits it reports;
// read: only the reported prefix is read; failure: 20 bytes hold any such value, and a false result would report zero bytes.
Span<byte> integerDigits = stackalloc byte[20];
```

- `init` says what writes the memory, `read` says the exact read boundary, and `failure`
  covers throw and false-return paths. Each field needs at least a short sentence.
- The bracketed value is a hash of the enclosing member's tokens under the four
  configurations (net8.0 and net10.0, each in DEBUG and RELEASE). Any code change to the
  member invalidates it, including the consumers, the read boundary or a branch that only one
  framework compiles. Comments are not part of the hash. When the test fails, it prints the
  new value. Re-audit the site, then update the comment.
- A statement with two allocations needs two comments.
- Explicit (`stackalloc byte[n]`) and implicit (`stackalloc[] { ... }`) syntax are both found.
  The test fails closed on a `stackalloc` that no audited configuration compiles, and on
  syntax Roslyn does not recognize as an allocation.

The comments sit next to the code, so there is no shared table for pull requests to conflict on.

### Compiled layer: what source cannot show

The test project builds all 28 package projects (the `RespirePackageProjects` list in
`Directory.Build.props`) for net8.0 and net10.0, without referencing them. It copies each
package assembly to `local-initialization-audit/<framework>/`. Then the test reads the IL:

- In repository namespaces (`Respire.*`, `Aspire.Respire.*`), the number of `localloc`
  instructions must equal the number of audited source sites for that package and framework.
  This also covers source-generator output. Respire.Json, Respire.Probabilistic,
  Respire.Search, Respire.StackExchangeCompat and Respire.TimeSeries run the Respire
  generators. A generator that starts emitting `stackalloc` changes the count.
- In other namespaces, every `localloc` is code compiled from a NuGet content source. Each one
  must appear in `ReviewedImportedSites`, keyed by assembly, framework, method and count. Today
  these are the three Polyfill 11.4.4 sites compiled into Respire and Respire.SignalR on
  net8.0: `Base64Url.Decode`, `Guid.CreateVersion7` and `HighNibble`. A Polyfill upgrade that
  adds or moves a site fails until someone reviews it.
- Every `SkipLocalsInitAttribute` must be in `ReviewedSkipLocalsInitScopes`, and every method
  that executes `localloc` must keep local initialization on.

`GeneratorsEmitNoStackAllocations` also checks the generator sources. No string they emit may
contain `stackalloc`, because generated code compiles into consumer assemblies, whose policy
Respire does not control.

### Limits

- Matching source text does not prove data-flow or memory safety. A reviewer still has to check
  each justification.
- The source layer parses package sources with the core project's evaluated framework symbols.
  No package defines its own symbols today. If a package starts to, extend the configurations.
- An imported site is keyed by method and count, not by its IL. A Polyfill change inside a
  reviewed method that keeps the count passes. Re-check `ReviewedImportedSites` when upgrading
  Polyfill.
- Method names in the compiled inventory are metadata names. A site inside a lambda or local
  function counts toward its package total like any other site.

### Tooling requirements

- **Audit:** the .NET 10 SDK from `global.json`, with net8.0 reference packs restorable. The
  test project builds every package for both frameworks, so its first build takes a few
  minutes. Run
  `dotnet build tests/Respire.Analyzers.Tests -c Release -f net10.0`, then
  `dotnet run --project tests/Respire.Analyzers.Tests -c Release -f net10.0 --no-build -- --treenode-filter "/*/*/LocalInitializationAuditTests/*"`.
- **Measurement workflow:** Python 3 on the runner, for `scripts/local_initialization_sources.py`
  and `scripts/assert_local_initialization_benchmarks.py`. Their controls are in
  `scripts/tests/test_local_initialization_sources.py`.

## Measuring a proposal

Add the `run-local-initialization-benchmarks` label to the pull request. The run measures the
pull request as it is when you add the label. Do not wait for it. Report the result in the
pull request description on a later pass.

`.github/workflows/benchmark-local-initialization.yml` checks out the pinned head twice and
derives the two trees from it:

- If the head applies `SkipLocalsInit` in `src/`, the baseline removes exactly those attribute
  lines (**shipped** mode). The comparison then measures the proposal.
- Otherwise the candidate adds `[module: SkipLocalsInit]` to the core Respire assembly
  (**probe** mode). This repeats the #1076 measurement on the current head.

The only source difference is the attribute. The derivation fails if `SkipLocalsInit` shares a
line with other code. The workflow records the exact patch and its hash. It runs baseline A,
the candidate and baseline B on the same runner, over the ten `ProtocolBenchmarks`
single-operation rows. The gate fails if any candidate confidence interval lies wholly above
both controls, or if any candidate allocation exceeds a control. Do not rerun just to get a
pass.

After timing, separate runs capture JIT listings of the regressing rows for both trees. They
cover the library callers and the CoreLib UTF-8 callees, with real addresses
(`DOTNET_JitDisasmDiffable=0`). The listings never come from timed runs.

The matrix runs twice: once with Dynamic PGO on (this leg gates) and once with
`DOTNET_TieredPGO=0` (report only). Per AGENTS.md, benchmark workflows run on net10.0 only.
The .NET 8 Unicode-read regression therefore needs a manual net8.0 comparison or a CI
correctness workflow. This workflow does not cover it.
