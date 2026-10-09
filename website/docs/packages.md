---
title: Packages and namespaces
description: Choose Respire packages by feature, with matching assembly and namespace names.
---

# Packages and namespaces

Each library uses the same name for its NuGet package, assembly, project, and root namespace. For example, install `Respire.Search`, reference `Respire.Search.dll`, and import `Respire.Search`. All libraries use the `Respire` product prefix, including APIs for Redis features.

## Choose a package

| Package, assembly, and root namespace | Purpose |
| --- | --- |
| `Respire` | Core RESP client, commands, connections, serialization, and built-in compression abstractions |
| `Respire.Json` | Redis JSON documents |
| `Respire.Search` | Redis Search, indexing, aggregation, and vector queries |
| `Respire.VectorData` | Explicit AOT-friendly VectorData hash/JSON collections, KNN and hybrid search |
| `Respire.TimeSeries` | Redis time series |
| `Respire.Probabilistic` | Bloom, Cuckoo, Count-Min Sketch, Top-K, and t-digest |
| `Respire.Aws` | AWS IAM credentials for ElastiCache and MemoryDB |
| `Respire.Azure` | Microsoft Entra credentials for Azure Managed Redis |
| `Respire.Caching` | `IDistributedCache` and `IBufferDistributedCache` integration |
| `Respire.Caching.Hybrid` | Redis-backed L2 storage for `HybridCache` |
| `Respire.FusionCache` | FusionCache backplane sharing an existing Respire client |
| `Respire.SignalR` | SignalR scale-out sharing an existing Respire client |
| `Respire.Streaming` | Hosted stream consumers with bounded concurrency and scoped handlers |
| `Respire.StackExchangeCompat` | Binary-safe value conversion and native command bridge for incremental StackExchange.Redis migration |
| `Respire.Coordination` | Distributed coordination primitives |
| `Respire.DependencyInjection` | .NET dependency injection registration |
| `Respire.Compression.Lz4` | Optional LZ4 value codec |
| `Respire.Compression.Zstd` | Optional Zstandard value codec |
| `Respire.Testing` | In-memory RESP test server |
| `Respire.Testing.Containers` | Redis and Valkey container fixtures |

`Respire.Analyzers` also has matching assembly and namespace names. It ships inside the core `Respire` package rather than as a separate NuGet package. Tests, samples, benchmarks, and pipeline projects are not published libraries.

Core APIs may use subnamespaces such as `Respire.Serialization` and `Respire.Compression`; a subnamespace does not imply a separate package. Import `Respire.Compression.Lz4` or `Respire.Compression.Zstd` for the optional codecs, and `Respire.Compression` for their shared options and abstractions.

## Redis features and custom modules

[Redis Open Source 8 includes JSON, Search, time series, and probabilistic data types](https://redis.io/blog/redis-8-ga/). Their client APIs remain optional packages so applications only take the dependencies they use. Earlier Redis deployments need the corresponding modules or Redis Stack. Other RESP-compatible servers may implement only some of these commands.

Reserve `Respire.Extensions.<ModuleName>` for client support for custom, separately installed Redis modules. None of the current packages belongs to this category. .NET integrations, cloud credentials, and client utilities use their feature names directly. C# extension methods alone do not make a package a Redis extension.

Installing a NuGet package does not install or enable a server feature. Check the target server's command support. Custom commands can use the [raw command API](./guides/raw-commands) or [command generator](./guides/generated-commands).

## Updating existing references

This naming cleanup changes public namespaces and assembly identities. Rebuild consuming applications and libraries together.

- Replace `Respire.Extensions.` with `Respire.` in package references, project paths, and `using` directives for the existing packages listed above.
- JSON, Search, TimeSeries, and Probabilistic already use the short NuGet IDs; their package references stay the same.
- Replace `using Redis.Search;` with `using Respire.Search;`.
- Import `Respire.Compression.Lz4` for `Lz4ValueCodec` and `Respire.Compression.Zstd` for `ZstdValueCodec`.
- Update assembly-qualified type names, reflection configuration, and explicit DLL references. Do not mix old and renamed packages in one dependency graph.

The naming follows [.NET assembly naming guidance](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/names-of-assemblies-and-dlls): use the product name and the common namespace prefix for the assembly.
