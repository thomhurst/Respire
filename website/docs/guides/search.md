---
title: Redis Search
---

`Respire.Search` provides typed index definitions, query options, aggregation stages, vector KNN requests, and hybrid queries. Install it alongside `Respire`:

```bash
dotnet add package Respire.Search
```

This package provides the client API. Your Redis server must also provide the Redis Search module.

```csharp
using Redis.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = new RespireSearchClient(client);

await search.CreateIndexAsync("books", new RespireSearchIndexDefinition
{
    Prefixes = ["book:"],
    Fields =
    [
        new("title", RespireSearchFieldType.Text, Sortable: true),
        new("category", RespireSearchFieldType.Tag),
        new("year", RespireSearchFieldType.Numeric, Sortable: true),
        new("embedding", RespireSearchFieldType.Vector, Options: ["FLAT", "6", "TYPE", "FLOAT32", "DIM", "3", "DISTANCE_METRIC", "COSINE"]),
    ],
});

var expression = RespireSearchQueryBuilder.And(
    RespireSearchQueryBuilder.TextField("title", "redis search"),
    RespireSearchQueryBuilder.Tag("category", "database"));
var found = await search.SearchAsync("books", new RespireSearchQuery(expression,
    new RespireSearchQueryOptions { Limit = (0, 20), ReturnFields = ["title", "year"] }));

var groups = await search.AggregateAsync("books", "*", new RespireSearchAggregateOptions
{
    Groups = [new(["@category"], [new("COUNT", [], "count")])],
    SortBy = [new("@count", RespireSearchSortDirection.Descending)],
});

var nearest = await search.VectorSearchAsync("books",
    new RespireVectorSearchRequest("embedding", new byte[] { 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0 }, 10));

var hybrid = await search.HybridSearchAsync("books", new RespireHybridSearchQuery(
    "@title:$term", "embedding", new byte[] { 0, 0, 0, 0, 0, 0, 128, 63, 0, 0, 0, 0 }, 10)
{
    Parameters = new Dictionary<string, RespireValue> { ["term"] = "redis" },
    RrfWindow = 50,
    TimeoutMilliseconds = 1_000,
    LoadFields = ["title", "category"],
});
```

Use `RespireSearchField.Options` for field-specific schema settings. Vector schema options contain the RediSearch algorithm and its arguments. Query helpers build common text, exact tag, numeric range, AND, and OR expressions; pass raw server query syntax for features outside these helpers.

Supported index commands are `FT.CREATE`, `FT.ALTER`, `FT.DROPINDEX`, and `FT.INFO`. Query methods use `FT.SEARCH`, `FT.EXPLAIN`, and `FT.EXPLAINCLI`. `RespireSearchQueryOptions` supports projections, scores, sorting, limits, named parameters, timeout, and dialect. `RespireSearchResult` contains total count, document IDs, fields, scores, and warnings where the server returns them. `AggregateAsync` uses `FT.AGGREGATE` and supports LOAD, FILTER, APPLY, GROUPBY with REDUCE, SORTBY, LIMIT, and DIALECT; rows expose string values by name.

`VectorSearchAsync` emits FT.SEARCH KNN syntax with dialect 2 and a binary `$vector` parameter. It returns up to `K` documents by default; set `Limit` to override. Vector queries need RediSearch 2.4 or later. `HybridSearchAsync` emits FT.HYBRID text + vector search with reciprocal-rank fusion. Set `RrfWindow`, `Parameters`, `TimeoutMilliseconds`, and `LoadFields` as needed. FT.HYBRID requires Redis Open Source 8.4.0 or later. Redis Search and RediSearch module features vary by server version; check [FT.CREATE](https://redis.io/docs/latest/commands/ft.create/), [FT.SEARCH](https://redis.io/docs/latest/commands/ft.search/), [FT.AGGREGATE](https://redis.io/docs/latest/commands/ft.aggregate/), and [FT.HYBRID](https://redis.io/docs/latest/commands/ft.hybrid/) for supported features.

`RespireSearchDocument.Fields` and aggregate `Rows` provide string views for convenient text results. `StructuredFields` and `StructuredRows` preserve RESP types, nested values, and copied raw bytes for binary fields. `ExplainAsync` accepts an optional dialect for dialect-specific expressions.

The package reuses Respire's generated command infrastructure. Generated module commands preserve cancellation and conservative routing/cache behavior and are covered by the Native AOT smoke app against Redis 8.4 with RESP2 and RESP3. They do not apply `WithKeyPrefix`; include prefixes in the indexed keyspace and use keys with the format expected by your server. The caller owns the underlying client. Use `GetIndexInfoAsync` to inspect the server's raw FT.INFO response.
