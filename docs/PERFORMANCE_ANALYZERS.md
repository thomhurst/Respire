# Performance analyzer policy

Every Performance-category analyzer rule is a build error in `src/`. Fix the code without changing
behavior. Suppress only when a rule conflicts with a public contract or ownership boundary, and then
only for the exact symbol or smallest statement region, with a reason. Build each affected source
project for both frameworks before pushing.

## Configuration

- All `src/` projects, including the netstandard analyzer assembly, set
  `AnalysisLevelPerformance=latest-all` and treat warnings as errors. See the SDK's
  [category analysis settings](https://learn.microsoft.com/dotnet/core/project-sdk/msbuild-props#analysislevelcategory).
- The package-project inventory in `Directory.Build.props` checks each source project uses this policy.
- `src/.editorconfig` keeps explicit error severities for rules enforced earlier. The rest become
  errors through `TreatWarningsAsErrors`. There is no category-wide suppression or `NoWarn` list.
- `global.json` selects the SDK and so the analyzer release. An SDK upgrade can add diagnostics on
  either framework.
- Tests, benchmarks, samples and tools keep their own analysis settings.
- Regular Release builds enforce the policy; no opt-in flag is needed.

## Exceptions

- Never suppress a whole file, project, namespace or category for one declaration.
- A new diagnostic needs review even if a neighboring declaration has an exception.
- Remove an exception when its declaration is removed or its contract changes.
- Do not change ordering or ownership just to silence a diagnostic. Keep the existing narrow
  lifecycle and cancellation suppressions.

`PerformanceSuppressions.cs` files and record-parameter attributes record the existing exceptions:

| Rule | Allowed for | Notes |
| --- | --- | --- |
| CA1819 | Response DTO properties that return existing array storage without copying | Changing them would break the public response contract ([guidance](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1819#when-to-suppress-warnings)). New properties on the same DTO are still diagnosed. On positional records, use `param: SuppressMessage`; `property:` and global property targets do not suppress it. |
| CA1815 | Command-building values, decoded status/view types, ownership handles, awaiters and enumerators used through their operations, not compared whole | Each exception states the type's role. Do not conflate resource identity, payload equality or enumeration state. Types meant for comparison or hash keys implement equality ([guidance](https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca1815)). |
| CA1812 | The output-cache cleanup service | Dependency injection constructs it through its `IHostedService` registration. |
