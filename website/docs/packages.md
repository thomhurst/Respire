---
title: Choose packages
description: Pick the Respire packages for what you're building. Each package name is also its namespace.
---

# Choose packages

Start with `Respire`. It includes the client, every command, locks, pub/sub, streams,
batches, transactions, client-side caching, Cluster, Sentinel, and OpenTelemetry. Add other
packages only when you need what they provide.

Each package uses one name for its NuGet package, assembly, and root namespace: install
`Respire.Search`, then add `using Respire.Search;`.

## By what you're building

| I'm building… | Install |
| --- | --- |
| A console app, worker, or library | `Respire` |
| An ASP.NET Core app | `Respire.DependencyInjection` (includes `Respire`) |
| A .NET Aspire app | `Respire.Aspire` (not on NuGet yet; see [Aspire](integrations/aspire.md)) |
| `IDistributedCache` or `HybridCache` | `Respire.Caching` or `Respire.Caching.Hybrid` |
| HTTP output caching, rate limiting, or idempotency keys | `Respire.OutputCaching`, `Respire.RateLimiting`, `Respire.Idempotency` |
| Rate limiters, semaphores, leader election, or Redlock | `Respire.Coordination` |
| Job queues, reliable queues, or stream workers | `Respire.Streaming` (plus `Respire.Streaming.Dashboard` for the UI) |
| JSON documents, search, time series, or Bloom filters | `Respire.Json`, `Respire.Search`, `Respire.TimeSeries`, `Respire.Probabilistic` |
| AI apps: chat history, semantic cache, vector search | `Respire.AI`, `Respire.VectorData` |
| Azure, AWS, or Google Cloud managed Redis with IAM | `Respire.Azure`, `Respire.Aws`, `Respire.Gcp` |
| Health checks | `Respire.HealthChecks` |
| Tests | `Respire.Testing` (in-memory) or `Respire.Testing.Containers` (real Redis) |
| A gradual move from StackExchange.Redis | `Respire.StackExchangeCompat` |

To start from a working project instead, use the
[project templates](getting-started.md#project-template): `dotnet new install Respire.Templates`.

## All packages

| Package, assembly, and root namespace | Purpose |
| --- | --- |
| [`Respire`](getting-started.md) | Core RESP client, commands, connections, serialization, and built-in compression abstractions |
| [`Respire.Json`](guides/json.md) | Redis JSON documents |
| [`Respire.Search`](guides/search.md) | Redis Search, indexing, aggregation, and vector queries |
| [`Respire.VectorData`](guides/vector-data.md) | Explicit AOT-friendly VectorData hash/JSON collections, KNN and hybrid search |
| [`Respire.AI`](guides/chat-history.md) | Redis chat history, Agent Framework history providers, [semantic response caching](guides/semantic-cache.md), and [exact-text embedding caching](guides/embedding-cache.md) |
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
| [`Respire.Aspire`](integrations/aspire.md) | .NET Aspire client registration, configuration, health checks, and telemetry |
| [`Respire.Aspire.DistributedCaching`](integrations/aspire.md), [`Respire.Aspire.HybridCaching`](integrations/aspire.md), [`Respire.Aspire.OutputCaching`](integrations/aspire.md) | `IDistributedCache`, `HybridCache`, and output caching on an Aspire-registered client |
| [`Respire.Compression.Lz4`](guides/value-codecs.md) | Optional LZ4 value codec |
| [`Respire.Serialization.MessagePack`](fundamentals/values-and-serialization.md#binary-serializer-packages) | MessagePack serializer and DI registration |
| [`Respire.Serialization.Protobuf`](fundamentals/values-and-serialization.md#binary-serializer-packages) | protobuf-net serializer and DI registration |
| [`Respire.Serialization.MemoryPack`](fundamentals/values-and-serialization.md#binary-serializer-packages) | MemoryPack serializer and DI registration |
| [`Respire.Compression.Zstd`](guides/value-codecs.md) | Optional Zstandard value codec |
| [`Respire.Testing`](guides/in-memory-testing.md) | In-memory RESP test server |
| [`Respire.Testing.StackExchangeCompat`](guides/in-memory-testing.md#stackexchangeredis-test-fixtures) | In-memory fixture for the supported StackExchange.Redis compatibility subset |
| [`Respire.Testing.Containers`](guides/testing-containers.md) | Redis and Valkey container fixtures |

- `Respire.Analyzers` ships inside the core `Respire` package, not as its own package.
- Subnamespaces such as `Respire.Serialization` and `Respire.Compression` belong to the core package. `Respire.Compression` holds the shared codec options; import `Respire.Compression.Lz4` or `Respire.Compression.Zstd` for a codec.
- The Aspire packages put their registration methods in `Microsoft.Extensions.Hosting`.
- Extension properties such as `redis.Json` and `redis.Coordination` need C# 14 (the `net10.0` default). On earlier language versions, construct the wrapper: `new RespireJsonClient(redis)` or `new RespireCoordination(redis)`.

## Upgrade packages together

Keep every Respire package on the same version. `Respire.Caching`, `Respire.Coordination`, `Respire.StackExchangeCompat`, `Respire.DataProtection`, `Respire.Json`, `Respire.Compression.Lz4`, `Respire.Compression.Zstd`, `Respire.Testing`, `Respire.Streaming`, and `Respire.Streaming.Dashboard` require the exact `Respire` version they were built against, including any prerelease suffix. Mixing versions can fail at runtime.

The three `Respire.Serialization.*` adapters also pin their core dependency exactly so
serializer and client packages are upgraded together, even though they use public APIs.

## Server modules

[Redis Open Source 8 includes JSON, Search, time series, and probabilistic data types](https://redis.io/blog/redis-8-ga/); their client APIs are optional packages. Earlier Redis needs the matching modules or Redis Stack, and other RESP servers may support only some commands. Installing a package does not enable a server feature.

The name `Respire.Extensions.<ModuleName>` is reserved for custom, separately installed Redis modules; no current package uses it. For custom commands, use the [raw command API](./guides/raw-commands) or [command generator](./guides/generated-commands).

<a id="updating-references-from-earlier-versions" />

For older `Respire.Extensions.*` references, see the [package migration guide](guides/package-migration.md).
