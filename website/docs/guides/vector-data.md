---
title: Microsoft.Extensions.VectorData
description: Store typed vector records in Redis hashes through an explicit AOT-friendly mapper.
---

# Microsoft.Extensions.VectorData

`Respire.VectorData` implements `VectorStore` and string-keyed `VectorStoreCollection<string,TRecord>` from `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 using Redis Query Engine hashes. It supports collection lifecycle, record CRUD, sequential batch operations and unfiltered FLOAT32 KNN search. Use an unprefixed Respire client; Search commands reject client key prefixes. This connector supports standalone Redis Query Engine, included in standalone Redis 8. Redis Cluster is unsupported: collection deletion scans one node and cannot remove unindexed hashes across all shards, even when a search coordinator is available.

```bash
dotnet add package Respire.VectorData
```

## Explicit mapping

Derive from `RespireVectorDataHashMapper<TRecord>`. Implement `GetKey`, `Write` and `Read`, and declare vector fields and any indexed scalar fields. Mapping uses your code directly, so immutable records work without reflection, attribute scanning or runtime serialization. Mappers must be thread-safe. Custom TAG separators must be printable ASCII characters (U+0020 through U+007E); invalid separators fail when constructing the collection, before any Redis request. Omitting the separator retains Redis's default comma. The runnable [VectorData sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.VectorData) includes a complete `MovieMapper` that writes UTF-8 text and little-endian vector bytes.

```csharp
using Microsoft.Extensions.VectorData;
using Respire;
using Respire.VectorData;
using Respire.Samples.VectorData; // Movie and MovieMapper from the runnable sample.

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
using var store = new RespireVectorStore(client, "my-application");
store.RegisterMapper(new MovieMapper());
using var movies = store.GetHashCollection<Movie>("movies");
await movies.EnsureCollectionExistsAsync();
await movies.UpsertAsync(new Movie("arrival", "Arrival", new float[] { 1, 0 }));

var record = await movies.GetAsync("arrival", new RecordRetrievalOptions
{
    IncludeVectors = true,
});

await foreach (var hit in movies.SearchAsync(new float[] { 1, 0 }, top: 10))
    Console.WriteLine($"{hit.Record.Title}: distance {hit.Score}");
```

Keys and collection names must be nonblank valid UTF-8 strings. The store encodes namespace, collection and record names separately, so delimiter characters do not collide. Index names and document prefixes belong to the configured namespace. Register one mapper per record type in each store. Collection schemas use explicit names made of ASCII letters, digits and underscores; `vector_score` is reserved. Scalar schemas support the typed `RespireSearchField` options; aliases and raw schema tokens are unsupported. Vector dimensions and finite elements are validated before writes and queries. `RespireVectorDataFloat32.Encode` and `Decode` provide portable binary conversion.

Upsert replaces a complete record atomically in one Lua script. Optional fields omitted by `Write` are deleted, and existing expiry is cleared. Batch upserts run sequentially and are not atomic across records; an error may leave earlier records committed. Reads return null for missing keys. Vector fields are excluded unless `IncludeVectors` is true; the mapper's `Read` method must handle absent vectors. Returned buffers are owned and survive later replies. Disposing a collection or store does not dispose the caller's client.

## Search behavior

Search accepts `float[]`, `Memory<float>` or `ReadOnlyMemory<float>`. It returns Redis's distance unchanged: lower scores are better. `Skip` and `top` select a page from the nearest `Skip + top` candidates. `ScoreThreshold` is a maximum distance applied after that page is selected; filtered hits are not replaced, so fewer than `top` records can be returned. Set `VectorProperty` to a direct mapped property expression when multiple vectors are declared. Records deleted between search and retrieval are omitted without refilling the page; this is not a transactional snapshot. Search indexing can lag writes. Malformed search scores or documents outside the collection become `VectorStoreException` with an `InvalidOperationException` cause and collection/operation metadata.

This first connector supports hashes and explicit typed mappings. JSON storage, expression filters, filtered retrieval, hybrid search, embedding generation, dynamic dictionaries and non-string keys are unsupported. Unsupported filters and inputs throw; they are never silently ignored. Server and transport failures become `VectorStoreException` with the original Respire exception as their cause and collection/operation metadata. Cancellation stays `OperationCanceledException`. [The connector epic](https://github.com/thomhurst/Respire/issues/887) retains the remaining features and full official conformance suite. Tests include named lifecycle and basic-model contracts adapted from [the upstream conformance tests at the package source revision](https://github.com/dotnet/extensions/tree/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests). Passing this subset does not establish complete upstream conformance.

## Exception behavior

The exception contract distinguishes database failures from application validation. Mapper exceptions and vector/schema validation errors propagate unchanged, including `ArgumentException`. An incomplete `HGETALL` field/value pair throws `InvalidOperationException`. The search and collection-name decoding failures described above are wrapped with operation metadata. Cancellation stays `OperationCanceledException`.

## NativeAOT

Use the concrete `GetHashCollection<TRecord>` method for trimming and NativeAOT. The upstream `VectorStore.GetCollection<TKey,TRecord>` method carries `RequiresDynamicCode` and `RequiresUnreferencedCode` annotations, which overrides must preserve. The safe concrete method uses only registered mapper code. No warning suppression or reflection fallback is needed.

Publish the sample with its local NativeAOT setting:

```bash
dotnet publish samples/Respire.Samples.VectorData -c Release -f net10.0 -r linux-x64 -o artifacts/vector-data
./artifacts/vector-data/Respire.Samples.VectorData redis://localhost:6379
```

The sample also supports .NET 8. A runtime identifier enables its NativeAOT property only on the executable; passing a global `PublishAot` property would incorrectly apply it to the repository's netstandard analyzer project. Install the platform prerequisites from [Microsoft's NativeAOT guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). CI publishes and runs the native sample against Redis for both frameworks.
