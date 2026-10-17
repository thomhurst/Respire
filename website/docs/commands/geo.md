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
    new RespireGeoEntry(-0.1276, 51.5072, "london"),
    new RespireGeoEntry(-0.1419, 51.5014, "westminster"));

RespireGeoSearchResult[] nearby = await redis.Geo.SearchAsync(
    "{places}:cafes",
    RespireGeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    RespireGeoSearchShape.Circle(10, RespireGeoUnit.Kilometers),
    new RespireGeoSearchOptions
    {
        Sort = RespireGeoSortOrder.Ascending,
        Count = 10,
        IncludeDistance = true,
        IncludeCoordinates = true,
    });
```

`RespireGeoSearchOrigin.FromMember` searches around an existing member. Use `RespireGeoSearchShape.Box`
for a rectangular area. `Count` limits results and must be positive. `Any = true`
requires `Count` and `Sort = RespireGeoSortOrder.Unsorted`; it cannot be combined with the
ascending sort in the example above. It lets the server stop early, so it does not
guarantee the nearest matches. Distance uses the search shape's unit.

## Polygon searches (Valkey 9.0+)

Use `RespireGeoSearchShape.Polygon` with the overload that omits the origin:

```csharp
var area = RespireGeoSearchShape.Polygon(
    new RespireGeoPosition(-0.2, 51.4),
    new RespireGeoPosition(0, 51.4),
    new RespireGeoPosition(0, 51.6),
    new RespireGeoPosition(-0.2, 51.6));

var matches = await redis.Geo.SearchAsync("{places}:cafes", area,
    new RespireGeoSearchOptions { IncludeDistance = true, IncludeCoordinates = true });
long stored = await redis.Geo.SearchStoreAsync("{places}:area", "{places}:cafes", area);
```

This sends `GEOSEARCH key BYPOLYGON count longitude latitude ...` or
`GEOSEARCHSTORE destination source BYPOLYGON count longitude latitude ...`.
The [Valkey command reference](https://valkey.io/commands/geosearch/) requires no
`FROMMEMBER`, `FROMLONLAT`, or distance unit for polygons. Valkey computes the bounding
box and a spherical centroid from the vertices. Distances from that centroid, including
`STOREDIST` scores, are in meters.

The factory copies the vertices. It accepts an open ring or a closed ring whose last
vertex repeats the first; repeated closing vertices are removed before sending.
At least three distinct vertices are required, with finite longitude in [-180, 180]
and latitude in [-85.05112878, 85.05112878]. Supply vertices in boundary order;
Respire does not check self-intersections or repair polygon geometry.
An explicit origin is rejected for polygons. Circle and box searches still require an origin.

Sorting, `Count`, `Any`, and result details work as for the other shapes.
`batch.Geo.Search(key, area)` and `transaction.Geo.Search(key, area)`, plus their
`SearchStore` counterparts, use the same shape and validation.

Older Valkey and unsupported Redis servers reject `BYPOLYGON` with a
`RespireServerException`. GEOSEARCH syntax errors include a `BYPOLYGON requires Valkey 9.0+`
compatibility hint while retaining the server error code, command name, and endpoint.
Other errors, such as `WRONGTYPE` or ACL denials, retain their original message.

## Commands and results

| Method | Redis command | Result |
| --- | --- | --- |
| `AddAsync` | GEOADD | Number of newly added members; `RespireGeoAddOptions.Changed` includes updated members |
| `DistanceAsync` | GEODIST | Distance, or `null` when either member is absent |
| `HashAsync` | GEOHASH | One geohash string per member, with `null` for missing members |
| `PositionAsync` | GEOPOS | One nullable `RespireGeoPosition` per member |
| `SearchAsync` | GEOSEARCH | Members and requested distance, hash, and coordinate details |
| `SearchStoreAsync` | GEOSEARCHSTORE | Number of members stored at the destination |

`RespireGeoAddOptions.When` selects conditional writes with `RespireSetWhen`. Search result `Member`
decodes UTF-8; use `MemberBytes` for exact binary member data. Optional result fields are
`null` unless requested.

## Store results and remove members

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
long stored = await redis.Geo.SearchStoreAsync(
    "{places}:nearby", "{places}:cafes",
    RespireGeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    RespireGeoSearchShape.Circle(10, RespireGeoUnit.Kilometers));
await redis.SortedSets.RemoveAsync("{places}:cafes", "london");
```

Geo indexes are sorted sets, so remove a member through `SortedSets.RemoveAsync`.
`storeDistance: true` stores distances as destination scores instead of geospatial scores.
Key prefixes apply to source and destination keys, not member names. In Cluster, both keys
must share one slot; the `{places}` hash tag above keeps them together.

`batch.Geo` and `transaction.Geo` expose the same operations without the `Async` suffix.
Read pending values after [executing the batch or transaction](../guides/batches-and-transactions.md).
