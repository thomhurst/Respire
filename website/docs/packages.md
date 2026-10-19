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
These baselines were seeded from the pre-2.0 API; the filename does not declare a stable release.
The PublicApiAnalyzers package checks signatures, including nullable annotations, during normal
builds. Adding, changing, or removing an API without updating its declaration fails the build.
The Modular Pipeline CI workflow builds all packages and frameworks with these checks enabled.

For contributors:

- Add new declarations to `PublicAPI.Unshipped.txt` using the analyzer's **Declare public API**
  code fix. Review the diff alongside the implementation.
- Keep shipped declarations. For an intentional removal, add `*REMOVED*` followed by the old
  declaration to the unshipped file. A changed signature is a removal plus an addition.
- Build both target frameworks before submitting the change. API baselines make changes visible
  for review; they do not authorize a breaking change. After 2.0, preserve compatibility or plan
  the removal for a major release.
- At release time, merge accepted unshipped declarations into the shipped baseline, applying
  removal markers, and reset the unshipped file to `#nullable enable`.

Deprecations are allowed, including after 2.0. An `[Obsolete]` attribute must provide a nonempty
`DiagnosticId` and an HTTPS `UrlFormat` containing `{0}` (the diagnostic ID), pointing to a published
migration note. The note should explain the replacement and migration steps. CI runs
`Respire.PublicApi.Tests` on net8.0 and net10.0 to check exported public types and members in every
shipping assembly. Its project references and expected assembly manifest come from
`RespirePackageProjects` in `Directory.Build.props`, plus `Respire.Analyzers`, which ships inside
the core package rather than as its own package; a missing assembly fails validation.
The compiler's required-member compatibility marker is excluded; authored deprecations on
constructors with required members still need migration metadata.
These checks make no network requests; reviewers verify that the migration note exists.

Packing also runs SDK package validation. See [Contributing API changes](contributing.md)
for the released-package baseline, intentional-break process, and experimental API exceptions.

## Choose a package

