---
title: Microsoft.Extensions.VectorData
description: Store typed vector records in Redis hashes or JSON through explicit AOT-friendly mapping.
---

# Microsoft.Extensions.VectorData

`Respire.VectorData` implements `VectorStore` and `VectorStoreCollection<string,TRecord>` from `Microsoft.Extensions.VectorData.Abstractions` 10.10.0. Records live in Redis Query Engine hashes or RedisJSON documents. Use it when you want the standard VectorData API with explicit, reflection-free mapping.

Both storage kinds support collection lifecycle, CRUD, sequential batches, expression filters, filtered retrieval, FLOAT32 KNN search and hybrid text/vector search.

Requirements:

- Standalone Redis with Query Engine, plus RedisJSON for JSON collections. Redis 8 includes both.
- Redis 8.4 or later for hybrid search (`FT.HYBRID`).
- An unprefixed Respire client. Search commands reject client key prefixes; the store applies its own key namespace.

Redis Cluster is unsupported: collection deletion scans one node and cannot remove unindexed records on other shards.

```bash
dotnet add package Respire.VectorData
```

## Explicit mapping

Derive from `RespireVectorDataHashMapper<TRecord>`. Implement `GetKey`, `Write` and `Read`, and declare vector fields and any indexed scalar fields. Immutable records work without reflection or runtime serialization. The runnable [VectorData sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.VectorData) includes a complete `MovieMapper`.

- Mappers must be thread-safe.
- Custom TAG separators must be printable ASCII (U+0020 to U+007E). Invalid separators fail when the collection is constructed, before any Redis request. The default is a comma.

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

Names and schemas:

- Keys and collection names must be nonblank valid UTF-8. Namespace, collection and record names are encoded separately, so delimiters cannot collide.
- Register one mapper per record type per store.
- Schema field names use ASCII letters, digits and underscores. `vector_score` is reserved.
- Scalar schemas support the typed `RespireSearchField` options. Aliases and raw schema tokens are unsupported.
- Vector dimensions and finite elements are validated before writes and queries. `RespireVectorDataFloat32.Encode` and `Decode` convert vectors to and from bytes.

Reads and writes:

- Upsert replaces the whole record atomically. Optional fields omitted by `Write` are deleted, and any expiry is cleared.
- **Batch upserts run sequentially and are not atomic across records.** An error can leave earlier records committed.
- Reads return null for missing keys.
- Vectors are excluded unless `IncludeVectors` is true, so `Read` must handle absent vectors.
- Returned buffers are owned and survive later replies.
- Disposing a collection or store does not dispose your client.

## JSON storage

Choose hashes for explicit field codecs and binary vectors. Choose JSON for nested documents and generated `System.Text.Json` serialization.

Derive from `RespireVectorDataJsonMapper<TRecord>` and pass a generated `JsonTypeInfo<TRecord>` to its constructor. There is no reflection fallback. The generated context must support serialization and deserialization, and the model must deserialize with vector properties absent, so do not mark vector properties required. Your `JsonPropertyName` attributes and serializer options decide stored names and values.

The sample's `JsonMovieMapper` stores the title at `$.details.movie_title` and the `Vector` property at `$.embedding`, and declares a second vector:

```csharp
using Respire;
using Respire.VectorData;
using Respire.Samples.VectorData; // JsonMovie and JsonMovieMapper from the runnable sample.

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
using var store = new RespireVectorStore(client, "my-application");
store.RegisterMapper(new JsonMovieMapper());
using var jsonMovies = store.GetJsonCollection<JsonMovie>("json-movies");
await jsonMovies.EnsureCollectionExistsAsync();
await jsonMovies.UpsertAsync(new JsonMovie("arrival", new("Arrival"), [1, 0]));

await foreach (var hit in jsonMovies.SearchAsync(new float[] { 1, 0 }, 10,
    new() { VectorProperty = movie => movie.Vector, IncludeVectors = true }))
    Console.WriteLine($"{hit.Record.Details.Title}: distance {hit.Score}");
```

Paths and aliases:

- A JSON scalar field's identifier is a property path and its alias is the query name, for example `new("$.details.movie_title", RespireSearchFieldType.Text, Alias: "title")`.
- A vector's `StorageName` is its query alias. `JsonPath` selects the stored property and defaults to `$.StorageName`.
- Paths have the form `$.property.nested_property`. Segments use ASCII letters, digits or underscores. Array indices, wildcards, recursive selectors and bracket syntax are unsupported.
- Aliases follow the hash name rules, including reserved `vector_score`. Duplicate paths or aliases fail before I/O.
- Collection creation checks every path against the generated metadata before I/O. Paths must match serialized names after naming policies and `JsonPropertyName`.
- For a nested vector, set `PropertyName` to the full CLR path (for example `Data.Values`) and select it with `VectorProperty = record => record.Data.Values`.

