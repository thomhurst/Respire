# Performance analyzer policy

All projects under `src/`, including the netstandard analyzer assembly, enable
the SDK's complete Performance category with `AnalysisLevelPerformance=latest-all`
and treat build warnings as errors. The package-project inventory in
`Directory.Build.props` checks that each source project belongs to this policy.
The repository SDK selection in `global.json` determines the analyzer release;
an SDK upgrade can therefore introduce new diagnostics on either target framework.
Tests, benchmarks, samples, and tools retain their existing analysis settings.

`src/.editorconfig` retains explicit error severities for the rules enforced before
the full category was enabled. The remaining Performance rules become build errors
through `TreatWarningsAsErrors`; no category-wide suppression or `NoWarn` list is used.
See the SDK's [category analysis settings](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props#analysislevelcategory).

## Reviewing exceptions

Prefer a behavior-preserving fix. When a rule conflicts with an established public
contract or an ownership boundary, suppress the exact symbol or the smallest
statement region and explain the reason. Do not suppress a whole file, project,
namespace, or category to accommodate one declaration. A new diagnostic requires
review even when a similar neighboring declaration has an exception.

`PerformanceSuppressions.cs` files and individual record-parameter attributes record
exact existing API exceptions:

- CA1819 exceptions identify response DTO properties that return existing array
  storage without copying on access. Changing these properties to collections or
  methods would change the public response contract. Microsoft's
  [CA1819 guidance](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1819#when-to-suppress-warnings)
  permits DTO exceptions. New properties on the same DTO are still diagnosed.
  For positional records, the diagnostic belongs to the constructor parameter:
  use `param: SuppressMessage`, not `property:` or a global property target. A
  compiler probe confirms that the latter two do not suppress this diagnostic.
- CA1815 exceptions identify command-building values, decoded status/view types,
  ownership handles, awaiters, and enumerators intended to be consumed through
  their operations or properties rather than compared as whole values. Each
  exception states that type's role. Resource identity, payload equality, and
  enumeration state must not be conflated to satisfy an analyzer. Types intended
  for comparison or hash keys should implement appropriate equality, following
  [CA1815 guidance](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1815).

Remove an exception when its declaration is removed or its contract changes.
The output-cache cleanup service has a class-specific CA1812 exception because
dependency injection constructs it through its `IHostedService` registration.
Keep existing narrowly documented lifecycle/cancellation suppressions: replacing
an operation solely to silence a diagnostic must not change ordering or ownership.

Build each affected source project for both supported frameworks before pushing.
The regular Release builds enforce the policy; no separate opt-in analyzer flag
is required.
