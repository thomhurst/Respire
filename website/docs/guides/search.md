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

Text fields also accept `Phonetic` with English, French, Portuguese, or Spanish
double-metaphone matching. Index definitions accept `Language`, `LanguageField`,
`Score` (0–1), `TemporarySeconds`, and `SkipInitialScan`. `StopWords = null` uses
the server's default stopwords; an empty list emits `STOPWORDS 0` and disables them.
**Temporary index expiry deletes its indexed documents as well as the index.**
See [FT.CREATE](https://redis.io/docs/latest/commands/ft.create/).

## Query options and returned text

`RespireSearchQueryOptions` accepts binary-safe `InKeys`, text `InFields`, `Slop`,
`InOrder`, `Language`, `Scorer`, `Verbatim`, and `NoStopWords`, alongside the existing
projection, sorting, paging, timeout, dialect, and parameter options. Empty key and
field lists leave the search unrestricted. A scorer name can identify a built-in
or server-registered scorer; the server validates supported languages and scorers.

Set `Highlight` to `RespireSearchHighlightOptions` and `Summarize` to
`RespireSearchSummaryOptions`. Both accept `Fields`; an empty list selects all
returned text fields. Highlighting accepts optional `(Open, Close)` `Tags`.
Summaries accept positive `Fragments` and `Length`, plus an optional `Separator`.
`NoContent` cannot be combined with highlighting or summaries. Key and text-field
selection lists are copied when their options are initialized. Keep binary key
memory unchanged until the search completes, as with other command arguments.

Returned `Fields` retain the server-generated text. `TextResults` provides owned
`RespireSearchTextResult` values with `Text`, `HighlightRequested`, and
`SummaryRequested`. Those flags describe the requested presentation, not whether
the server changed the value. With default field selection, this view includes
returned scalar fields because only the server knows which fields are indexed
as text. Explicit field selection limits this view to those names. The client
preserves each complete summary string, including separators; splitting could
misinterpret a separator already present in the original text. Highlight markup
is not HTML-escaped. Escape or sanitize it according to your rendering context.

`ExplainScore` requires `WithScores`; it retains the nested explanation in
`ScoreExplanation` while `Score` remains numeric. `WithPayloads` retains owned
binary `Payload` memory, preserving null versus empty. This is a legacy server
feature, and modern indexes normally return null. `WithSortKeys` retains the
encoded `SortKey`, such as `#5` for a numeric sort value; it can be null without
a sortable value. Metadata also works with `NoContent`. These reply options work
through normal, vector, and profiled searches on RESP2 and RESP3.
See [FT.SEARCH](https://redis.io/docs/latest/commands/ft.search/).

## Collecting documents within aggregate groups

Redis 8.10's `COLLECT` reducer is available through `RespireSearchReducer.Collect`.
Pass `RespireSearchCollectOptions` with either nonempty `Fields` or `AllFields`,
optional `Distinct`, `SortBy`, and `Limit`, and an optional reducer alias.
Field and sort names are normalized to an `@` prefix, and all argument counts
are computed automatically. `AllFields` projects fields already materialized
by `LOAD` or earlier pipeline stages; it does not load whole documents.

Collected entries are nested values in `StructuredRows[row][alias].Items`.
RESP2 entries are field/value arrays; RESP3 entries are maps whose `Items`
alternate keys and values. `StructuredRows` retains their owned values after
later commands. See [FT.AGGREGATE](https://redis.io/docs/latest/commands/ft.aggregate/).
Native `FT.HYBRID` remains supported with typed text/vector queries, projections,
parameters, and reciprocal-rank fusion; integration tests cover both protocols.

## Spelling dictionaries and corrections

`AddDictionaryTermsAsync` (`FT.DICTADD`) and `DeleteDictionaryTermsAsync`
(`FT.DICTDEL`) return the number of terms inserted or removed, not the total size.
`DumpDictionaryAsync` (`FT.DICTDUMP`) returns owned strings in unspecified order;
an absent dictionary returns an empty list. These spelling dictionaries are separate
from autocomplete dictionaries and do not require an index.

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

These commands require Search 1.4 or later; `DIALECT` requires Search 2.4.3 or later.
Redis 8.10 integration tests cover RESP2 and RESP3. `Distance` accepts 1–4, and
`Dialect` accepts 1–4; omitted values use server defaults. Include and exclude lists
emit repeated `TERMS INCLUDE` / `TERMS EXCLUDE` clauses. Options copy these lists
during initialization and expose read-only snapshots. Later changes to the input
lists cannot affect an options instance or its record copies. Inclusion supplies extra
suggestions. Exclusion suppresses spellchecking of matching **query terms**. For
example, excluding a dictionary containing `reids` suppresses corrections for `reids`.
Each correction retains its query term and scored suggestions, including empty
suggestion lists. Returned strings and lists remain valid after later commands.
See the [Redis command reference](https://redis.io/docs/latest/commands/ft.spellcheck/).

Dictionary commands route by dictionary name; spellcheck routes by index name.
Respire does not distribute dictionaries or combine results across nodes. Configure
the server search coordinator and dictionary placement accordingly; plain cluster
mode can return node-local results. Key-prefixed views reject these commands.
Dictionary writes conservatively invalidate the local cache; dump and spellcheck
leave it intact. Invalid options fail before sending, and server errors and
cancellation propagate to the caller.

## Autocomplete dictionaries

Suggestion dictionaries are Redis keys independent of Search indexes. They do not
require `FT.CREATE`, and indexing documents does not populate them. Manage entries
with `AddSuggestionAsync` (`FT.SUGADD`), `DeleteSuggestionAsync` (`FT.SUGDEL`),
`GetSuggestionCountAsync` (`FT.SUGLEN`), and `GetSuggestionsAsync` (`FT.SUGGET`):

```csharp
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

Adding an existing suggestion replaces its weight unless `Increment` is set;
the return value is the dictionary's current entry count. Deletion returns whether
an entry existed. Missing dictionaries have zero entries and no suggestions.
Negative weights are valid; NaN is rejected. An empty prefix is allowed.

`Fuzzy` allows one edit in the prefix. `Max` must be positive; omitting it uses the
server default of 5. Scores are server-calculated match scores, not necessarily
the stored weights. A score is null when `WithScores` is omitted. Payloads are
owned binary memory, with null representing an absent or unrequested payload and
empty memory preserving a returned empty payload. Keep input payload memory
unchanged until `AddSuggestionAsync` completes. Redis 8.10 supports payloads,
although the [FT.SUGADD reference](https://redis.io/docs/latest/commands/ft.sugadd/)
marks the `PAYLOAD` option deprecated.

The dictionary argument is a binary-safe `RespireKey`; commands route by that key
without cross-node fan-out or merged suggestions. Server deployment support still
applies: the [Redis command reference](https://redis.io/docs/latest/commands/ft.sugget/)
lists restrictions for clustered Redis Software/Cloud databases. Prefixed views
reject these FT commands like other Search operations. Reads retain the local
client cache; adding and deleting suggestions conservatively invalidate it.

## Synonyms and tag values

`UpdateSynonymsAsync` sends `FT.SYNUPDATE` to create or extend a synonym group;
updating a group adds terms without removing existing members. By default, the
server scans existing documents to index the new synonym mappings. Set
`skipInitialScan: true` only when the mappings should apply to documents indexed
after the update. Background reindexing may still be running when the command
returns.

```csharp
using Respire.Search;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
var search = client.Search;
await search.UpdateSynonymsAsync("books", "computing", ["computer", "pc", "laptop"]);
var memberships = await search.GetSynonymsAsync("books");
var categories = await search.GetTagValuesAsync("books", "category");
```

`GetSynonymsAsync` sends `FT.SYNDUMP` and returns owned term-to-group mappings.
A term can belong to several groups; all memberships are retained. Index names,
group IDs, and terms must be nonblank, and updates require at least one term.
Without `skipInitialScan`, the first term cannot literally be `SKIPINITIALSCAN`
(case-insensitive), because Redis interprets that position as the option. Place
that literal term after another term, or explicitly enable `skipInitialScan`.

`GetTagValuesAsync` sends `FT.TAGVALS` for a TAG field and returns owned distinct
values in the server's order and normalization. There is no paging or sorting.
Redis documents [FT.TAGVALS](https://redis.io/docs/latest/commands/ft.tagvals/)
as deprecated, but Redis 8.10 supports it. Synonym dumps and tag-value reads retain
the client cache; synonym updates conservatively invalidate it. All three commands
retain Search's prefix restrictions and index/coordinator routing, without client
fan-out or merged shard results.

## Index inventory and aliases

`ListIndexesAsync()` sends `FT._LIST`. `ListAliasesAsync(index)` sends
`FT.ALIASLIST index` and requires Redis 8.10 or later. Both return owned name
collections that remain valid after later commands. Alias listing is per index,
not a global alias-to-index map.

Use `AddAliasAsync`, `UpdateAliasAsync`, and `DeleteAliasAsync` for
`FT.ALIASADD`, `FT.ALIASUPDATE`, and `FT.ALIASDEL`. After preparing and verifying a
replacement index, atomically switch the alias without interrupting callers:

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

`UpdateAliasAsync` also creates an absent alias. Deleting an alias leaves its
index and documents intact. Alias mutations conservatively invalidate the local
client cache; inventory and alias listing leave it intact. Prefixed views reject
all these commands, as they do other Search commands.

Respire sends inventory to the selected node without cluster fan-out. Alias
mutations route by alias name, and alias listing routes by index name. These
names must resolve to the appropriate node or server-side Search coordinator;
Respire does not synchronize aliases or merge inventory across shards. On a
cluster without a Search coordinator, results describe only the selected node.

## Queries

Other index operations use `FT.CREATE`, `FT.ALTER`, `FT.DROPINDEX`, and `FT.INFO`. `GetIndexInfoAsync` returns a parsed `RespireSearchIndexInfo` with the index name, document count, schema attributes, and every reported property as a copied value. Query methods use `FT.SEARCH`, `FT.EXPLAIN`, and `FT.EXPLAINCLI`; `ExplainAsync` takes `RespireSearchExplainOptions` to select CLI output or a dialect.

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

## Search configuration

`GetConfigurationAsync(option)` returns an owned, read-only dictionary of option names and nullable string values. Its default option is `*`; individual names and wildcard support are server-defined. `SetConfigurationAsync(option, value)` changes one option and requires `RespireOptions.AllowAdmin = true`, like the standard server configuration API. Both methods preserve server errors, including invalid values, immutable options, and ACL denials.

```csharp
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

These methods send the legacy `FT.CONFIG GET/SET` commands, which remain available on Redis 8.10 but are [deprecated since Redis 8.0](https://redis.io/docs/latest/commands/ft.config-get/). Modern Redis exposes Search options through standard `CONFIG GET/SET` with `search-` names: prefer `client.Server.ConfigAsync("search-*")` and `client.Server.SetConfigAsync("search-timeout", "1000")`. The server's [Search configuration reference](https://redis.io/docs/latest/develop/ai/search-and-query/administration/configuration/) maps legacy option names to modern names. Options vary by server version and deployment; installing this package does not enable configuration on a managed service that restricts it.

Configuration belongs to one node. Each call selects one node and never fans out. Cluster and Sentinel clients use the primary command route; standalone clients use their connected endpoint. Separate calls can select different nodes after topology changes. To target a specific node, use a separate standalone client connected directly to that node. Key-prefixed views reject both methods. Reads preserve the local client-side cache; writes invalidate it conservatively. Cancellation stops waiting and cannot undo a change already accepted by the server.

Legacy [FT.CONFIG SET](https://redis.io/docs/latest/commands/ft.config-set/) changes do not persist across restarts. Use the deployment's configuration mechanism or standard `CONFIG REWRITE` where supported to persist modern configuration. Not every option can change at runtime.

## Profiling Search, Aggregate, and Hybrid queries

`ProfileSearchAsync`, `ProfileAggregateAsync`, and `ProfileHybridSearchAsync` execute
[FT.PROFILE](https://redis.io/docs/latest/commands/ft.profile/) and return the normal typed
query result in `Result`, together with an owned profile tree in `Profile`. They reuse the
same query options and encoders as the corresponding unprofiled methods. Profiling
aggregation does not create a cursor. Search and Aggregate profiling require Search
2.2 or later; Hybrid profiling requires [Redis 8.8 or later](https://redis.io/docs/latest/develop/whats-new/8-8/#ftprofile-hybrid) with Search. All three forms
are tested against Redis 8.10 over RESP2 and RESP3. Server errors pass through unchanged,
including errors from older servers that do not support the selected profile form.
The typed profile reader targets the Redis 8.10 field/value layouts; incompatible
older profile layouts raise `InvalidOperationException` rather than dropping fields.
Profiles allow at most 64 nested collection levels, counting the profile root and
unknown fields. Deeper replies raise `InvalidOperationException` before the tree is copied.

```csharp
await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var expression = Respire.Search.RespireSearchExpression.FromRaw("hello");
var query = new Respire.Search.RespireSearchQuery(expression,
    new() { ReturnFields = ["title"], WithScores = true });
var search = new Respire.Search.RespireSearchClient(client);
var profiled = await search.ProfileSearchAsync("documents", query, limited: true);
Console.WriteLine($"Matches: {profiled.Result.Total}");
foreach (var shard in profiled.Profile.Children)
{
    if (shard.Metrics.TryGetValue("Total profile time", out var milliseconds))
        Console.WriteLine($"{shard.Name}: {milliseconds} ms");
}
```

`Children` preserves the ordered shard, coordinator, iterator, and processor tree,
including Hybrid's separate SEARCH and VSIM branches. `Type` identifies iterators
and processors; `TimeMilliseconds` reads their `Time` metric. `Metrics` includes all
finite numeric fields, including numeric strings and unknown field names. Examples
include `Parsing time`, `Results processed`, and
`Number of reading operations`. Times use milliseconds; counts are numeric server
values. `Properties` retains every reported field as an owned `RespireSearchValue`,
including unknown fields, nested collections, and binary strings. Names and metrics
can change between server versions. The tree owns its data after the pooled reply
is disposed; collections follow the package's owned-snapshot contract.

Profiling adds execution and measurement work, and large trees increase reply size.
Use it for diagnosis rather than every production request. `limited: true` asks Redis
to omit reader details within built-in unions; it does not skip query execution.
Normal `NoContent` or `Limit = (0, 0)` Search options can reduce returned documents.
Cancellation abandons the wait; it does not stop a query already accepted by Redis.
Profiling preserves the local client cache, rejects prefix views, and uses the same
index/coordinator routing as the corresponding query. Profile times describe the
selected server deployment, not the client's network latency.

## Routing, caching, and ownership

Index commands carry an index name instead of keys. On a Redis Cluster, Respire routes each index command, including cursor reads, to the node that owns the index name's hash slot. Configuration commands follow the node-local scope described above. Respire does not fan out queries or merge shard results. Cross-shard search relies on the server's search coordinator, so check that your cluster deployment provides one. Without it, for example in plain Redis Open Source cluster mode, a query only sees the documents on the node that receives it, and Respire cannot tell that the result is partial. Standalone and Sentinel deployments need no special handling.

With client-side caching enabled, read-only Search commands leave the local cache intact, while index changes invalidate it conservatively. Key-prefixed views reject Search commands, so include prefixes in the indexed keyspace and use keys in the format your index expects. Search methods build one argument list per call and are not part of Respire's zero-allocation hot path. The caller owns the underlying client.

The package reuses Respire's generated command infrastructure, and a Native AOT smoke app covers it against Redis 8.4 over RESP2 and RESP3.