Storage behavior:

- JSON vectors are numeric arrays; hash vectors are little-endian byte blobs. Query vectors are binary FLOAT32 for both.
- Missing or null vectors stay valid and unindexed.
- `IncludeVectors` applies to every declared vector, including nested ones. Omitting vectors keeps sibling data.
- Upsert replaces the whole document with `JSON.SET` and clears expiry. Omitted optional data and vectors disappear.
- Collection deletion also removes records written before index creation, without touching other namespaces.
- Batch, cancellation, distance, paging and exceptions behave as for hashes.
- `GetHashCollection` and `GetJsonCollection` reject a mapper of the wrong storage kind. The upstream `GetCollection<string,TRecord>` picks the registered kind.

## Search behavior

Search accepts `float[]`, `Memory<float>` or `ReadOnlyMemory<float>`. Lower scores are better.

| Metric | Score returned |
| --- | --- |
| COSINE | Cosine distance |
| L2 | Squared Euclidean distance |
| IP | Negative dot product (Redis's `1 - dot(a,b)` minus 1) |

- `Skip` and `top` select a page from the nearest `Skip + top` candidates.
- `ScoreThreshold` is a maximum distance applied after the page is selected. Filtered-out hits are not replaced, so you can get fewer than `top` records.
- Set `VectorProperty` when more than one vector is declared.
- Records deleted between search and retrieval are omitted, without refilling the page. Results are not a transactional snapshot, and indexing can lag writes.

Unsupported: embedding generation, dynamic dictionaries and non-string keys. Unsupported filters and inputs always throw; they are never silently ignored.

## Expression filters

Declare `FilterFields` on either mapper with CLR property names, unique query aliases and `RespireVectorDataFilterKind` values. They add their own schema fields, so do not repeat their aliases in `DataFields`. A raw TAG or TEXT data field alone does not enable filtering.

- Filtering requires Redis Query Engine 2.10 or later (included in Redis 8).
- Recreate existing indexes after adding filter fields.

### Hash filter encoding

The sample `MovieMapper` declares ordinal string filters for `Title` and `Tag`, stored in `filter_title` and `filter_tag`.

- Strings: write with `RespireVectorDataFilterEncoding.EncodeTag` (collections: `EncodeTags`) and store the text as UTF-8 bytes. Decode with `DecodeTag`, or keep separate original fields as the sample does. Encoding preserves case, empty strings, whitespace, separators and Unicode, and blocks query syntax. Do not use raw TAG fields for exact string matching: Redis splits separators and trims whitespace.
- Booleans: write invariant `0` or `1`.
- Numbers: write with `RespireVectorDataFilterEncoding.EncodeNumber`. Formatting a `float` directly can shift its indexed boundary.
- Write every nonnullable scalar filter property.
- For null scalars, omit the field. Explicit null hash bytes are unsupported. Encoded empty strings remain present.
- String collections hold nonnull elements. An omitted or empty collection has no membership matches.

### JSON filter encoding

Set each filter's `JsonPath` to its serialized path (default `$.StorageName`) and `PropertyName` to the CLR path, including dotted nested paths. For example, `JsonMovieMapper` declares `new("Details.Title", "filter_title", RespireVectorDataFilterKind.String) { JsonPath = "$.details.movie_title" }`; the title stays indexed as TEXT under the separate `title` alias.

- Upsert stores normalized filter copies under the reserved root object `__respire_filters`, using the same encodings as hashes. Your serialized values are unchanged, and the copies are removed before deserialization. Models and extension data must not use `__respire_filters`.
- Documents written outside the connector must maintain `__respire_filters` themselves. After adding mappings, recreate the index and upsert existing documents.
- Null and omitted properties behave like missing hash fields.
- Upsert rejects null or omitted nonnullable numeric or Boolean filter properties before I/O. Do not suppress them with `WhenWritingDefault`; use nullable types when a value or parent may be absent.
- Filter paths require standard converters and strict number handling, including on parents and collection elements. Custom converters and non-strict number handling are rejected at collection creation.

### Example and operators

```csharp
using Respire;
using Respire.VectorData;
using Respire.Samples.VectorData; // Movie and MovieMapper from the runnable sample.

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
using var store = new RespireVectorStore(client, "my-application");
store.RegisterMapper(new MovieMapper());
using var movies = store.GetHashCollection<Movie>("movies");
await movies.EnsureCollectionExistsAsync();

var title = "Arrival";
await foreach (var hit in movies.SearchAsync(new float[] { 1, 0 }, top: 10,
    new() { Filter = movie => movie.Title == title && movie.Tag != null }))
    Console.WriteLine(hit.Record.Title);

await foreach (var movie in movies.GetAsync(movie => movie.Title == title, top: 10,
    new() { Skip = 0, IncludeVectors = true }))
    Console.WriteLine(movie.Title);
```

| Operand | Operators |
| --- | --- |
| Mapped `string` | Ordinal `==`, `!=`, including null |
| Mapped `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `float`, `double` and nullable forms | `==`, `!=`, `<`, `<=`, `>`, `>=`; values must be finite |
| `bool` and `bool?` | `==`, `!=`, direct predicates, `!` |
| Any predicate | `&&`, `\|\|`, parentheses, `!`, constant `true`/`false` |
| Mapped `string[]` and `List<string>` | `Contains(value)`, `Any(element => values.Contains(element))` |
| Inline/captured arrays and `List<T>` | `Contains(mappedScalar)`, including `Enumerable.Contains`, `MemoryExtensions.Contains` and an explicit null comparer |

- Missing nullable fields fail relational comparisons and match `!=` against nonnull values.
- Values can be literals, initialized arrays, `Array.Empty<T>()`, captured locals and field chains (including static fields). Captured nullable `.Value` throws when null.
- Expressions are read without compilation.

Unsupported expressions throw `NotSupportedException` before a request: unmapped properties, captured property getters, method calls, arithmetic, property-to-property comparisons, custom comparers, substring/prefix matching, collection equality, `long`, `ulong`, `decimal`, enums, dates, record `.Value` access on nullable properties (compare the nullable directly), and conversions other than nullable lifting, small-integer promotion and lossless promotion to double.

Filters apply before KNN selects the nearest `Skip + top` candidates. Filtered `GetAsync` pages with `Skip`/`top` in unspecified order; `OrderBy` is unsupported.

## Hybrid search

Collections implement `IKeywordHybridSearchable<TRecord>` using [`FT.HYBRID`](https://redis.io/docs/latest/commands/ft.hybrid/) (Redis Open Source 8.4 or later with Redis Search). If the server lacks the command, the call throws `NotSupportedException` naming the capability. Permission, index and query errors keep their server diagnostics. There is no client-side fusion fallback.

Declare `TextFields` on either mapper to bind CLR properties to indexed TEXT fields in `DataFields`. Each binding needs a unique property and field. JSON bindings use the query alias and may use dotted CLR paths. `NoIndex` fields cannot be hybrid text fields.

<!-- doc-test-declaration -->
```csharp
using Respire.Search;
using Respire.VectorData;
using Respire.Samples.VectorData; // Movie from the runnable sample.

public abstract class MovieTextMapper : RespireVectorDataHashMapper<Movie>
{
    public override IReadOnlyList<RespireSearchField> DataFields =>
        [new("title", RespireSearchFieldType.Text)];

    public override IReadOnlyList<RespireVectorDataTextField> TextFields =>
        [new(nameof(Movie.Title), "title")];
}
```

Pass a FLOAT32 vector and one or more nonblank keywords. Keywords are escaped as quoted terms or phrases and joined with OR, so they cannot inject query syntax. Set `AdditionalProperty` when several text bindings exist and `VectorProperty` when several vectors exist. Expression filters apply to both legs before ranking.

```csharp
using Microsoft.Extensions.VectorData;
using Respire;
using Respire.VectorData;
using Respire.Samples.VectorData; // Movie and MovieMapper from the runnable sample.

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
using var store = new RespireVectorStore(client, "my-application");
store.RegisterMapper(new MovieMapper());
using var collection = store.GetHashCollection<Movie>("movies");
await collection.EnsureCollectionExistsAsync();

IKeywordHybridSearchable<Movie> hybrid = collection;
await foreach (var hit in hybrid.HybridSearchAsync(
    new float[] { 1, 0 }, ["science", "space travel"], top: 5,
    new() { AdditionalProperty = movie => movie.Title, Filter = movie => movie.Tag == "science" }))
{
    Console.WriteLine($"{hit.Record.Id}: {hit.Score}");
}
```

- Redis fuses rankings with reciprocal rank fusion (constant 60, window `Math.Max(20, Skip + top)`). The vector candidate count uses the same window, so a larger page can change ranking.
- **Scores are server fusion scores where higher is better**, unlike KNN distances.
- `ScoreThreshold` is a minimum fusion score applied after paging. It does not refill the page.
- Results are a union: a vector-only match can appear when no keyword matches.

## Exception behavior

| Failure | Thrown |
| --- | --- |
| Server or transport error | `VectorStoreException` with collection/operation metadata; the Respire exception is the cause |
| Malformed search score, document outside the collection, undecodable collection name | `VectorStoreException` wrapping `InvalidOperationException` |
| Incomplete `HGETALL` field/value pair | `InvalidOperationException` |
| Mapper error, vector or schema validation | Propagates unchanged (for example `ArgumentException`) |
| Unsupported filter or input | `NotSupportedException` |
| Missing `FT.HYBRID` | `NotSupportedException` |
| Cancellation | `OperationCanceledException`, not wrapped |

## NativeAOT

Use `GetHashCollection<TRecord>` or `GetJsonCollection<TRecord>` for trimming and NativeAOT. The upstream `VectorStore.GetCollection<TKey,TRecord>` carries `RequiresDynamicCode` and `RequiresUnreferencedCode`. The concrete methods use only your mapper code or generated JSON metadata, with no warning suppression. Hash scalar codecs stay your code; there is no generated or reflection-based VectorData mapper.

The sample runs every mapping path with reflection serialization disabled, on RESP2 and RESP3:

| Mapping path | Native smoke coverage |
| --- | --- |
| Explicit hash codec (`MovieMapper`, immutable record, binary FLOAT32) | CRUD, nullable scalar filters, filtered retrieval, filtered KNN and hybrid; `Memory<float>` and `ReadOnlyMemory<float>` query inputs |
| Generated JSON metadata (`JsonMovieMapper`, renamed nested data, multiple array vectors) | CRUD, replacement of optional properties, vector inclusion/omission, nested filters, filtered retrieval, selected-vector filtered KNN and hybrid; `float[]` query input |
| Generated nested JSON vector (`NestedVectorMapper`, renamed `Data.Values` path) | Batch CRUD, collection/store lifecycle, nested vector inclusion/omission, HNSW KNN and hybrid with nested text/vector selectors |

Publish and run the sample:

```bash
dotnet publish samples/Respire.Samples.VectorData -c Release -f net10.0 -r linux-x64 -o artifacts/vector-data
./artifacts/vector-data/Respire.Samples.VectorData redis://localhost:6379
```

The sample also targets .NET 8. Passing a runtime identifier enables NativeAOT on the executable only. Do not pass a global `PublishAot` property: it would also apply to the repository's netstandard analyzer project. See [Microsoft's NativeAOT guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) for platform prerequisites.

## Vector-set backend decision

`Respire.VectorData` has no `VADD`/`VSIM` vector-set backend, because native vector sets fail a durability requirement:

- On Redis **8.6.0** and **8.10.2**, acknowledged `VADD` replacements lose their new vector and attributes after an AOF crash/reload, even with `appendfsync always`. Wrapping `VADD` in Lua does not help.
- Lua `VREM` then `VADD` survives the reload, but a failed add can leave the old record deleted, with no rollback.
- Vector-set scores are cosine-based only: score `s` is cosine similarity `2*s-1` or cosine distance `2*(1-s)`. They cannot provide dot-product or L2 ranking.

These results apply to the tested versions. To check another version, run the disposable [durability probe](https://github.com/thomhurst/Respire/blob/main/scripts/Test-VectorSetDurability.ps1). Use [native vector-set commands](vector-sets.md) only with these limits understood.

## Conformance testing

The connector runs the official [VectorData conformance suite](https://github.com/dotnet/extensions/tree/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests) (package 10.10.0) for both hash and JSON backends on RESP2 and RESP3. It passes for the advertised API surface, not for every optional upstream capability. Excluded upstream contracts cover features the connector does not offer:

- Dynamic dictionary collections, non-string or generated keys, and models without vectors.
- Embedding generators and dependency-injection registration.
- `OrderBy` on filtered retrieval.
- Captured property getters in filters.
- Equality filters on `long`, `decimal`, `Guid`, date/time types and whole string arrays.
- Similarity scores, non-squared Euclidean, Hamming and Manhattan distance.

To run the suite locally:

```bash
dotnet run --project tests/Respire.VectorData.Conformance.Tests -c Release -f net10.0 -- --filter-not-trait 'Unsupported=*' --minimum-expected-tests 493 --fail-skips on
```

The fixture starts its own Redis 8.10 container. To use an existing standalone Redis with Query Engine, set `RESPIRE_VECTOR_CONFORMANCE_CONNECTION` to its `redis://` connection string.
