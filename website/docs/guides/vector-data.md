---
title: Microsoft.Extensions.VectorData
description: Store typed vector records in Redis hashes or JSON through explicit AOT-friendly mapping.
---

# Microsoft.Extensions.VectorData

`Respire.VectorData` implements `VectorStore` and string-keyed `VectorStoreCollection<string,TRecord>` from `Microsoft.Extensions.VectorData.Abstractions` 10.10.0 using Redis Query Engine hashes or RedisJSON documents. Both storage kinds support collection lifecycle, record CRUD, sequential batch operations, FLOAT32 KNN search and hybrid text/vector search. Hash collections also support expression filters and filtered retrieval. Use an unprefixed Respire client; Search commands reject client key prefixes. This connector supports standalone Redis Query Engine, included in standalone Redis 8; hybrid search requires the `FT.HYBRID` capability introduced in Redis 8.4. Redis Cluster is unsupported: collection deletion scans one node and cannot remove unindexed records across all shards, even when a search coordinator is available.

```bash
dotnet add package Respire.VectorData
```

## Vector-set backend decision

`Respire.VectorData` uses Query Engine hash and JSON collections. It does not expose
a `VADD`/`VSIM` collection backend. The [vector-set evaluation](https://github.com/thomhurst/Respire/issues/1261)
found that acknowledged replacements lose both their new vector and attributes after
an AOF crash/reload on Redis **8.6.0** and **8.10.2**, even with `appendfsync always`.
Wrapping `VADD` in Lua does not fix this. This blocks a direct native-upsert adapter.

Lua `VREM` followed by `VADD` survives that reload with compatible inputs, but a failed
add can leave the previous record deleted. Lua isolation provides no rollback. A
backend using this approach needs validation before removal and a defined recovery
protocol, with AOF, replica, concurrent-operation and failure-path tests before release.
These results apply to the evaluated server versions; run the disposable
[durability probe](https://github.com/thomhurst/Respire/blob/main/scripts/Test-VectorSetDurability.ps1)
against another version rather than assuming its behavior.

Native vector sets offer one vector per member and JSON attributes, with cosine
similarity scores. Their score `s` maps to cosine similarity `2*s-1` or cosine distance
`2*(1-s)`; relabeling it cannot supply dot-product or L2 ranking. A VectorData backend
also needs independent empty-collection metadata, an authoritative record codec,
schema validation, and a translator for the distinct vector-set filter language.
Optional capabilities can be restricted honestly, but successful writes must persist.
The evaluation does not establish VectorData conformance for a vector-set backend.
Use [native vector-set commands](vector-sets.md) only with their server semantics and
these durability limits understood.

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

## JSON storage

Choose hashes for explicit field codecs and binary vectors. Choose JSON for nested documents and generated `System.Text.Json` serialization. Derive from `RespireVectorDataJsonMapper<TRecord>` and pass a generated `JsonTypeInfo<TRecord>` to its constructor. There is no reflection fallback. The generated context must support both serialization and deserialization, and the model must deserialize with vector properties absent. Required vector properties are therefore unsuitable for the default retrieval behavior. Your `JsonPropertyName` attributes and serializer options determine the stored names and values, including nested data and omitted optional properties.

The runnable sample includes `JsonMovie`, `MovieJsonContext`, and `JsonMovieMapper`. Its title is stored at `$.details.movie_title`, and the CLR `Vector` property is stored at `$.embedding`. It also declares a second vector. Register the JSON mapper and choose the concrete JSON collection method:

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

JSON scalar schemas use a `RespireSearchField` whose identifier is a property path and whose alias is an explicit query name, for example `new("$.details.movie_title", RespireSearchFieldType.Text, Alias: "title")`. Each vector's `StorageName` is its query alias; `JsonPath` selects its stored property and defaults to `$.StorageName`. Paths select single object properties using `$.property.nested_property`. Property segments contain ASCII letters, digits, or underscores; array indices, wildcards, recursive selectors, and bracket syntax are unsupported. Aliases follow the hash name rules, including the reserved `vector_score` name. Duplicate paths or aliases fail before I/O.

Collection creation validates every JSON schema path against the supplied generated metadata before I/O. Paths must match serialized names, including naming policies and `JsonPropertyName` attributes, and each parent must expose object property metadata. Null or omitted optional vector values remain valid. For nested vector selection, set `PropertyName` to the full CLR member path, for example `Data.Values`, and select it with `VectorProperty = record => record.Data.Values`. Two vectors under the same parent use distinct paths such as `Data.First` and `Data.Second`.

JSON vectors are numeric arrays, while hash vectors are little-endian byte blobs. KNN query vectors use binary FLOAT32 values for both storage kinds. JSON writes validate dimensions, numeric representation, and finite FLOAT32 elements before dispatch. Missing or null vector properties remain unindexed. `IncludeVectors` applies to every declared vector, including nested vector paths; omitting vectors does not remove their sibling data. Retrieved records own their arrays and nested data.

JSON upsert uses a single script that replaces the entire root with `JSON.SET` and clears expiry. Omitted optional data and vectors disappear from the previous document. Collection deletion also removes records written before index creation, while preserving other collection and store namespaces. Batch, cancellation, distance, paging, and exception behavior match hashes. Register one storage mapping per record type per store; `GetHashCollection` and `GetJsonCollection` reject a mapper of the wrong storage kind. The upstream `GetCollection<string,TRecord>` method selects the registered kind automatically.

## Search behavior

Search accepts `float[]`, `Memory<float>` or `ReadOnlyMemory<float>`. Lower scores are better. COSINE returns cosine distance and L2 returns squared Euclidean distance. IP returns negative dot product: the connector subtracts 1 from Redis's `1 - dot(a,b)` score before applying `ScoreThreshold`. `Skip` and `top` select a page from the nearest `Skip + top` candidates. `ScoreThreshold` is a maximum distance applied after that page is selected; filtered hits are not replaced, so fewer than `top` records can be returned. Set `VectorProperty` to a mapped property expression when multiple vectors are declared; JSON mappings also accept nested member paths. Records deleted between search and retrieval are omitted without refilling the page; this is not a transactional snapshot. Search indexing can lag writes. Malformed search scores or documents outside the collection become `VectorStoreException` with an `InvalidOperationException` cause and collection/operation metadata.

This connector supports hashes, JSON, explicit typed mappings and capability-aware hybrid search. Embedding generation, dynamic dictionaries and non-string keys are unsupported. Expression filters and filtered retrieval currently support hashes only; JSON requests with filters throw before I/O. Unsupported filters and inputs are never silently ignored. Server and transport failures become `VectorStoreException` with the original Respire exception as their cause and collection/operation metadata. Cancellation stays `OperationCanceledException`. [The connector epic](https://github.com/thomhurst/Respire/issues/887) retains the remaining features and full official conformance suite. Existing tests include basic-model, multivector, hybrid and supported hash-filter contracts adapted from [the upstream conformance tests at the package source revision](https://github.com/dotnet/extensions/tree/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests), with the supported JSON contracts tested on RESP2 and RESP3.

The separate `Respire.VectorData.Conformance.Tests` project references the official, MIT-licensed `Microsoft.Extensions.VectorData.ConformanceTests` **10.10.0** package, matching the abstractions. Its package metadata pins source revision `02107c65bab30aad9e35b5133ed643eaa77bccd8`. CI runs **358** cases against real Redis Query Engine on RESP2 and RESP3: **260 hash cases**, **96 JSON lifecycle/backend cases** and **2 supplemental JSON cancellation cases**. The official runner targets `net10.0`, because this upstream package ships `net10.0` and `net472` assets. Connector correctness, cancellation/error tests and NativeAOT checks continue on both `net8.0` and `net10.0`.

### Hash coverage

| Official contract | Hash cases per protocol |
| --- | ---: |
| Collection lifecycle and metadata | 6 |
| Basic model CRUD, null/empty batches, filtered retrieval, vector search and paging | 28 |
| Model without data fields | 4 |
| Multiple vectors and ambiguous selection errors | 6 |
| Typed filters, Boolean logic, nullable constants, arrays/lists and captured fields | 54 |
| Cosine distance, negative dot product and squared Euclidean distance, including exact scores and thresholds | 3 |
| FLAT and HNSW index search | 2 |
| Hybrid search, filters, paging, multiple keywords/text fields and selection errors | 7 |
| Data type CRUD/null values, with supported equality filters | 16 |
| Raw FLOAT32 memory and array vectors | 2 |
| Explicit string keys | 1 |
| Unsupported string input without an embedding generator | 1 |

Lifecycle, model, filter, distance, index and hybrid assertions are inherited from the package unchanged. HNSW uses the upstream index test helper with `IndexKind.Hnsw`. The no-generator error case invokes the original official fact and checks its exact diagnostic prefix. The pinned upstream key/data/vector type methods combine typed, dynamic and embedding-generation steps without a typed-only fixture switch. Their sources in `tests/Respire.VectorData.Conformance.Tests/PinnedTyped` retain the original typed steps and assertions verbatim, with the mandatory unsupported dynamic/generation sections removed and the source revision recorded. The string-key case calls the upstream typed key helper. Data cases use the upstream `isFilterable` hook to keep every typed CRUD/null assertion while excluding only unsupported equality filtering. They cover byte, short, int, long, float, double, decimal, string, Boolean, Guid, DateTime, DateTimeOffset, DateOnly, TimeOnly, string arrays and nullable int. Fixture mappers use reflection only to adapt the upstream test records into explicit production hash mappings; this adds no reflection fallback to the shipped connector. JSON lifecycle fixtures continue to use source-generated metadata with reflection serialization disabled.

Run locally with `dotnet run --project tests/Respire.VectorData.Conformance.Tests -c Release -f net10.0 -- --filter-not-trait 'Unsupported=*' --minimum-expected-tests 358 --fail-skips on`. The exclusion trait marks unsupported contracts rather than skipped failures; every selected case must pass and zero skips are allowed. The fixture starts and disposes its own pinned Redis 8.10 container, including the `FT.HYBRID` capability. To use an existing standalone Redis Query Engine instance, set `RESPIRE_VECTOR_CONFORMANCE_CONNECTION` to its `redis://` connection string; the fixture does not stop that server. Each fixture isolates its namespace, deletes indexed and unindexed records, and disposes its collections, stores and client.

### Hash exclusions

These are capability exclusions, not claims of full upstream conformance. The source links below refer to the same pinned upstream revision; production guards are in the [store](https://github.com/thomhurst/Respire/blob/main/src/Respire.VectorData/RespireVectorStore.cs), [collection](https://github.com/thomhurst/Respire/blob/main/src/Respire.VectorData/RespireVectorStoreCollection.cs), [filter translator](https://github.com/thomhurst/Respire/blob/main/src/Respire.VectorData/RespireVectorDataFilter.cs) and [Search metric schema](https://github.com/thomhurst/Respire/blob/main/src/Respire.Search/RespireSearchSchema.cs).

| Excluded capability and exact upstream cases | Production and upstream evidence |
| --- | --- |
| Dynamic dictionaries: `DynamicModelTests`, dynamic sections of key/data/vector types and `FilterTests.Equal_with_string_sql_injection_in_name` | `RespireVectorStore.GetDynamicCollection` throws. [FilterTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/FilterTests.cs) supports `TestDynamic=false`; the injection-in-name fact contains only a dynamic branch. The typed assertions still run. |
| Non-string keys, generated keys and `KeyTypeTests.MismatchedKeyTypeThrows` | `GetCollection` rejects every non-string `TKey`; keys come from the explicit mapper without generation. [KeyTypeTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/TypeTests/KeyTypeTests.cs) uses Guid for both its Guid fact and its mismatched-key fact. String CRUD/delete assertions from its protected helper still run. |
| Models without vectors: `NoVectorModelTests` | The collection constructor requires at least one vector. [NoVectorModelTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/ModelTests/NoVectorModelTests.cs) explicitly applies only to databases supporting vectorless models. |
| Filtered ordering: the three `BasicModelTests.GetAsync_with_filter_and_*OrderBy*` facts | Filtered `GetAsync` rejects `OrderBy`. [BasicModelTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/ModelTests/BasicModelTests.cs) requests ordering in these facts; unordered filtered retrieval still runs. |
| Captured property getters: `FilterTests.Equal_with_string_containing_special_characters` | The filter translator's `Value` accepts captured fields and rejects getters/method execution. This [upstream fact](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/FilterTests.cs#L58) captures `fixture.SpecialCharactersText` as a property getter. Existing local tests cover special-character literals/captured fields and prove getters never execute. |
| Equality filters for long, decimal, Guid, date/time data, and whole string arrays | `NumericType` supports byte/sbyte/short/ushort/int/uint/float/double; string collections support membership only. [DataTypeTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/TypeTests/DataTypeTests.cs) exposes `isFilterable`; all 16 data CRUD cases still run, and supported types retain equality/null filtering assertions. |
| `EmbeddingTypeTests.Embedding_of_float`, non-FLOAT32 inputs and all generated-embedding steps | `SearchAsync` accepts only raw FLOAT32 array/memory input. [EmbeddingTypeTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/TypeTests/EmbeddingTypeTests.cs) combines raw vectors with mandatory dynamic/generated steps; only those sections are removed for raw FLOAT32 tests. |
| Generator/DI contracts in `EmbeddingGenerationTests`, except `SearchAsync_string_without_generator_throws`; `DependencyInjectionTests` | The connector has no generator option or VectorData DI registration API. [EmbeddingGenerationTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/EmbeddingGenerationTests.cs) requires these APIs for generator tests; its applicable no-generator error assertion runs unchanged. [DependencyInjectionTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/DependencyInjectionTests.cs) requires registration delegates. |
| `DistanceFunctionTests.CosineSimilarity`, `DotProductSimilarity`, `EuclideanDistance`, `HammingDistance`, `ManhattanDistance` | COSINE is cosine distance and L2 is squared Euclidean distance; the connector subtracts 1 from Redis's IP score to return negative dot product. [DistanceFunctionTests](https://github.com/dotnet/extensions/blob/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests/DistanceFunctionTests.cs) expects different scores for these five functions. Supported distance assertions retain `AssertScores=true` and score-threshold checks. |

The upstream `TestSuiteImplementationTests` checks the conformance library itself rather than a provider. This pinned revision has no provider cancellation facts; existing hash/filter/hybrid cancellation and server-error contracts run in `Respire.VectorData.Tests` on both frameworks. The complete coverage/AOT matrix remains in [#1392](https://github.com/thomhurst/Respire/issues/1392). [The conformance parent](https://github.com/thomhurst/Respire/issues/1262) and [connector epic](https://github.com/thomhurst/Respire/issues/887) remain open.

### JSON coverage

The JSON backend also executes **84** cases directly from the official package: applicable basic-model CRUD, null argument errors, missing records, vector inclusion, search and paging; all no-data and multivector model contracts; cosine distance, negative dot product and squared Euclidean scores with score thresholds; Flat and the upstream index helper with HNSW; and all six unfiltered hybrid contracts, including multiple text properties and the missing-property error. The adapter invokes the packaged methods and their assertions unchanged. Every case gets a fresh fixture seeded by upstream model definitions. The runner uses reflection only to discover and invoke test methods; production JSON serialization uses explicit generated metadata. Two supplemental connector cases cover pre-cancellation on 14 collection/store operations and verify that records remain intact. Upstream 10.10.0 contains no collection cancellation facts. CI rejects skips in every selected case.

JSON exclusions are explicit in `JsonBackendTests.Cases` and its `BasicExclusions` list. The following boundaries link production behavior to the pinned upstream contracts:

| Excluded upstream contract | Production boundary and upstream evidence |
| --- | --- |
| `FilterTests`, basic filtered retrieval/search and ordering, hybrid `HybridSearchAsync_with_filter` | `RespireVectorStoreCollection.GetAsync(filter, ...)`, `SearchAsync` and `HybridSearchAsync` reject JSON expression filters. The upstream methods require those filters. The basic empty-upsert, empty-delete and missing-delete facts also count records using filtered retrieval, so their complete upstream methods cannot execute against JSON. Connector-specific null/empty-batch tests remain in `JsonValidationTests`. |
| `KeyTypeTests`, `EmbeddingTypeTests`, `DataTypeTests` | Their complete test helpers require dynamic dictionary collections in addition to typed CRUD; embedding tests also require expression filters and embedding generators. `RespireVectorStore.GetDynamicCollection` rejects dynamic collections, `GetCollection<TKey,TRecord>` supports only string keys, and `SearchAsync` accepts FLOAT32 arrays/memory rather than embedding generators. No helper is truncated and no unsupported fact is reported as passing. String keys, `ReadOnlyMemory<float>` and integer/string data run through the official model suites; broader JSON serializer and array-vector behavior remains connector-specific coverage. |
| `DynamicModelTests`, `NoVectorModelTests`, `EmbeddingGenerationTests` | The store rejects dynamic collections; the collection constructor requires at least one vector; search rejects automatic embedding generation. These upstream suites require those capabilities. |
| Similarity scores, nonsquared Euclidean distance, Hamming and Manhattan distance | `RespireVectorDataVectorField.DistanceMetric` exposes Redis COSINE, IP and L2 only. Applicable official score contracts are cosine distance, negative dot product and squared Euclidean distance; assertions and score thresholds run for all three. Similarity transforms, square-root conversion, Hamming and Manhattan are not exposed. |
| `DependencyInjectionTests`, `TestSuiteImplementationTests` | The connector exposes explicit store construction and mapper registration, without upstream DI registration delegates. `TestSuiteImplementationTests` validates the upstream test infrastructure itself, not a provider backend. |

The [pinned upstream source](https://github.com/dotnet/extensions/tree/02107c65bab30aad9e35b5133ed643eaa77bccd8/src/Libraries/Microsoft.Extensions.VectorData.ConformanceTests) contains the complete methods and helpers named above. This JSON slice does not establish complete upstream conformance. [The conformance parent](https://github.com/thomhurst/Respire/issues/1262) remains open for the [coverage/AOT matrix](https://github.com/thomhurst/Respire/issues/1392).

## Expression filters

Declare `FilterFields` on your hash mapper with explicit CLR property names, unique hash storage names and `RespireVectorDataFilterKind` values. These add their own schema fields; do not repeat their storage names in `DataFields`. Existing indexes must be recreated when adding filter fields. Filtering requires Redis Query Engine 2.10 or later (included in Redis 8) for `INDEXMISSING`. JSON mappings do not expose these hash filter fields.

The sample `MovieMapper` declares ordinal string filters for `Title` and `Tag`, stored in `filter_title` and `filter_tag`. Write string fields with `RespireVectorDataFilterEncoding.EncodeTag` and collections with `EncodeTags`, then encode the returned text as UTF-8 hash bytes. Decode with `DecodeTag` in `Read`, or keep separate original fields as the sample does. Encoding preserves case, empty strings, whitespace, separators and Unicode, and prevents values from introducing query syntax. Raw TAG fields are unsuitable for exact CLR string semantics because Redis splits separators and trims whitespace. The connector validates canonical encoded filter tokens before upsert.

Write Boolean filter fields as invariant `0` or `1` and numeric fields using `RespireVectorDataFilterEncoding.EncodeNumber`. This preserves the exact double representation, including lossless promotion of `float` values; formatting a float directly can change its indexed boundary. Write every nonnullable scalar property. Omit fields for null scalar values; explicit null hash bytes are unsupported. Missing fields represent null, while encoded empty strings remain present. String collections support nonnull string elements; an omitted or empty collection has no membership matches. Record mapping remains explicit and does not use runtime reflection.

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

Supported operators:

- Direct mapped string properties: ordinal `==` and `!=`, including null.
- Mapped `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `float`, `double` and nullable forms: `==`, `!=`, `<`, `<=`, `>`, `>=`. Bounds preserve exclusive/inclusive semantics. Values must be finite. Missing nullable fields fail relational comparisons and match inequality against nonnull values.
- Boolean properties and nullable Boolean equality: `==`, `!=`, direct Boolean predicates and `!`.
- Boolean combinations: `&&`, `||`, parentheses, `!` and constant `true`/`false`.
- Mapped `string[]` and `List<string>`: `Contains(value)` and `Any(element => values.Contains(element))`. Inline/captured arrays and supported `List<T>` values can also contain a mapped scalar property. `Enumerable.Contains` and array-backed `MemoryExtensions.Contains` are supported, including an explicit null comparer.
- Literal constants, initialized arrays, `Array.Empty<T>()`, captured locals and field chains (including static fields). Captured nullable `.Value` is supported and throws when null. Expressions are visited without compilation; captured fields are read from metadata already rooted by the expression.

Unsupported expressions throw `NotSupportedException` before a request: unregistered or nested record properties, property getters for captured values, arbitrary method calls, arithmetic, property-to-property comparisons, custom comparers, string substring/prefix matching, collection equality, `long`, `ulong`, `decimal`, enums, dates and conversions that change numeric semantics. Nullable record `.Value` access is unsupported; compare the nullable property directly. Only nullable lifting, small-integer promotion and lossless promotion to double are accepted as conversions.

Filters apply before KNN selects the nearest `Skip + top` candidates and work with the selected `VectorProperty`. Filtered retrieval uses `Skip`/`top` without vector ranking; result order is unspecified and `OrderBy` is unsupported. Vector inclusion, cancellation and records deleted between searching and hash retrieval behave as described above.

## Hybrid search

Collections implement `IKeywordHybridSearchable<TRecord>` for Redis Query Engine [`FT.HYBRID`](https://redis.io/docs/latest/commands/ft.hybrid/), available in Redis Open Source 8.4 or later with Redis Search. The connector checks the server's command table with `COMMAND INFO FT.HYBRID` when hybrid execution fails. A missing command throws `NotSupportedException` naming the required capability; Redis version strings alone do not establish support. When an ACL or proxy denies capability inspection, only an unknown-command error naming `FT.HYBRID` produces that diagnostic. Permission, index and query errors retain their database failure diagnostics. There is no client-side rank fusion fallback.

Declare `TextFields` on either mapper to bind CLR property names to existing indexed TEXT fields in `DataFields`. Each binding must name a unique CLR property and a unique field; JSON bindings use the query alias and may use dotted CLR paths. Fields with `NoIndex` cannot serve as hybrid text fields. The sample hash mapper binds `Title` to `title`; the JSON mapper binds `Details.Title` to the `title` alias. This metadata avoids property-name guessing and runtime reflection.

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

Pass an already generated FLOAT32 vector and one or more nonblank keywords. Keywords are escaped quoted terms or phrases, joined with OR within the selected text field. They cannot inject native query syntax. Select `AdditionalProperty` when multiple text bindings exist, and `VectorProperty` when multiple vectors exist. Hash expression filters apply before ranking to both text and vector legs; JSON expression filters remain unsupported.

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

Redis fuses the text and vector rankings using reciprocal rank fusion (RRF), with constant 60 and a window of `Math.Max(20, Skip + top)`. The vector candidate count uses the same window. Returned scores are the unmodified server fusion scores, where higher is better. `ScoreThreshold` is a minimum fusion score applied after selecting the `Skip`/`top` page; it does not refill the page. These scores differ from KNN vector distances, where lower is better. Increasing the requested window can change fusion ranking. Text matches and vector matches form a union, so a vector-only match can still appear when no keyword matches.

`IncludeVectors`, cancellation, batched retrieval and records removed between search and retrieval behave as for KNN search. Tests cover the applicable pinned upstream hybrid contracts, text selection, multi-keyword ranking, hash filters, JSON nested text and selected vectors, scores and paging on real Redis 8.4 and 8.10 using RESP2 and RESP3, plus the missing capability on Redis 8.2.

## Exception behavior

The exception contract distinguishes database failures from application validation. Mapper exceptions and vector/schema validation errors propagate unchanged, including `ArgumentException`. An incomplete `HGETALL` field/value pair throws `InvalidOperationException`. The search and collection-name decoding failures described above are wrapped with operation metadata. Cancellation stays `OperationCanceledException`.

## NativeAOT

Use the concrete `GetHashCollection<TRecord>` or `GetJsonCollection<TRecord>` method for trimming and NativeAOT. The upstream `VectorStore.GetCollection<TKey,TRecord>` method carries `RequiresDynamicCode` and `RequiresUnreferencedCode` annotations, which overrides must preserve. The safe concrete methods use only registered mapper code or explicitly supplied JSON metadata. No warning suppression or reflection fallback is needed. The sample disables reflection serialization and exercises both storage kinds, renamed nested JSON data, vector omission, and KNN search.

Publish the sample with its local NativeAOT setting:

```bash
dotnet publish samples/Respire.Samples.VectorData -c Release -f net10.0 -r linux-x64 -o artifacts/vector-data
./artifacts/vector-data/Respire.Samples.VectorData redis://localhost:6379
```

The sample also supports .NET 8. A runtime identifier enables its NativeAOT property only on the executable; passing a global `PublishAot` property would incorrectly apply it to the repository's netstandard analyzer project. Install the platform prerequisites from [Microsoft's NativeAOT guide](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). CI publishes and runs the native sample against Redis for both frameworks.
