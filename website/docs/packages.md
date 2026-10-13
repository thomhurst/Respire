---
title: Packages and namespaces
description: Choose Respire packages by feature, with matching assembly and namespace names.
---

# Packages and namespaces

Each library uses the same name for its NuGet package, assembly, project, and root namespace. For example, install `Respire.Search`, reference `Respire.Search.dll`, and import `Respire.Search`. Libraries use the `Respire` product prefix, including APIs for Redis features. The .NET Aspire integrations are the exception: they follow Aspire's `Aspire.<Product>` naming.

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

`Respire.Analyzers` also has matching assembly and namespace names. It ships inside the core `Respire` package rather than as a separate NuGet package. Tests, samples, benchmarks, and pipeline projects are not published libraries.

Core APIs may use subnamespaces such as `Respire.Serialization` and `Respire.Compression`; a subnamespace does not imply a separate package. Import `Respire.Compression.Lz4` or `Respire.Compression.Zstd` for the optional codecs, and `Respire.Compression` for their shared options and abstractions. The Aspire packages put their registration methods in `Microsoft.Extensions.Hosting`, following Aspire convention.

Some packages add extension properties to the client, such as `redis.Json` and `redis.Coordination`. Extension properties need C# 14, the default for `net10.0` projects. On earlier language versions, construct the wrapper instead, for example `new RespireJsonClient(redis)` or `new RespireCoordination(redis)`.

## Redis features and custom modules

[Redis Open Source 8 includes JSON, Search, time series, and probabilistic data types](https://redis.io/blog/redis-8-ga/). Their client APIs remain optional packages so applications only take the dependencies they use. Earlier Redis deployments need the corresponding modules or Redis Stack. Other RESP-compatible servers may implement only some of these commands.

Reserve `Respire.Extensions.<ModuleName>` for client support for custom, separately installed Redis modules. None of the current packages belongs to this category. .NET integrations, cloud credentials, and client utilities use their feature names directly. C# extension methods alone do not make a package a Redis extension.

Installing a NuGet package does not install or enable a server feature. Check the target server's command support. Custom commands can use the [raw command API](./guides/raw-commands) or [command generator](./guides/generated-commands).

## Updating references from earlier versions

Earlier versions used `Respire.Extensions.*` names for some packages. The rename changes public namespaces and assembly identities, so rebuild consuming applications and libraries together.

- Replace `Respire.Extensions.` with `Respire.` in package references, project paths, and `using` directives for the existing packages listed above.
- JSON, Search, TimeSeries, and Probabilistic already use the short NuGet IDs; their package references stay the same.
- Replace `using Redis.Search;` with `using Respire.Search;`.
- Import `Respire.Compression.Lz4` for `Lz4ValueCodec` and `Respire.Compression.Zstd` for `ZstdValueCodec`.
- Update assembly-qualified type names, reflection configuration, and explicit DLL references. Do not mix old and renamed packages in one dependency graph.

The naming follows [.NET assembly naming guidance](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-assemblies-and-dlls): use the product name and the common namespace prefix for the assembly.
