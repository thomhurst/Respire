---
title: Sparse arrays
description: Redis 8.10 sparse arrays, range scans, aggregation, and ring buffers.
---

# Sparse arrays

`redis.Arrays` exposes all 18 Redis array commands. The complete family requires Redis 8.10; older servers report their usual unknown-command error for unavailable operations. Arrays store values at unsigned indexes without shifting other positions when slots are deleted.

Indexes use `ulong`, including positions beyond `long.MaxValue`. Redis reserves `ulong.MaxValue` for normal slot operations. `SeekAsync` also accepts that value to exhaust the insertion cursor.

```csharp
await redis.Arrays.SetAsync("samples", 2, "0", "7");
ulong populated = await redis.Arrays.CountAsync("samples"); // 2
ulong length = await redis.Arrays.LengthAsync("samples");   // 4
string?[] values = await redis.Arrays.RangeAsync("samples", 0, 3);
// [null, null, "0", "7"]

await redis.Arrays.SetManyAsync("samples",
    new RespireArrayItem(10, "ten"),
    new RespireArrayItem(20, "twenty"));
await redis.Arrays.DeleteRangeAsync("samples", new RespireArrayRange(2, 3));
```

`SetAsync` and `SetManyAsync` return the number of newly populated slots, rather than the number of supplied values. `CountAsync` counts populated slots; `LengthAsync` returns the highest populated index plus one. Deleting the final populated slot removes the key.

## Typed values and holes

`SetAsync<T>` uses the configured serializer. `GetAsync<T>` returns `default` for a missing slot. For value types, use nullable `T` or `TryGetAsync<T>` to distinguish absence from a stored default. `GetManyAsync<T>`, `RangeAsync<T>`, and `LastItemsAsync<T>` retain the reply's positions; nullable value types preserve empty slots. Typed scan and grep entries identify populated slots, but their values may deserialize to null (for example, stored JSON `null`).

```csharp
RespireGet<int> slot = await redis.Arrays.TryGetAsync<int>("samples", 2);
if (slot.Found) Console.WriteLine(slot.Value); // Stored 0 is still found.
int?[] values = await redis.Arrays.RangeAsync<int?>("samples", 0, 3);
```

Ranges include both endpoints. Descending endpoints return values in reverse order. [ARGETRANGE](https://redis.io/docs/latest/commands/argetrange/) returns nil for each missing position, even when the entire key is missing, and rejects ranges larger than one million positions.

## Scans and searches

[ARSCAN](https://redis.io/docs/latest/commands/arscan/) takes index ranges, skips empty slots, and returns nested index/value pairs. `ScanPageAsync` performs one command. `ScanAsync` requests pages with `LIMIT`, then advances past the last returned index. Each page is a separate read; changes during enumeration can affect later pages.

```csharp
await foreach (var entry in redis.Arrays.ScanAsync("samples", 0, 1000, pageSize: 100))
    Console.WriteLine($"{entry.Index}: {entry.Value}");

RespireArrayPredicate[] predicates =
[
    new(RespireArrayPredicateKind.Contains, "error"),
    new(RespireArrayPredicateKind.Glob, "*timeout*"),
];
ulong[] matches = await redis.Arrays.GrepAsync("log",
    RespireArrayBound.First, RespireArrayBound.Last, predicates,
    new RespireArrayGrepOptions { IgnoreCase = true, Limit = 20 });
```

`GrepAsync` returns indexes. `GrepEntriesAsync` requests `WITHVALUES` and returns indexes with values. Predicate kinds are exact equality, substring (`MATCH`), Redis glob, and POSIX extended regex (`RE`). Predicates use OR by default; `MatchAll` requests AND. Logical bounds `First` and `Last` encode Redis's `-` and `+` tokens.

Read operations use generated catalog metadata and honor replica read policies. `ARSCAN` retains the catalog's `CursorRead` classification with no standard cursor argument layout. Array enumeration does not invent a Redis cursor or acquire its own cursor-affinity lease; routing retains the existing shared read-policy behavior. Transactions execute on their selected primary.

## Aggregation and insertion cursors

```csharp
RespireArrayAggregate sum = await redis.Arrays.AggregateAsync(
    "samples", 0, 100, RespireArrayOperation.Sum);
Console.WriteLine(sum.NumericText); // Redis numeric text, without double rounding.

RespireArrayAggregate used = await redis.Arrays.AggregateAsync(
    "samples", 0, 100, RespireArrayOperation.Used);
Console.WriteLine(used.Integer);

await redis.Arrays.InsertAsync("events", "first", "second");
ulong? next = await redis.Arrays.NextIndexAsync("events");
await redis.Arrays.SeekAsync("events", 10);
await redis.Arrays.RingAsync("recent", 3, "a", "b", "c", "d");
string?[] recent = await redis.Arrays.LastItemsAsync("recent", 3);
// ["b", "c", "d"]
```

`AggregateAsync` preserves the server's numeric text for SUM/MIN/MAX and signed integers for AND/OR/XOR/MATCH/USED. Empty numeric or bitwise input gives `IsNull`; MATCH and USED give zero. MATCH requires the optional `match` argument. Redis evaluates aggregation ranges in ascending order regardless of endpoint order.

`InsertAsync` writes at the insertion cursor and returns the last written index. `SetAsync` and `SetManyAsync` do not move that cursor. `NextIndexAsync` returns zero before insertion or when the key is missing, and null after exhaustion. `SeekAsync` returns false for a missing key.

`RingAsync` wraps writes into a positive size and returns the last written slot. Resizing preserves the latest contiguous tail; a hole ends the retained tail. `LastItemsAsync` walks recent positions and can return holes. Results are oldest first unless `reverse: true` is specified. Its count is capped by the number of populated slots.

`InfoAsync(key, full: true)` returns typed ARINFO metadata and optional slice statistics. Missing keys raise a server error. ARINFO reports insertion position zero when exhausted; `NextIndexAsync` distinguishes that state.

## Batches, transactions, and the fake server

`batch.Arrays` and `transaction.Arrays` expose the same single-command methods without `Async`. Queue `ScanPage`, then read its pending result after execution; streaming `ScanAsync` remains immediate-only. Batches copy argument spans when queued, but referenced byte buffers must remain unchanged until execution completes. Transactions serialize arguments into their owned buffer when queued.

`Respire.Testing` supports the complete command family, sparse slots, unsigned indexes, insertion cursors, and ring resizing. It stores a dictionary rather than Redis slices, so ARINFO storage statistics are zero. Its AROP numeric arithmetic is bounded to .NET `decimal`; unsupported numeric ranges report explicit errors. ARGREP RE supports printable ASCII values and the common regex subset without escapes, POSIX classes, or .NET-specific groups. Use real Redis for allocator statistics, long-double precision, and full POSIX regex compatibility.
