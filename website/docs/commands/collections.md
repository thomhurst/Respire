---
title: Collections and streams
description: Work with hashes, lists, sets, sorted sets, and streams.
---

# Collections and streams

Collection commands are grouped by Redis data type. Method names omit the Redis prefix because the facet supplies the context. Bitmap, HyperLogLog, and geo operations also have typed facets.

Membership checks use `ExistsAsync` for a key or named hash field and `ContainsAsync` for a member value. Batch and transaction facets follow the same rule without the `Async` suffix.

```csharp
bool keyExists = await redis.Keys.ExistsAsync("user:42");
bool fieldExists = await redis.Hashes.ExistsAsync("user:42", "name");
bool containsMember = await redis.Sets.ContainsAsync("team:red", "ada");
```

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

Pre-release migration: replace `Hashes.DeleteAsync` and `Streams.DeleteAsync` with
`RemoveAsync`, and `Hashes.GetAndDeleteAsync` with `GetAndRemoveAsync`. Deferred hash commands
use `Remove` and `GetAndRemove`. Whole-key deletion and stream group/consumer lifecycle methods
keep their `Delete` names.

`LengthAsync(key, field)` returns the value's byte length (`HSTRLEN`), including zero for
an empty or missing field. `RandomFieldAsync` returns one field or null for a missing hash.
`RandomFieldsAsync` and `RandomFieldsWithValuesAsync` accept a count: positive counts return
up to that many distinct fields, negative counts allow repeats, and zero returns an empty
array. The field/value result is an array of pairs so repeated fields are preserved.
Bound the magnitude of `count` to control reply size and allocation: a negative count can
return more entries than the hash contains because fields may repeat.
These random selection methods require Redis 6.2 or later.

```csharp
long nameBytes = await redis.Hashes.LengthAsync("user:42", "name");
string? randomField = await redis.Hashes.RandomFieldAsync("user:42");
string[] randomFields = await redis.Hashes.RandomFieldsAsync("user:42", count: 2);
KeyValuePair<string, string>[] samples =
    await redis.Hashes.RandomFieldsWithValuesAsync("user:42", count: -5);

RespireExpiryTime[] expiryTimes = await redis.Hashes.ExpiryTimeAsync("user:42", "name", "role");
RespireExpiryTime[] secondResolution = await redis.Hashes.ExpiryTimeAsync(
    "user:42", ExpiryTimePrecision.Seconds, "name", "role");
```

`ExpiryTimeAsync` requires Redis 7.4 or later. It uses `HPEXPIRETIME` by default; request
`ExpiryTimePrecision.Seconds` to use `HEXPIRETIME`, which rounds fractional seconds up.
Results follow input order, including
repeated fields. `Exists` distinguishes missing fields from persistent fields; `HasExpiry`
indicates an expiration is set. `UnixTimeMilliseconds` always uses milliseconds, including
when requesting whole-second resolution, and is null when missing or persistent. The numeric
timestamp preserves Redis values beyond the range of `DateTimeOffset`. Use `GetExpiresAt()` for a
nullable UTC `DateTimeOffset`; that conversion throws if a timestamp exceeds its supported range.
Use `TryGetExpiresAt(out DateTimeOffset expiresAt)` for a nonthrowing conversion; it returns
false for missing/persistent fields and timestamps outside the supported range.

All these methods also exist on batch and transaction hash facets without the `Async` suffix.

## Lists

```csharp
await redis.Lists.RightPushAsync("jobs", "invoice:42", "invoice:43");
string? next = await redis.Lists.LeftPopAsync("jobs");
string[] nextBatch = await redis.Lists.LeftPopManyAsync("jobs", count: 128);
string[] pending = await redis.Lists.RangeAsync("jobs", 0, 99);
```

Search with `PositionAsync` for one index or `PositionsAsync` for multiple indexes (Redis 6.0.6+).
A missing match returns `null` or an empty array respectively. Indexes always count from the head;
negative `rank` searches from the tail and returns matches in that search order. `rank` cannot be
zero or `long.MinValue`. `count: 0` returns all matches, and `maxLength: 0` removes the scan limit;
negative counts and scan limits are rejected.

```csharp
long? position = await redis.Lists.PositionAsync("jobs", "invoice:42", rank: -1);
long[] positions = await redis.Lists.PositionsAsync("jobs", "invoice:42", count: 10, maxLength: 1000);
long inserted = await redis.Lists.InsertBeforeAsync("jobs", "invoice:43", "invoice:42a");
await redis.Lists.InsertAfterAsync("jobs", "invoice:43", "invoice:43a");
await redis.Lists.SetAsync("jobs", -1, "invoice:44");
long existingLength = await redis.Lists.RightPushIfExistsAsync("jobs", "invoice:45", "invoice:46");
```

