---
title: Contributing API changes
description: Validate NuGet compatibility and review intentional API breaks.
---

# Contributing API changes

Update the [public API declarations](packages.md#public-api-review-and-deprecation) alongside
each API change. Build both supported frameworks and run `Respire.PublicApi.Tests` to check
the API and deprecation policy.

## Validate the package

Every shipping project enables the SDK's
[NuGet package validation](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview)
in `Directory.Build.props`. Validation runs during `dotnet pack`, including the Modular
Pipeline's pack step. A successful build alone does not run it. For example, from the repository root:

```powershell
dotnet pack src/Respire/Respire.csproj -c Release
```

Pack all target frameworks together; overriding `TargetFrameworks` would hide discrepancies
between the net8.0 and net10.0 assets. Tests, samples, benchmarks, and the bundled analyzer
project are not independently packable.

Before the first supported major release, validation checks compatible assets within each
package. It does **not** yet compare against a published package. Once all shipping packages
are available on NuGet at 2.0.0, add this property to the package-validation property group in
`Directory.Build.props`:

```xml
<PackageValidationBaselineVersion>2.0.0</PackageValidationBaselineVersion>
```

Restore and pack every shipping project after changing the baseline. The SDK restores each
project's baseline package using its package ID. Keep the baseline fixed between releases.
After each stable release, advance it to the latest published stable version so APIs added in
minor and patch releases are protected too. For example, after publishing 2.1.0, use 2.1.0 as
the baseline for subsequent changes; retaining 2.0.0 would miss removal of an API added in 2.1.0.
Do not advance the baseline to an unpublished version or change it merely to make a PR pass.
A package introduced later needs its own first published stable version as a project-level
baseline override; advance that override after each of its stable releases as well. Do not use
a pre-2.0 package as the stable contract.

## Intentional breaks

For stable APIs, preserve compatibility in minor and patch releases. Schedule an intentional
break for a major release and describe the affected callers and migration in the PR and feature
guide. Updating `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt` does not approve a binary
break or suppress package validation.

When a reviewed break produces an ApiCompat error, generate candidate suppressions for only
the affected project:

```powershell
dotnet pack src/Respire/Respire.csproj -c Release -p:ApiCompatGenerateSuppressionFile=true
```

Review `src/Respire/CompatibilitySuppressions.xml` before committing it. The SDK reads this
file automatically. Keep only entries for the approved change, retaining the diagnostic ID,
exact target, left and right assets, and `IsBaselineSuppression` value. Add an XML comment
explaining the reason and linking the issue. A baseline exception must use
`IsBaselineSuppression=true`; it must not exempt differences between assets in the new package.
Then pack again **without** the generation flag to prove the reviewed exceptions are sufficient.

Do not disable validation, add broad `CPxxxx`/`PKVxxx` entries to `NoWarn`, or check in automatic
suppression generation. Restore an accidentally dropped framework instead of suppressing its
diagnostic. When a supported framework is intentionally retired in a major release, document
the migration and review that framework exception separately. After each stable release, advance
the baseline and remove obsolete suppressions; unused suppressions fail validation by
default. See the SDK's
[suppression format](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/diagnostic-ids#how-to-suppress).

## Experimental APIs

The [RSP0001–RSP0006 surfaces](experimental-apis.md) remain outside the stable compatibility
promise, including in minor and patch releases. Keep their `ExperimentalAttribute` markers
and public API declarations accurate. The compiler opt-ins in `NoWarn` do not constitute
ApiCompat suppressions.

If package validation reports a change to an experimental API, use the same per-project,
per-target suppression review above. Verify that the old API was experimental in the baseline
assembly (directly, through its containing type, or at assembly level), and record its RSP ID
and the reason in the XML comment and PR. Adding an experimental marker to a previously stable
API does not authorize breaking that API. Do not suppress the whole core assembly to exempt
its few experimental members, or suppress framework discrepancies. When an API becomes stable,
remove its experimental marker and reassess its suppressions at the next baseline update.
