---
title: Geospatial indexes
description: Store coordinates, find nearby members, and save geospatial search results.
---

# Geospatial indexes

Use `redis.Geo` from the core `Respire` package. Coordinates are longitude first, then latitude.
Basic geo commands require Redis 3.2+; `SearchAsync` and `SearchStoreAsync` require Redis 6.2+.

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
long added = await redis.Geo.AddAsync("{places}:cafes",
    new GeoEntry(-0.1276, 51.5072, "london"),
    new GeoEntry(-0.1419, 51.5014, "westminster"));

GeoSearchResult[] nearby = await redis.Geo.SearchAsync(
    "{places}:cafes",
    GeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    GeoSearchShape.Circle(10, GeoUnit.Kilometers),
    new GeoSearchOptions
    {
        Sort = GeoSortOrder.Ascending,
        Count = 10,
        IncludeDistance = true,
        IncludeCoordinates = true,
    });
```

`GeoSearchOrigin.FromMember` searches around an existing member. Use `GeoSearchShape.Box`
for a rectangular area. `Count` limits results and must be positive. `Any = true`
requires `Count` and `Sort = GeoSortOrder.Unsorted`; it cannot be combined with the
ascending sort in the example above. It lets the server stop early, so it does not
guarantee the nearest matches. Distance uses the search shape's unit.

## Commands and results

| Method | Redis command | Result |
| --- | --- | --- |
| `AddAsync` | GEOADD | Number of newly added members; `GeoAddOptions.Changed` includes updated members |
| `DistanceAsync` | GEODIST | Distance, or `null` when either member is absent |
| `HashAsync` | GEOHASH | One geohash string per member, with `null` for missing members |
| `PositionAsync` | GEOPOS | One nullable `GeoPosition` per member |
| `SearchAsync` | GEOSEARCH | Members and requested distance, hash, and coordinate details |
| `SearchStoreAsync` | GEOSEARCHSTORE | Number of members stored at the destination |

`GeoAddOptions.When` selects conditional writes with `SetWhen`. Search result `Member`
decodes UTF-8; use `MemberBytes` for exact binary member data. Optional result fields are
`null` unless requested.

## Store results and remove members

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
long stored = await redis.Geo.SearchStoreAsync(
    "{places}:nearby", "{places}:cafes",
    GeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    GeoSearchShape.Circle(10, GeoUnit.Kilometers));
await redis.SortedSets.RemoveAsync("{places}:cafes", "london");
```

Geo indexes are sorted sets, so remove a member through `SortedSets.RemoveAsync`.
`storeDistance: true` stores distances as destination scores instead of geospatial scores.
Key prefixes apply to source and destination keys, not member names. In Cluster, both keys
must share one slot; the `{places}` hash tag above keeps them together.

`batch.Geo` and `transaction.Geo` expose the same operations without the `Async` suffix.
Read pending values after [executing the batch or transaction](../guides/batches-and-transactions.md).