`InsertBeforeAsync` and `InsertAfterAsync` return the new length, `0` for a missing key, or `-1`
for a missing pivot. They target the first matching pivot. `SetAsync` accepts negative indexes
and preserves Redis errors for missing keys or out-of-range indexes. `LeftPushIfExistsAsync`
and `RightPushIfExistsAsync` require at least one value and return `0` without creating a missing
list. Values and pivots accept binary `RespireValue` inputs. Every method also exists on batch
and transaction `Lists` facets without the `Async` suffix; queued push methods snapshot the
argument span, while supplied byte buffers must remain unchanged until execution finishes.

Set `waitFor` to transparently select the blocking command and a dedicated connection. See [blocking queues](../guides/blocking-queues).

## Sets

```csharp
await redis.Sets.AddAsync("team:red", "ada", "grace");
await redis.Sets.AddAsync("on-call", "ada");

string[] both = await redis.Sets.IntersectAsync("team:red", "on-call");
bool member = await redis.Sets.ContainsAsync("team:red", "ada");
bool[] membership = await redis.Sets.ContainsManyAsync("team:red", "ada", "missing", "ada");
long sharedCount = await redis.Sets.IntersectCountAsync("team:red", "on-call");
long atMostOne = await redis.Sets.IntersectCountAsync(limit: 1, "team:red", "on-call");
bool moved = await redis.Sets.MoveAsync("team:red", "team:blue", "grace");
string? random = await redis.Sets.PopAsync("available");
string[] randomBatch = await redis.Sets.PopManyAsync("available", count: 2);
```

