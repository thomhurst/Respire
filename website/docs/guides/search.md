---
title: Redis Search
---

`Respire.Search` is a typed client for Redis Search: index definitions, queries, aggregation pipelines with cursor paging, vector KNN and hybrid queries. Use it to query hashes or JSON documents by field, text or vector.

```bash
dotnet add package Respire.Search
```

Requirements and setup:

- Search is built into Redis Open Source 8 and later. Older deployments need Redis Stack or the RediSearch module.
- With C# 14 or later, use `client.Search` on `RespireClient` or `IRespireClient` (namespace `Respire.Search`). It does no I/O, reuses one wrapper per client and leaves client ownership with you. On earlier C# versions, use `new RespireSearchClient(client)`.
- Key-prefixed views from `WithKeyPrefix` reject all Search commands, because index names are not keys. Put the prefix in the index definition's `Prefixes` instead.

```csharp
using Respire;
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

| Field type | Options |
| --- | --- |
| Text | `Weight`, `NoStem`, `Phonetic` (English, French, Portuguese or Spanish double metaphone) |
| Tag | `Separator`, `CaseSensitive` |
| Vector | Typed `RespireSearchVectorOptions` (algorithm, element type, dimensions, distance metric); `Attributes` for settings such as `M` or `EF_CONSTRUCTION` |

- `RespireSearchField.Options` is a raw-token escape hatch for server-specific settings. A vector field uses either typed `Vector` or raw `Options`, not both.
- Invalid combinations, such as `Weight` on a tag or `Sortable` on a vector, throw before anything is sent.
- Index definitions accept `Language`, `LanguageField`, `Score` (0–1), `TemporarySeconds` and `SkipInitialScan`.
- `StopWords = null` uses server defaults; an empty list sends `STOPWORDS 0` and disables them.

**Temporary index expiry deletes its indexed documents as well as the index.** See [FT.CREATE](https://redis.io/docs/latest/commands/ft.create/).

Other index commands: `FT.ALTER`, `FT.DROPINDEX` and `FT.INFO`. `GetIndexInfoAsync` returns a `RespireSearchIndexInfo` with the name, document count, schema attributes and every reported property.

## Queries

`RespireSearchQueryOptions` covers projections, scores, sorting, limits, named parameters, timeout and dialect. Named parameters need dialect 2 or later. `RespireSearchResult` holds the total, document IDs, fields, scores and server warnings. `ExplainAsync` (`FT.EXPLAIN`/`FT.EXPLAINCLI`) takes `RespireSearchExplainOptions` for CLI output or a dialect.

Query helpers build exact text, tag, numeric range (inclusive or exclusive, infinite bounds allowed), AND and OR expressions, escaping field names and values. For prefix, fuzzy, wildcard and other syntax, wrap trusted query text in `RespireSearchExpression.FromRaw`.

### Untrusted input

Query, vector filter, hybrid text, explain and aggregate query parameters take `RespireSearchExpression`, so plain strings cannot reach them by accident. Builder helpers return this type; `FromRaw` marks trusted native syntax such as `@title:$term`.

**Pass untrusted values through a helper** (`Tag`, `TextField`, `NumericRange`) or as `$name` entries in `Parameters` (dialect 2 or later). Aggregation `FILTER` and `APPLY` use a separate expression language and stay raw strings, so never put untrusted text in them.

## Query options and returned text

More `RespireSearchQueryOptions`: binary-safe `InKeys`, text `InFields`, `Slop`, `InOrder`, `Language`, `Scorer`, `Verbatim` and `NoStopWords`. Empty key and field lists leave the search unrestricted. The server validates languages and scorer names (built-in or server-registered).

Highlighting and summaries:

- `Highlight` takes `RespireSearchHighlightOptions` with optional `(Open, Close)` `Tags`.
- `Summarize` takes `RespireSearchSummaryOptions` with positive `Fragments` and `Length` and an optional `Separator`.
- Both accept `Fields`; an empty list selects all returned text fields.
- `NoContent` cannot be combined with either.
- **Highlight markup is not HTML-escaped.** Escape or sanitize it for your rendering context.

`TextResults` returns `RespireSearchTextResult` values with `Text`, `HighlightRequested` and `SummaryRequested`. The flags reflect the request, not whether the server changed the value. With default field selection this view includes scalar fields too; explicit `Fields` limits it. Summaries are kept as whole strings, separators included.

Reply metadata (works with `NoContent`, and with normal, vector and profiled searches):

- `ExplainScore` requires `WithScores`. The explanation is in `ScoreExplanation`; `Score` stays numeric.
- `WithPayloads` returns binary `Payload`, keeping null distinct from empty. Modern indexes normally return null.
- `WithSortKeys` returns the encoded `SortKey`, such as `#5` for a numeric value, or null without a sortable value.

