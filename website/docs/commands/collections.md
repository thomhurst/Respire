---
title: Collections and streams
description: Work with hashes, lists, sets, sorted sets, and streams.
---

# Collections and streams

Collection commands are grouped by Redis data type. Method names omit the Redis prefix because the facet supplies the context. Bitmap, HyperLogLog, and geo operations also have typed facets.

## Hashes

```csharp
await redis.Hashes.SetAsync("user:42", "name", "Ada");
await redis.Hashes.SetAsync(
    "user:42",
    ("role", "admin"),
    ("region", "eu-west"));

string? name = await redis.Hashes.GetStringAsync("user:42", "name");
Dictionary<string, string> profile = await redis.Hashes.GetAllAsync("user:42");
```

## Lists

```csharp
await redis.Lists.RightPushAsync("jobs", "invoice:42", "invoice:43");
string? next = await redis.Lists.LeftPopAsync("jobs");
string[] nextBatch = await redis.Lists.LeftPopManyAsync("jobs", count: 128);
string[] pending = await redis.Lists.RangeAsync("jobs", 0, 99);
```

Set `waitFor` to transparently select the blocking command and a dedicated connection. See [blocking queues](../guides/blocking-queues).

## Sets

```csharp
await redis.Sets.AddAsync("team:red", "ada", "grace");
await redis.Sets.AddAsync("on-call", "ada");

string[] both = await redis.Sets.IntersectAsync("team:red", "on-call");
bool member = await redis.Sets.ContainsAsync("team:red", "ada");
```

## Sorted sets

```csharp
await redis.SortedSets.AddAsync("scores", "ada", 98.5);
await redis.SortedSets.IncrementAsync("scores", "ada", 1.5);

SortedSetEntry[] top = await redis.SortedSets.RangeWithScoresAsync(
    "scores",
    start: 0,
    stop: 9,
    descending: true);
```

## Streams

```csharp
await redis.Streams.CreateGroupAsync("events", "processors", createStream: true);

await foreach (var entry in redis.Streams.ReadGroupAsync(
    "events",
    group: "processors",
    consumer: Environment.MachineName,
    cancellationToken: stoppingToken))
{
    await HandleAsync(entry.GetString("type"));
    await entry.AckAsync();
}
```

Blocking stream reads use the same dedicated-connection mechanism as blocking list operations.

## Bitmaps, HyperLogLogs, and geo indexes

```csharp
bool wasActive = await redis.Bitmaps.GetAndSetAsync("active:2026-08-09", userId, true);
long active = await redis.Bitmaps.CountAsync("active:2026-08-09");
long? firstActive = await redis.Bitmaps.PositionAsync("active:2026-08-09", true);

await redis.HyperLogLog.AddAsync("visitors", sessionId);
long estimate = await redis.HyperLogLog.CountAsync("visitors");

await redis.Geo.AddAsync("cafes", new GeoEntry(-0.1276, 51.5072, "london"));
GeoSearchResult[] nearby = await redis.Geo.SearchAsync(
    "cafes",
    GeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    GeoSearchShape.Circle(10, GeoUnit.Kilometers));
```

For uncommon operations and modules, use the [complete command catalog](../guides/raw-commands).