`ContainsManyAsync` preserves input order and duplicates; missing members produce `false`.
It maps to [SMISMEMBER](https://redis.io/docs/latest/commands/smismember/) (Redis 6.2+).
`MoveAsync` atomically transfers one member and returns `false` if it was absent from the
source, including when the source key is missing. An existing destination member still
counts as a successful move from the source.

`IntersectCountAsync` uses [SINTERCARD](https://redis.io/docs/latest/commands/sintercard/)
(Redis 7+) without fetching members. The optional limit overload puts `limit` before the
variadic keys; zero means unlimited, and negative limits are rejected. Membership checks
require at least one member; intersection counts require at least one key. For cancellation,
pass a key/member span and a required trailing token, for example
`IntersectCountAsync(1, ["team:red", "on-call"], cancellationToken)`.

Batch and transaction facets expose `ContainsMany`, `Move`, and `IntersectCount` with the
same results through `RespirePending<T>`. Redis Cluster requires all keys in a move or
intersection to share a hash slot. Key prefixes apply to every source and destination key.

Count-based pops use `PopManyAsync` and return an empty array when the key is missing.
Single-member `PopAsync` returns null when missing. Batch and transaction facets use
`PopMany` for arrays and `Pop` for scalars. Pre-release count-based `PopAsync`/`Pop` overloads
are removed; rename those calls without changing their arguments.

## Sorted sets

```csharp
await redis.SortedSets.AddAsync("scores", "ada", 98.5);
await redis.SortedSets.AddAsync("scores", ("grace", 97.5), ("linus", 96.0));
await redis.SortedSets.IncrementAsync("scores", "ada", 1.5);
double?[] scores = await redis.SortedSets.ScoresManyAsync("scores", "ada", "missing");
SortedSetEntry? next = await redis.SortedSets.PopAsync("ready:scores");
SortedSetEntry[] nextBatch = await redis.SortedSets.PopManyAsync("ready:scores", count: 2);
SortedSetEntry<int>[] nextPlayers = await redis.SortedSets.PopManyAsync<int>("player:scores", count: 2);
SortedSetEntry<int>? highest = await redis.SortedSets.PopAsync<int>("player:scores", descending: true);

SortedSetEntry[] top = await redis.SortedSets.RangeWithScoresAsync(
    "scores",
    start: 0,
    stop: 9,
    descending: true);

var finalists = new RespireScoreRange(
    RespireScoreBound.Exclusive(90),
    RespireScoreBound.Max);
SortedSetEntry[] page = await redis.SortedSets.RangeByScoreWithScoresAsync(
    "scores",
    finalists,
    offset: 0,
    count: 10,
    descending: true);

var names = new RespireLexRange("a", RespireLexBound.Exclusive("m"));
string[] alphabetical = await redis.SortedSets.RangeByLexAsync("names", names);
long stored = await redis.SortedSets.StoreRangeByScoreAsync(
    "finalists",
    "scores",
    finalists,
    count: 100,
    descending: true);

string[] combined = await redis.SortedSets.UnionAsync("regional:uk", "regional:eu");
long combinedCount = await redis.SortedSets.UnionStoreAsync(
    "regional:all", "regional:uk", "regional:eu");

SortedSetEntry<int>[] players =
    await redis.SortedSets.RangeWithScoresAsync<int>("player:scores", descending: true);
```

Score and lex boundaries are inclusive by default. Use `Exclusive(...)` for an open boundary,
or `Min` / `Max` for negative and positive infinity. Supplying `offset` requires `count` because
Redis emits them together as `LIMIT offset count`. The same range APIs are available on batches
and transactions without the `Async` suffix. Sorted-set intersection, union, and difference each
have read and `Store` forms. Typed rank ranges, score ranges, and pops deserialize members while
preserving their scores in `SortedSetEntry<T>`.

Bulk adds accept `(RespireValue Member, double Score)` tuples. Members can be text, numbers,
or raw bytes, just like single-member adds. `SortedSetEntry` is a read result; replace
`new SortedSetEntry(member, score)` write inputs with `(member, score)`. Batches and transactions
accept the same tuples through `SortedSets.Add`.

Byte-backed members borrow the caller's memory. For batches and transactions, keep those bytes
unchanged until `ExecuteAsync` or `CommitAsync` completes. If you must reuse a buffer earlier,
copy it before queuing, for example `(buffer.ToArray(), score)`.

```csharp
byte[] member = [0xff, 0x00, 0x80];
await redis.SortedSets.AddAsync("binary:scores", (member, 1.5), ("text", 2.5));

// Use an explicit tuple array when retaining inputs for reuse or passing cancellation.
(RespireValue Member, double Score)[] entries = [(member, 3.5), ("text", 4.5)];
await redis.SortedSets.AddAsync("binary:scores", entries, cancellationToken);
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

RespireStreamClaimResult recovered = await redis.Streams.ClaimPendingAsync(
    "events",
    group: "processors",
    consumer: Environment.MachineName,
    minIdle: TimeSpan.FromMinutes(1),
    count: 100,
    cancellationToken: stoppingToken);

foreach (var entry in recovered.Entries)
{
    await HandleAsync(entry.GetString("type"));
    await entry.AckAsync(stoppingToken);
}
```

Blocking stream reads use the same dedicated-connection mechanism as blocking list operations.
Use `ClaimAsync` when the pending ids are already known. `ClaimPendingAsync` exposes the
`XAUTOCLAIM` next position and deleted ids for iterative recovery. To replay the current
consumer's own pending-entry list, call `ReadGroupAsync` with an explicit `startAt`; that replay
is non-blocking and completes after the pending entries are exhausted.

## Bitmaps, HyperLogLogs, and geo indexes

```csharp
bool wasActive = await redis.Bitmaps.SetAsync("active:2026-08-09", userId, true);
long active = await redis.Bitmaps.SetBitCountAsync("active:2026-08-09");
long? firstActive = await redis.Bitmaps.PositionAsync("active:2026-08-09", true);
long?[] bytes = await redis.Bitmaps.FieldReadOnlyAsync(
    "packed:counters",
    BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), BitFieldOffset.Fields(2)));
long?[] updated = await redis.Bitmaps.FieldAsync(
    "packed:counters",
    BitFieldOperation.Set(BitFieldEncoding.Unsigned(8), BitFieldOffset.Bits(16), 10),
    BitFieldOperation.Increment(BitFieldEncoding.Unsigned(8), BitFieldOffset.Fields(2), 1));

await redis.HyperLogLog.AddAsync("visitors", sessionId);
long estimate = await redis.HyperLogLog.CountAsync("visitors");

await redis.Geo.AddAsync("cafes", new GeoEntry(-0.1276, 51.5072, "london"));
GeoSearchResult[] nearby = await redis.Geo.SearchAsync(
    "cafes",
    GeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    GeoSearchShape.Circle(10, GeoUnit.Kilometers));
```

BITFIELD operations use `BitFieldEncoding.Signed(width)` (1–64 bits) or `Unsigned(width)` (1–63 bits). `BitFieldOffset.Bits(n)` is an absolute bit offset; `Fields(n)` multiplies the index by the encoding width (Redis `#n`). Both offset factories reject negative values. The default offset is bit zero. `Get`, `Set`, and `Increment` all accept these types; `FieldReadOnlyAsync` accepts only `Get` operations.

For uncommon operations and modules, use the [complete command catalog](../guides/raw-commands).