| Package, assembly, and root namespace | Purpose |
| --- | --- |
| [`Respire`](getting-started.md) | Core RESP client, commands, connections, serialization, and built-in compression abstractions |
| [`Respire.Json`](guides/json.md) | Redis JSON documents |
| [`Respire.Search`](guides/search.md) | Redis Search, indexing, aggregation, and vector queries |
| [`Respire.VectorData`](guides/vector-data.md) | Explicit AOT-friendly VectorData hash/JSON collections, KNN and hybrid search |
| [`Respire.AI`](guides/chat-history.md) | Redis chat history, Agent Framework history providers, and [semantic response caching](guides/semantic-cache.md) |
| [`Respire.TimeSeries`](guides/timeseries.md) | Redis time series |
| [`Respire.Probabilistic`](guides/probabilistic.md) | Bloom, Cuckoo, Count-Min Sketch, Top-K, and t-digest |
| [`Respire.Aws`](guides/aws-iam-credentials.md) | AWS IAM credentials for ElastiCache and MemoryDB |
| [`Respire.Azure`](guides/azure-managed-redis.md) | Microsoft Entra credentials for Azure Managed Redis |
| [`Respire.Gcp`](guides/google-cloud-memorystore.md) | Google Cloud IAM credentials for Memorystore for Redis Cluster and Valkey |
| [`Respire.Caching`](integrations/caching.md) | `IDistributedCache` and `IBufferDistributedCache` integration |
| [`Respire.Caching.Hybrid`](integrations/caching.md) | Redis-backed L2 storage for `HybridCache` |
| [`Respire.OutputCaching`](integrations/output-caching.md) | ASP.NET Core output caching store |
| [`Respire.Idempotency`](integrations/idempotency.md) | ASP.NET Core idempotency-key response replay |
| [`Respire.RateLimiting`](integrations/rate-limiting.md) | Redis-backed ASP.NET Core rate limiting policies |
| [`Respire.DataProtection`](integrations/data-protection.md) | DataProtection key storage and [payload encryption](guides/payload-encryption.md) |
| [`Respire.FusionCache`](integrations/fusioncache.md) | FusionCache backplane and distributed locker sharing an existing Respire client |
| [`Respire.SignalR`](integrations/signalr.md) | SignalR scale-out sharing an existing Respire client |
| [`Respire.Streaming`](guides/stream-workers.md) | Hosted stream consumers with bounded concurrency and scoped handlers |
| [`Respire.Streaming.Dashboard`](guides/stream-dashboard.md) | Authorized ASP.NET Core dashboard for job queues, stream groups, pending deliveries, and dead-letter actions |
| [`Respire.StackExchangeCompat`](guides/stackexchange-interop.md) | Binary-safe value conversion, native command bridge, and a limited `IConnectionMultiplexer` adapter for incremental StackExchange.Redis migration |
| [`Respire.Coordination`](guides/coordination.md) | Rate limiters, semaphores, read-write locks, latches, leases, and Redlock |
| [`Respire.DependencyInjection`](integrations/dependency-injection.md) | .NET dependency injection registration |
| [`Respire.HealthChecks`](integrations/health-checks.md) | ASP.NET Core health checks using existing client connections |
| [`Respire.Resilience`](guides/external-resilience.md) | [Polly v8 and Microsoft.Extensions.Resilience](guides/external-resilience.md) with one command retry owner |
| [`Aspire.Respire`](integrations/aspire.md) | .NET Aspire client registration, configuration, health checks, and telemetry |
| [`Aspire.Respire.DistributedCaching`](integrations/aspire.md), [`Aspire.Respire.HybridCaching`](integrations/aspire.md), [`Aspire.Respire.OutputCaching`](integrations/aspire.md) | `IDistributedCache`, `HybridCache`, and output caching on an Aspire-registered client |
| [`Respire.Compression.Lz4`](guides/value-codecs.md) | Optional LZ4 value codec |
| [`Respire.Serialization.MessagePack`](fundamentals/values-and-serialization.md#binary-serializer-packages) | MessagePack serializer and DI registration |
| [`Respire.Serialization.Protobuf`](fundamentals/values-and-serialization.md#binary-serializer-packages) | protobuf-net serializer and DI registration |
| [`Respire.Serialization.MemoryPack`](fundamentals/values-and-serialization.md#binary-serializer-packages) | MemoryPack serializer and DI registration |
| [`Respire.Compression.Zstd`](guides/value-codecs.md) | Optional Zstandard value codec |
| [`Respire.Testing`](guides/in-memory-testing.md) | In-memory RESP test server |
| [`Respire.Testing.Containers`](guides/testing-containers.md) | Redis and Valkey container fixtures |

- `Respire.Analyzers` ships inside the core `Respire` package, not as its own package.
- Subnamespaces such as `Respire.Serialization` and `Respire.Compression` belong to the core package. `Respire.Compression` holds the shared codec options; import `Respire.Compression.Lz4` or `Respire.Compression.Zstd` for a codec.
- The Aspire packages put their registration methods in `Microsoft.Extensions.Hosting`.
- Extension properties such as `redis.Json` and `redis.Coordination` need C# 14 (the `net10.0` default). On earlier language versions, construct the wrapper: `new RespireJsonClient(redis)` or `new RespireCoordination(redis)`.

## Keep internal API consumers on the same version

`Respire.Caching`, `Respire.Coordination`, `Respire.StackExchangeCompat`, `Respire.DataProtection`, `Respire.Json`, `Respire.Compression.Lz4`, `Respire.Compression.Zstd`, `Respire.Testing`, `Respire.Streaming`, and `Respire.Streaming.Dashboard` depend on core implementation details. Their NuGet packages require the exact `Respire` version they were built against, including the prerelease suffix. Upgrade these packages together with `Respire`; mixing versions can break internal calls at runtime.

The three `Respire.Serialization.*` adapters also pin their core dependency exactly so
serializer and client packages are upgraded together, even though they use public APIs.

## Redis features and custom modules

[Redis Open Source 8 includes JSON, Search, time series, and probabilistic data types](https://redis.io/blog/redis-8-ga/); their client APIs are optional packages. Earlier Redis needs the matching modules or Redis Stack, and other RESP servers may support only some commands. Installing a package does not enable a server feature.

The name `Respire.Extensions.<ModuleName>` is reserved for custom, separately installed Redis modules; no current package uses it. For custom commands, use the [raw command API](./guides/raw-commands) or [command generator](./guides/generated-commands).

<a id="updating-references-from-earlier-versions" />

For older `Respire.Extensions.*` references, see the [package migration guide](guides/package-migration.md).
