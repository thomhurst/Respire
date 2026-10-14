# Respire.Search

Typed Redis Search APIs for indexes, full-text queries, aggregation, and vector search.

## Install

```sh
dotnet add package Respire.Search
```

## Usage

```csharp
using Respire;
using Respire.Search;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var search = new RespireSearchClient(redis);
await search.CreateIndexAsync("books", new RespireSearchIndexDefinition
{
    Prefixes = ["book:"],
    Fields = [new("title", RespireSearchFieldType.Text)],
});
await redis.Hashes.SetAsync("book:1", "title", "Redis guide");
var results = await search.SearchAsync("books", new RespireSearchQuery(
    RespireSearchQueryBuilder.TextField("title", "Redis")));
```

Requires a server with Redis Search support. Run index creation once during setup; installing this package does not enable server modules.

[Read the guide](https://thomhurst.github.io/Respire/docs/guides/search) for configuration, requirements, and further examples.
