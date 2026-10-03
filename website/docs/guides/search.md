---
title: Redis Search
---

`Respire.Search` provides typed index definitions, query options, ordered aggregation pipelines with cursor paging, vector KNN requests, and hybrid queries. Install it alongside `Respire`:

```bash
dotnet add package Respire.Search
```

With C# 14 or later, import the namespace shown below and use `client.Search` on
`RespireClient` or `IRespireClient`. The property reuses one wrapper per client instance,
performs no network I/O, and leaves ownership of the underlying client with you.
Key-prefixed views get their own wrapper and retain the module's prefix restrictions.
The existing `new RespireSearchClient(client)` constructor remains available.

The package, assembly, and root namespace are all `Respire.Search`. Search is built into Redis Open Source 8 and later; older deployments need Redis Stack or the RediSearch module. Installing the client package does not add server capabilities.

```csharp
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;

await search.CreateIndexAsync("books", new RespireSearchIndexDefinition
{
    Prefixes = ["book:"],
    Fields =
    [
        new("title", RespireSearchFieldType.Text, Sortable: true) { Weight = 2 },
        new("category", RespireSearchFieldType.Tag) { Separator = ',' },
        new("year", RespireSearchFieldType.Numeric, Sortable: true),
        new("embedding", RespireSearchFieldType.Vector)
        {
            Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 3, RespireSearchDistanceMetric.Cosine),
        },
    ],
});

var expression = RespireSearchQueryBuilder.And(
    RespireSearchQueryBuilder.TextField("title", "redis search"),
    RespireSearchQueryBuilder.Tag("category", "database"),
    RespireSearchQueryBuilder.NumericRange("year", 2020, 2026));
var found = await search.SearchAsync("books", new RespireSearchQuery(expression,
    new RespireSearchQueryOptions { Limit = (0, 20), ReturnFields = ["title", "year"] }));

var groups = await search.AggregateAsync("books", RespireSearchExpression.FromRaw("*"), new RespireSearchAggregateOptions
{
    Stages =
    [
        RespireSearchAggregateStage.GroupBy(["@category"], new RespireSearchReducer("COUNT", [], "count")),
        RespireSearchAggregateStage.Filter("@count > 1"),
        RespireSearchAggregateStage.SortBy(new RespireSearchAggregateSort("@count", RespireSearchSortDirection.Descending)),
    ],
});

var nearest = await search.VectorSearchAsync("books",
    new RespireVectorSearchRequest("embedding", new byte[] { 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0 }, 10)
    {
        Filter = RespireSearchQueryBuilder.Tag("category", "database"),
    });

var hybrid = await search.HybridSearchAsync("books", new RespireHybridSearchQuery(
    RespireSearchExpression.FromRaw("@title:$term"), "embedding", new byte[] { 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0 }, 10)
{
    Parameters = new Dictionary<string, RespireValue> { ["term"] = "redis" },
    RrfWindow = 50,
    TimeoutMilliseconds = 1_000,
    LoadFields = ["title", "category"],
});
```

## Schema

Text fields accept `Weight` and `NoStem`. Tag fields accept `Separator` and `CaseSensitive`. Vector fields take a typed `RespireSearchVectorOptions` (algorithm, element type, dimensions, and distance metric). The client computes the algorithm argument count, and `Attributes` adds settings such as `M` or `EF_CONSTRUCTION`. `RespireSearchField.Options` remains a raw-token escape hatch for server-version-specific settings that have no typed property. A vector field uses either typed `Vector` options or raw `Options`, not both. Invalid combinations, such as `Weight` on a tag field or `Sortable` on a vector field, throw before anything is sent.

## Queries

Supported index commands are `FT.CREATE`, `FT.ALTER`, `FT.DROPINDEX`, and `FT.INFO`. `GetIndexInfoAsync` returns a parsed `RespireSearchIndexInfo` with the index name, document count, schema attributes, and every reported property as a copied value. Query methods use `FT.SEARCH`, `FT.EXPLAIN`, and `FT.EXPLAINCLI`; `ExplainAsync` takes `RespireSearchExplainOptions` to select CLI output or a dialect.

`RespireSearchQueryOptions` supports projections, scores, sorting, limits, named parameters, timeout, and dialect. Named parameters need an explicit dialect of 2 or later. `RespireSearchResult` contains the total count, document IDs, fields, scores, and any warnings the server returns.

Query helpers escape field names, tag values, and quoted text, so values passed to them cannot change the query. They build exact text, tag, numeric range (inclusive or exclusive, with infinite bounds), AND, and OR expressions. For prefix, fuzzy, wildcard, and other advanced syntax, wrap trusted native query text with `RespireSearchExpression.FromRaw`.

