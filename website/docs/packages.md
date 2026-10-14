---
title: Packages and namespaces
description: Choose Respire packages by feature, with matching assembly and namespace names.
---

# Packages and namespaces

Each library uses one name for its NuGet package, assembly, and root namespace: install `Respire.Search`, import `Respire.Search`. The .NET Aspire integrations are the exception; they follow Aspire's `Aspire.<Product>` naming.

## Public API review and deprecation

Every shipping package has a checked-in public API baseline in
`src/<package>/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. Both net8.0 and net10.0
check the same baseline: the packages expose the same public surface on both frameworks.
These baselines were seeded from the pre-1.0 API; the filename does not declare a stable release.
The PublicApiAnalyzers package checks signatures, including nullable annotations, during normal
builds. Adding, changing, or removing an API without updating its declaration fails the build.
The Modular Pipeline CI workflow builds all packages and frameworks with these checks enabled.

For contributors:

- Add new declarations to `PublicAPI.Unshipped.txt` using the analyzer's **Declare public API**
  code fix. Review the diff alongside the implementation.
- Keep shipped declarations. For an intentional removal, add `*REMOVED*` followed by the old
  declaration to the unshipped file. A changed signature is a removal plus an addition.
- Build both target frameworks before submitting the change. API baselines make changes visible
  for review; they do not authorize a breaking change. After 1.0, preserve compatibility or plan
  the removal for a major release.
- At release time, merge accepted unshipped declarations into the shipped baseline, applying
  removal markers, and reset the unshipped file to `#nullable enable`.

Deprecations are allowed, including after 1.0. An `[Obsolete]` attribute must provide a nonempty
`DiagnosticId` and an HTTPS `UrlFormat` containing `{0}` (the diagnostic ID), pointing to a published
migration note. The note should explain the replacement and migration steps. The core lifecycle
test checks this metadata without making network requests; reviewers verify that the note exists.

## Choose a package

| Package, assembly, and root namespace | Purpose |
| --- | --- |
| `Respire` | Core RESP client, commands, connections, serialization, and built-in compression abstractions |
| `Respire.Json` | Redis JSON documents |
| `Respire.Search` | Redis Search, indexing, aggregation, and vector queries |
| `Respire.VectorData` | Explicit AOT-friendly VectorData hash/JSON collections, KNN and hybrid search |
| `Respire.AI` | Semantic response caching for Microsoft.Extensions.AI chat clients |
| `Respire.TimeSeries` | Redis time series |
| `Respire.Probabilistic` | Bloom, Cuckoo, Count-Min Sketch, Top-K, and t-digest |
| `Respire.Aws` | AWS IAM credentials for ElastiCache and MemoryDB |
| `Respire.Azure` | Microsoft Entra credentials for Azure Managed Redis |
| `Respire.Caching` | `IDistributedCache` and `IBufferDistributedCache` integration |
| `Respire.Caching.Hybrid` | Redis-backed L2 storage for `HybridCache` |
| `Respire.OutputCaching` | ASP.NET Core output caching store |
| `Respire.DataProtection` | ASP.NET Core DataProtection key storage |
| `Respire.FusionCache` | FusionCache backplane and distributed locker sharing an existing Respire client |
| `Respire.SignalR` | SignalR scale-out sharing an existing Respire client |
| `Respire.Streaming` | Hosted stream consumers with bounded concurrency and scoped handlers |
| `Respire.StackExchangeCompat` | Binary-safe value conversion, native command bridge, and a limited `IConnectionMultiplexer` adapter for incremental StackExchange.Redis migration |
| `Respire.Coordination` | Rate limiters, semaphores, read-write locks, latches, leases, and Redlock |
| `Respire.DependencyInjection` | .NET dependency injection registration |
| `Respire.HealthChecks` | ASP.NET Core health checks using existing client connections |
| `Aspire.Respire` | .NET Aspire client registration, configuration, health checks, and telemetry |
| `Aspire.Respire.DistributedCaching`, `Aspire.Respire.HybridCaching`, `Aspire.Respire.OutputCaching` | `IDistributedCache`, `HybridCache`, and output caching on an Aspire-registered client |
| `Respire.Compression.Lz4` | Optional LZ4 value codec |
| `Respire.Compression.Zstd` | Optional Zstandard value codec |
| `Respire.Testing` | In-memory RESP test server |
| `Respire.Testing.Containers` | Redis and Valkey container fixtures |

- `Respire.Analyzers` ships inside the core `Respire` package, not as its own package.
- Subnamespaces such as `Respire.Serialization` and `Respire.Compression` belong to the core package. `Respire.Compression` holds the shared codec options; import `Respire.Compression.Lz4` or `Respire.Compression.Zstd` for a codec.
- The Aspire packages put their registration methods in `Microsoft.Extensions.Hosting`.
- Extension properties such as `redis.Json` and `redis.Coordination` need C# 14 (the `net10.0` default). On earlier language versions, construct the wrapper: `new RespireJsonClient(redis)` or `new RespireCoordination(redis)`.

## Keep internal API consumers on the same version

`Respire.Caching`, `Respire.Coordination`, `Respire.StackExchangeCompat`, `Respire.DataProtection`, `Respire.Json`, `Respire.Compression.Lz4`, `Respire.Compression.Zstd`, `Respire.Testing`, and `Respire.Streaming` depend on core implementation details. Their NuGet packages require the exact `Respire` version they were built against, including the prerelease suffix. Upgrade these packages together with `Respire`; mixing versions can break internal calls at runtime.

## Redis features and custom modules

[Redis Open Source 8 includes JSON, Search, time series, and probabilistic data types](https://redis.io/blog/redis-8-ga/); their client APIs are optional packages. Earlier Redis needs the matching modules or Redis Stack, and other RESP servers may support only some commands. Installing a package does not enable a server feature.

The name `Respire.Extensions.<ModuleName>` is reserved for custom, separately installed Redis modules; no current package uses it. For custom commands, use the [raw command API](./guides/raw-commands) or [command generator](./guides/generated-commands).

## Updating references from earlier versions

Some packages were named `Respire.Extensions.*`. The rename changes namespaces and assembly identities, so rebuild consuming applications and libraries together.

- Replace `Respire.Extensions.` with `Respire.` in package references, project paths, and `using` directives.
- JSON, Search, TimeSeries, and Probabilistic package IDs are unchanged.
- Replace `using Redis.Search;` with `using Respire.Search;`.
- Import `Respire.Compression.Lz4` for `Lz4ValueCodec` and `Respire.Compression.Zstd` for `ZstdValueCodec`.
- Update assembly-qualified type names, reflection configuration, and explicit DLL references. Do not mix old and renamed packages in one dependency graph.
