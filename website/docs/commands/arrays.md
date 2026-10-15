---
title: Sparse arrays
description: Redis 8.8+ sparse arrays, range scans, aggregation, and ring buffers.
---

# Sparse arrays

`redis.Arrays` exposes all 18 Redis array commands (Redis Open Source 8.8.0+; see the [Redis array command reference](https://redis.io/docs/latest/commands/arscan/)). Arrays store values at unsigned indexes; deleting a slot does not shift the others. Older servers return an unknown-command error.

Indexes are `ulong`. Redis reserves `ulong.MaxValue`, so slot operations reject it; `SeekAsync` accepts it to exhaust the insertion cursor.

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

- `SetAsync` and `SetManyAsync` return the number of newly populated slots.
- `CountAsync` counts populated slots; `LengthAsync` returns the highest populated index plus one.
- Deleting the last populated slot removes the key.

## Typed values and holes

`SetAsync<T>` uses the configured serializer. `GetAsync<T>` returns `default` for a missing slot; for value types, use a nullable `T` or `TryGetAsync<T>` to tell absence from a stored default. `GetManyAsync<T>`, `RangeAsync<T>`, and `LastItemsAsync<T>` keep reply positions, so nullable types show empty slots. Typed scan and grep entries are populated slots, but a value can still deserialize to null (for example, stored JSON `null`).

```csharp
RespireGetResult<int> slot = await redis.Arrays.TryGetAsync<int>("samples", 2);
if (slot.Found) Console.WriteLine(slot.Value); // Stored 0 is still found.
int?[] values = await redis.Arrays.RangeAsync<int?>("samples", 0, 3);
```

Ranges include both endpoints; descending endpoints return values in reverse. [ARGETRANGE](https://redis.io/docs/latest/commands/argetrange/) returns nil for each missing position (even if the key is missing) and rejects ranges over one million positions.

## Scans and searches

[ARSCAN](https://redis.io/docs/latest/commands/arscan/) returns index/value pairs for populated slots in a range. `ScanPageAsync` sends one command. `ScanAsync` pages with `LIMIT`; each page is a separate read, so concurrent changes can affect later pages.

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

- `GrepAsync` returns indexes; `GrepEntriesAsync` (`WITHVALUES`) returns indexes with values.
- Predicate kinds: exact equality, substring (`MATCH`), Redis glob, and POSIX extended regex (`RE`).
- Predicates are ORed; `MatchAll` ANDs them.
- `RespireArrayBound.First` and `Last` send Redis's `-` and `+`.

Reads follow the client's replica read policy. `ScanAsync` routes each page independently, so pages can come from different servers. Transactions run on their selected primary.

## Aggregation and insertion cursors

```csharp
RespireArrayAggregateResult sum = await redis.Arrays.AggregateAsync(
    "samples", 0, 100, RespireArrayOperation.Sum);
Console.WriteLine(sum.NumericText); // Redis numeric text, without double rounding.

RespireArrayAggregateResult used = await redis.Arrays.AggregateAsync(
    "samples", 0, 100, RespireArrayOperation.Used);
Console.WriteLine(used.Integer);

await redis.Arrays.InsertAsync("events", "first", "second");
ulong? next = await redis.Arrays.NextIndexAsync("events");
await redis.Arrays.SeekAsync("events", 10);
await redis.Arrays.RingAsync("recent", 3, "a", "b", "c", "d");
string?[] recent = await redis.Arrays.LastItemsAsync("recent", 3);
// ["b", "c", "d"]
```

`AggregateAsync`:

- SUM/MIN/MAX return the server's numeric text (`NumericText`); AND/OR/XOR/MATCH/USED return signed integers (`Integer`).
- Empty input gives `IsNull` for numeric and bitwise operations, and zero for MATCH and USED.
- MATCH requires the `match` argument.
- The range is traversed descending when `start > end`.

Insertion cursor:

- `InsertAsync` writes at the cursor and returns the last written index. `SetAsync` and `SetManyAsync` do not move it.
- `NextIndexAsync` returns zero before any insertion or for a missing key, and null once exhausted.
- `SeekAsync` returns false for a missing key.

Ring buffers:

- `RingAsync` wraps writes into a positive size and returns the last written slot. Resizing keeps the latest contiguous tail; a hole ends it.
- `LastItemsAsync` returns recent positions, holes included, oldest first unless `reverse: true`. Redis 8.10 caps the count at the number of populated slots; zero or negative counts return an empty array.

`InfoAsync(key, full: true)` returns typed ARINFO metadata and optional slice statistics. Missing keys raise a server error. ARINFO reports insertion position zero when the cursor is exhausted; use `NextIndexAsync` to tell that apart.

## Batches, transactions, and the fake server

`batch.Arrays` and `transaction.Arrays` expose the same single-command methods without `Async`. Use `ScanPage` in a queue; streaming `ScanAsync` is immediate-only. Batches copy argument spans when queued, but referenced byte buffers must not change until execution completes. Transactions serialize arguments when queued.

`Respire.Testing` supports the whole command family. Its limits:

- ARINFO storage statistics are zero.
- AROP arithmetic is bounded to .NET `decimal`; values outside it return an error.
- ARGREP `RE` supports printable ASCII and a common regex subset: no escapes, POSIX classes, or .NET-specific groups.
- Glob matching has a one-million-step budget per command; exceeding it returns an error.

Use real Redis for allocator statistics, long-double precision, and full POSIX regex.