See [FT.SEARCH](https://redis.io/docs/latest/commands/ft.search/).

## Aggregation

`AggregateAsync` uses `FT.AGGREGATE`. `RespireSearchAggregateOptions.Stages` is an ordered pipeline sent exactly as written, so a FILTER can use an earlier APPLY alias and a LIMIT can run before GROUPBY.

- `RespireSearchAggregateStage` creates LOAD, FILTER, APPLY, GROUPBY with REDUCE, SORTBY (multiple keys, optional `Max`) and LIMIT stages.
- An empty GROUPBY property list groups all rows together.
- `Rows` gives string values by name. `StructuredRows` keeps nested values from reducers such as `TOLIST`.

Cursor paging:

- `AggregatePagesAsync` uses `WITHCURSOR` and returns an `IAsyncEnumerable` of pages, fetching each on demand. Stopping early (break, exception, cancellation) deletes the server cursor.
- For manual control, `AggregateWithCursorAsync` returns the first page. Pass each page to `ReadCursorAsync` until `IsComplete`, or call `DeleteCursorAsync` to stop. Overloads take an index name and cursor ID.

## Collecting documents within aggregate groups

`RespireSearchReducer.Collect` exposes Redis 8.10's `COLLECT` reducer. Pass `RespireSearchCollectOptions` with nonempty `Fields` or `AllFields`, optional `Distinct`, `SortBy` and `Limit`, and an optional alias. Names get an `@` prefix automatically.

- `AllFields` projects fields already loaded by `LOAD` or earlier stages; it does not load whole documents.
- Entries are in `StructuredRows[row][alias].Items`: field/value arrays on RESP2, maps on RESP3 (whose `Items` alternate keys and values).

See [FT.AGGREGATE](https://redis.io/docs/latest/commands/ft.aggregate/).

## Vector and hybrid queries

`VectorSearchAsync` sends `FT.SEARCH` KNN syntax with dialect 2 and a binary `$vector` parameter. Requires RediSearch 2.4 or later.

- Set `Filter` for a pre-filtered query such as `(@category:{database})=>[KNN ...]`.
- By default it returns up to `K` documents sorted by score; explicit `Limit` and `SortBy` are kept.
- `vector` is a reserved parameter name; supplying it in `Parameters` throws.

`HybridSearchAsync` sends `FT.HYBRID` (text plus vector search, reciprocal rank fusion). Set `RrfWindow`, `Parameters`, `TimeoutMilliseconds` and `LoadFields` as needed. It requires Redis Open Source 8.4.0 or later; servers without the command make it throw `NotSupportedException`.

Results:

- `RespireSearchDocument.Fields` and aggregate `Rows` are string views.
- `StructuredFields` and `StructuredRows` keep RESP types, nested values and raw bytes for binary fields.
- Unexpected reply shapes throw `InvalidOperationException` rather than dropping data.

Feature support varies by server version; see [FT.HYBRID](https://redis.io/docs/latest/commands/ft.hybrid/).

## Spelling dictionaries and corrections

Spelling dictionaries are separate from autocomplete dictionaries and need no index.

| Method | Command | Returns |
| --- | --- | --- |
| `AddDictionaryTermsAsync` | `FT.DICTADD` | Number of terms inserted |
| `DeleteDictionaryTermsAsync` | `FT.DICTDEL` | Number of terms removed |
| `DumpDictionaryAsync` | `FT.DICTDUMP` | Terms in unspecified order; empty if absent |
| `SpellCheckAsync` | `FT.SPELLCHECK` | Each query term with scored suggestions (possibly empty) |

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;
await search.AddDictionaryTermsAsync("book-vocabulary", ["redis", "database"]);
var terms = await search.DumpDictionaryAsync("book-vocabulary");

// The books index must already exist.
var corrections = await search.SpellCheckAsync("books", "reids", new()
{
    Distance = 2,
    IncludeDictionaries = ["book-vocabulary"],
    Dialect = 2,
});
foreach (var correction in corrections)
foreach (var suggestion in correction.Suggestions)
    Console.WriteLine($"{correction.Term}: {suggestion.Term} ({suggestion.Score})");

await search.DeleteDictionaryTermsAsync("book-vocabulary", terms);
```

- Requires Search 1.4 or later; `Dialect` requires Search 2.4.3 or later.
- `Distance` and `Dialect` accept 1–4; omitted values use server defaults.
- Include lists add suggestions. Exclude lists suppress checking of matching **query terms**: excluding a dictionary containing `reids` suppresses corrections for `reids`.
- Options copy include/exclude lists on initialization; later changes to your lists have no effect.
- Dictionary commands route by dictionary name; spellcheck routes by index name. Plain cluster mode can return node-local results.

See [FT.SPELLCHECK](https://redis.io/docs/latest/commands/ft.spellcheck/).

## Autocomplete dictionaries

Suggestion dictionaries are Redis keys, independent of indexes. They need no `FT.CREATE`, and indexing documents does not fill them. Use `AddSuggestionAsync` (`FT.SUGADD`), `DeleteSuggestionAsync` (`FT.SUGDEL`), `GetSuggestionCountAsync` (`FT.SUGLEN`) and `GetSuggestionsAsync` (`FT.SUGGET`):

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;
await search.AddSuggestionAsync("book-suggestions", "Redis in practice", 2,
    new() { Payload = System.Text.Encoding.UTF8.GetBytes("book:42") });
await search.AddSuggestionAsync("book-suggestions", "Redis in practice", 1,
    new() { Increment = true });
var suggestions = await search.GetSuggestionsAsync("book-suggestions", "Red",
    new() { Fuzzy = true, Max = 10, WithScores = true, WithPayloads = true });
foreach (var suggestion in suggestions)
    Console.WriteLine($"{suggestion.Text}: {suggestion.Score}");
```

- Adding an existing suggestion replaces its weight unless `Increment` is set. It returns the dictionary's entry count.
- Deletion returns whether the entry existed. Missing dictionaries have zero entries.
- Negative weights are valid; NaN is rejected. An empty prefix is allowed.
- `Fuzzy` allows one edit in the prefix. `Max` must be positive; the server default is 5.
- Scores are server match scores, not necessarily stored weights; null without `WithScores`.
- Payloads are binary: null means absent or not requested, empty means an empty payload. Keep input payload memory unchanged until `AddSuggestionAsync` completes. Redis 8.10 supports payloads, though [FT.SUGADD](https://redis.io/docs/latest/commands/ft.sugadd/) marks `PAYLOAD` deprecated.
- The dictionary is a binary-safe `RespireKey` and routes by key. Clustered Redis Software/Cloud has restrictions; see [FT.SUGGET](https://redis.io/docs/latest/commands/ft.sugget/).

## Synonyms and tag values

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;
await search.UpdateSynonymsAsync("books", "computing", ["computer", "pc", "laptop"]);
var memberships = await search.GetSynonymsAsync("books");
var categories = await search.GetTagValuesAsync("books", "category");
```

`UpdateSynonymsAsync` (`FT.SYNUPDATE`) creates or extends a synonym group without removing members.

- By default the server rescans existing documents; reindexing may still run after the call returns. Set `skipInitialScan: true` to apply mappings only to documents indexed later.
- Index names, group IDs and terms must be nonblank, with at least one term.
- Without `skipInitialScan`, the first term cannot be `SKIPINITIALSCAN` (any case), because Redis reads it as the option. Put it after another term.

`GetSynonymsAsync` (`FT.SYNDUMP`) returns term-to-group mappings, keeping every group a term belongs to.

`GetTagValuesAsync` (`FT.TAGVALS`) returns a TAG field's distinct values in server order and normalization, without paging or sorting. [FT.TAGVALS](https://redis.io/docs/latest/commands/ft.tagvals/) is deprecated but supported on Redis 8.10.

## Index inventory and aliases

- `ListIndexesAsync()` sends `FT._LIST`.
- `ListAliasesAsync(index)` sends `FT.ALIASLIST index` and requires Redis 8.10 or later. It lists aliases per index, not a global map.
- `AddAliasAsync`, `UpdateAliasAsync` and `DeleteAliasAsync` send `FT.ALIASADD`, `FT.ALIASUPDATE` and `FT.ALIASDEL`. `UpdateAliasAsync` creates an absent alias. Deleting an alias keeps its index and documents.

To swap indexes without interrupting callers, build and verify the new index, then switch the alias:

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;

// Create books-v1 and verify its indexing is complete before adding the alias.
await search.AddAliasAsync("books-live", "books-v1");
// Build books-v2 and verify its indexing is complete before switching.
await search.UpdateAliasAsync("books-live", "books-v2");
var current = await search.SearchAsync("books-live", new(RespireSearchExpression.FromRaw("*")));
// Drop the old index only after UpdateAliasAsync completes; keep the indexed documents.
await search.DropIndexAsync("books-v1");
```

Without a Search coordinator, the alias and target index must resolve to the same server.

## Search configuration

`GetConfigurationAsync(option)` returns a read-only dictionary of option names to nullable string values; the default option is `*`. `SetConfigurationAsync(option, value)` changes one option and requires `RespireOptions.AllowAdmin = true`. Server errors (invalid values, immutable options, ACL denials) pass through.

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    AllowAdmin = true,
});
var configuration = await client.Search.GetConfigurationAsync();
Console.WriteLine(configuration["TIMEOUT"]);
await client.Search.SetConfigurationAsync("TIMEOUT", "1000");
```

These send `FT.CONFIG GET/SET`, [deprecated since Redis 8.0](https://redis.io/docs/latest/commands/ft.config-get/) but still available on 8.10. Prefer standard config with `search-` names: `client.Server.ConfigAsync("search-*")` and `client.Server.SetConfigAsync("search-timeout", "1000")`. The [Search configuration reference](https://redis.io/docs/latest/develop/ai/search-and-query/administration/configuration/) maps old names to new.

- Configuration is per node. Each call goes to one node (the primary route on Cluster and Sentinel) and never fans out. To target a specific node, connect a standalone client to it.
- **[FT.CONFIG SET](https://redis.io/docs/latest/commands/ft.config-set/) changes do not survive restarts.** Use your deployment's config mechanism or `CONFIG REWRITE` to persist. Not every option can change at runtime.
- Cancellation cannot undo a change the server already accepted.

## Profiling Search, Aggregate, and Hybrid queries

`ProfileSearchAsync`, `ProfileAggregateAsync` and `ProfileHybridSearchAsync` run [FT.PROFILE](https://redis.io/docs/latest/commands/ft.profile/). They return the normal typed result in `Result` and a profile tree in `Profile`, using the same options as the unprofiled methods.

```csharp
using Respire;
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var expression = RespireSearchExpression.FromRaw("hello");
var query = new RespireSearchQuery(expression,
    new() { ReturnFields = ["title"], WithScores = true });
var search = client.Search;
var profiled = await search.ProfileSearchAsync("documents", query, limited: true);
Console.WriteLine($"Matches: {profiled.Result.Total}");
foreach (var shard in profiled.Profile.Children)
{
    if (shard.Metrics.TryGetValue("Total profile time", out var milliseconds))
        Console.WriteLine($"{shard.Name}: {milliseconds} ms");
}
```

Requirements: Search 2.2 or later for Search and Aggregate; [Redis 8.8 or later](https://redis.io/docs/latest/develop/whats-new/8-8/#ftprofile-hybrid) for Hybrid. Profiling aggregation does not create a cursor.

The tree:

- `Children` keeps the ordered shard, coordinator, iterator and processor tree, including Hybrid's separate SEARCH and VSIM branches.
- `Type` identifies iterators and processors. `TimeMilliseconds` reads their `Time` metric.
- `Metrics` holds every finite numeric field (such as `Parsing time` or `Results processed`), including unknown names. Times are in milliseconds.
- `Properties` keeps every reported field as a `RespireSearchValue`, including nested and binary values.
- Field names and metrics can change between server versions. The reader targets Redis 8.10 layouts; incompatible older layouts, or replies deeper than 64 levels, throw `InvalidOperationException`.

Usage notes:

- Profiling adds server work and reply size; use it to diagnose, not on every request.
- `limited: true` omits reader details inside built-in unions; the query still runs in full. `NoContent` or `Limit = (0, 0)` reduce returned documents.
- Cancellation stops waiting; it does not stop a query Redis already accepted.
- Times describe the server, not client network latency.

## Routing, caching, and ownership

Index commands carry an index name, not keys. On Redis Cluster, each command (including cursor reads) goes to the node that owns the index, alias or dictionary name's hash slot. Alias listing and spellcheck route by index name; alias changes route by alias name; `FT._LIST` goes to one node.

**Respire never fans out queries or merges shard results.** Cross-shard search needs the server's Search coordinator. Without it, as in plain Redis Open Source cluster mode, a query sees only the receiving node's documents, and Respire cannot detect the partial result. Standalone and Sentinel need no special handling.

- Client-side cache: read-only Search commands leave it intact; index, alias, dictionary, suggestion, synonym and configuration writes invalidate it.
- Search methods are not on Respire's zero-allocation hot path.
- Returned strings, lists and trees are owned copies that stay valid after later commands.
- Keep binary input memory (keys, payloads) unchanged until the command completes.
- Invalid options throw before sending; server errors and cancellation propagate.
- A Native AOT smoke app covers the package against Redis 8.10 over RESP2 and RESP3.