### Untrusted input

Query syntax is represented by `RespireSearchExpression`. Builder helpers escape values and return this type. `And` and `Or` accept typed expressions. `FromRaw` explicitly marks trusted native syntax, such as `@title:$term`. Query, vector filter, hybrid text, explain, and aggregate query entry points require this type, so plain strings cannot enter those calls implicitly. Pass untrusted values through a helper such as `Tag`, `TextField`, or `NumericRange`, or reference them as `$name` parameters in `Parameters` (dialect 2 or later). Aggregation `FILTER` and `APPLY` use a separate expression language and remain raw strings.

## Aggregation

`AggregateAsync` uses `FT.AGGREGATE`. `RespireSearchAggregateOptions.Stages` is an ordered pipeline that is sent exactly as written. Use it when a FILTER needs an alias from an earlier APPLY, or when a LIMIT must run before GROUPBY. The factory methods on `RespireSearchAggregateStage` create LOAD, FILTER, APPLY, GROUPBY with REDUCE, SORTBY (multiple keys and an optional `Max`), and LIMIT stages. An empty GROUPBY property list groups every row together. Rows expose string values by name, and `StructuredRows` preserves nested values from reducers such as `TOLIST`.

For large results, `AggregatePagesAsync` runs the aggregation with `WITHCURSOR` and returns an `IAsyncEnumerable` of pages, reading the next page only when you ask for it. If you stop early with `break`, an exception, or cancellation, it deletes the server cursor for you. For manual control, `AggregateWithCursorAsync` returns the first page. Each page records its index, so pass the page to `ReadCursorAsync` until `IsComplete` is true, or release the cursor early with `DeleteCursorAsync`. Overloads that take an index name and cursor ID are also available.

## Vector and hybrid queries

`VectorSearchAsync` emits FT.SEARCH KNN syntax with dialect 2 and a binary `$vector` parameter. Set `Filter` on the request for a pre-filtered KNN query such as `(@category:{database})=>[KNN ...]`. The request is validated when it is created. By default it returns up to `K` documents sorted by score; explicit `Limit` and `SortBy` options are kept. The parameter name `vector` is reserved, and supplying it in `Parameters` throws. Vector queries need RediSearch 2.4 or later.

`HybridSearchAsync` emits FT.HYBRID text and vector search with reciprocal-rank fusion. Set `RrfWindow`, `Parameters`, `TimeoutMilliseconds`, and `LoadFields` as needed. FT.HYBRID requires Redis Open Source 8.4.0 or later; on an older server that does not recognize the command, `HybridSearchAsync` throws `NotSupportedException`. When FT.HYBRID returns an error, Respire checks the server's command table with `COMMAND INFO FT.HYBRID` instead of relying on the error wording, and falls back to the wording only when `COMMAND INFO` is denied. Search features vary by server version; check [FT.CREATE](https://redis.io/docs/latest/commands/ft.create/), [FT.SEARCH](https://redis.io/docs/latest/commands/ft.search/), [FT.AGGREGATE](https://redis.io/docs/latest/commands/ft.aggregate/), and [FT.HYBRID](https://redis.io/docs/latest/commands/ft.hybrid/) for supported features.

`RespireSearchDocument.Fields` and aggregate `Rows` provide string views for convenient text results. `StructuredFields` and `StructuredRows` preserve RESP types, nested values, and copied raw bytes for binary fields. Replies with an unexpected shape throw `InvalidOperationException` rather than silently dropping data.

## Routing, caching, and ownership

FT.* commands carry an index name instead of keys. On a Redis Cluster, Respire routes each Search command, including cursor reads, to the node that owns the index name's hash slot. Respire does not fan out queries or merge shard results. Cross-shard search relies on the server's search coordinator, so check that your cluster deployment provides one. Without it, for example in plain Redis Open Source cluster mode, a query only sees the documents on the node that receives it, and Respire cannot tell that the result is partial. Standalone and Sentinel deployments need no special handling.

With client-side caching enabled, read-only Search commands leave the local cache intact, while index changes invalidate it conservatively. Key-prefixed views reject Search commands, so include prefixes in the indexed keyspace and use keys in the format your index expects. Search methods build one argument list per call and are not part of Respire's zero-allocation hot path. The caller owns the underlying client.

The package reuses Respire's generated command infrastructure, and a Native AOT smoke app covers it against Redis 8.4 over RESP2 and RESP3.
