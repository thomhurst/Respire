---
title: Experimental APIs
---

# Experimental APIs

Respire uses `System.Diagnostics.CodeAnalysis.ExperimentalAttribute` to identify public APIs whose contracts are still settling. These APIs are available to try, but are outside the 1.0 compatibility promise. They may change or be removed in a later release, including a minor or patch release.

The compiler reports an error when you use one of these APIs, even without warnings-as-errors enabled. Opt in by suppressing that feature's diagnostic ID with `NoWarn` in your project or a scoped `#pragma warning disable`. Each diagnostic page shows the exact opt-in. Suppression acknowledges the compatibility risk; it does not change runtime behavior or server requirements.

| Diagnostic | Experimental surface |
| --- | --- |
| [RSP0001](diagnostics/RSP0001.md) | VectorData integration |
| [RSP0002](diagnostics/RSP0002.md) | Semantic chat caching |
| [RSP0003](diagnostics/RSP0003.md) | Hedged reads |
| [RSP0004](diagnostics/RSP0004.md) | Hash import sessions |
| [RSP0005](diagnostics/RSP0005.md) | Deferred raw commands |
| [RSP0006](diagnostics/RSP0006.md) | StackExchange.Redis compatibility |

VectorData, AI, and StackExchangeCompat carry assembly-level attributes, so their entire public surfaces are experimental. Core Respire uses type/member attributes so ordinary client, hash, batch, and transaction APIs do not require an opt-in. Respire's own builds opt in to these six IDs to implement and test the features; that build setting is not shipped to consumers.

The `RSPxxxx` IDs are compiler diagnostics for experimental features. They are distinct from the `RESP001`–`RESP006` analyzer and source-generator diagnostics, which are tracked as shipped in release 1.0.0. An experimental ID remains tied to its feature and will not be reused for a different feature after stabilization.

See the [C# experimental attribute specification](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-12.0/experimental-attribute) for compiler behavior.
