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

## Binary key serialization

`Keys.DumpAsync(key)` returns an owned `byte[]`, or `null` when the key is missing.
The bytes are Redis's serialized representation, not a Respire serializer payload, and
remain valid after reply or batch disposal. [DUMP](https://redis.io/docs/latest/commands/dump/)
does not include expiry; read `Keys.ExpiryAsync` separately when needed.

```csharp
byte[]? payload = await redis.Keys.DumpAsync("source");
if (payload is not null)
{
    await redis.Keys.RestoreAsync("copy", payload,
        expiry: TimeSpan.FromMinutes(5),
        options: new RespireRestoreOptions { Replace = true });
}
```

`RestoreAsync` returns true on `OK`. The default expiry and `RespireExpiry.Persist`
create a persistent key, including when replacing an expiring key. A relative expiry must
remain positive after millisecond truncation; an absolute `DateTimeOffset` must be after
the Unix epoch and uses `ABSTTL`. Past positive timestamps expire immediately.
`RespireExpiry.Keep`, nonpositive relative milliseconds, and nonpositive absolute Unix
milliseconds are rejected before sending or enqueueing. This avoids Redis's special zero
TTL silently making an intended expiry persistent.

`RespireRestoreOptions` supports `Replace`, nonnegative `IdleTimeSeconds`, and byte-valued
`Frequency` (0–255). Idle time and frequency are mutually exclusive and apply to LRU and
LFU eviction respectively. [RESTORE](https://redis.io/docs/latest/commands/restore/) requires
Redis 2.6+, `REPLACE` requires 3.0+, and `ABSTTL`/`IDLETIME`/`FREQ` require 5.0+.
Payload checksum/version/format errors and existing-key errors remain Redis server errors.

Batch and transaction facets expose `Keys.Dump` and `Keys.Restore` with the same options.
RESTORE borrows its input memory: keep its bytes unchanged until execution completes.
Typed commands apply the view's key prefix. DUMP does not invalidate cached data;
RESTORE invalidates its target key through the existing mutation handling.

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

Multi-key pops return the selected list along with its values. `PopManyAsync` uses LMPOP
(Redis 7.0+) and selects the first nonempty list in input order. `side` controls which end of
that list is popped; `count` must be positive. Supplying `waitFor` selects BLMPOP instead.
`PopAsync` always waits using BLPOP or BRPOP and returns one value. All return `null` when no
list is available before the timeout. Pass `Timeout.InfiniteTimeSpan` to wait indefinitely;
zero waits use the same minimum one-millisecond Redis timeout as single-key list pops.

```csharp
RespireListPopManyResult? jobs = await redis.Lists.PopManyAsync(
    ["{jobs}:urgent", "{jobs}:normal"], count: 10, side: ListSide.Left);
RespireListPopResult? job = await redis.Lists.PopAsync(
    ["{jobs}:urgent", "{jobs}:normal"], waitFor: TimeSpan.FromSeconds(5));
if (job is { } popped)
{
    long remaining = await redis.Lists.CountAsync(popped.Key);
}
```

Returned keys preserve binary bytes and own their storage. A prefixed view removes its literal
prefix from the returned key so it can be passed back through that view. Values are decoded as
UTF-8, matching the existing list pop methods. Cluster calls require every input key to share a
slot after prefixing; empty Redis keys remain valid. Blocking calls use dedicated pooled
connections, and cancellation discards a blocked connection without stalling multiplexed traffic.

Batch and transaction `Lists.PopMany(keys, count, side)` queue the nonblocking LMPOP command.
The key span is copied into command arguments; caller-owned byte buffers must remain unchanged
until execution finishes. Returned key bytes and value strings survive deferred response disposal.
Blocking pops have no batch or transaction form.

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

`RandomMemberAsync(key)` returns one member or `null`; `RandomMembersAsync(key, count)`
and `RandomMembersWithScoresAsync(key, count)` return owned arrays (Redis 6.2+). Positive
counts select distinct members up to the set size; negative counts allow duplicates and
return exactly the absolute count for nonempty sets. Zero returns empty. `long.MinValue`
is rejected because its absolute value cannot fit in a signed 64-bit integer. Sampling
never removes members. Generic variants deserialize members, including binary-safe `byte[]`.
A missing scalar generic result is `default(T)`; counted missing results are empty arrays.

`CountByLexAsync` and `RemoveRangeByLexAsync` accept `RespireLexRange`, with inclusive,
exclusive, or infinite bounds. Lexicographical operations require all members to have the
same score. `IntersectCountAsync` (Redis 7+) returns only intersection cardinality; a
nonnegative `limit` caps work and the returned count, while zero means unlimited. At least
one key is required, and Cluster keys must share a slot after the client prefix is applied.

```csharp
SortedSetEntry[] sample = await redis.SortedSets.RandomMembersWithScoresAsync("scores", 3);
long common = await redis.SortedSets.IntersectCountAsync(100, "{scores}:today", "{scores}:yesterday");
var range = new RespireLexRange(RespireLexBound.Inclusive("a"), RespireLexBound.Exclusive("m"));
long matches = await redis.SortedSets.CountByLexAsync("names", range);
long removed = await redis.SortedSets.RemoveRangeByLexAsync("names", range);
```

Every method also has a batch/transaction mirror without `Async`. Deferred result arrays
remain valid after response disposal. Key spans are snapshotted; referenced byte buffers
must remain unchanged until execution completes, as with other deferred commands.

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

### Trimming

Use `StreamAddOptions.MinId` or `MaxLength` to trim while appending. Use `Streams.TrimAsync`
with `StreamTrimOptions` to trim an existing stream and receive the number of entries removed.
`MinId` removes entries with lower numeric IDs; `MaxLength` retains the newest entries up to
the requested length. The thresholds are mutually exclusive. `TrimAsync` requires one;
`AddAsync` leaves the stream untrimmed when neither is supplied.

```csharp
await redis.Streams.AddAsync("events", new StreamAddOptions
{
    MinId = "1700000000000-0", Limit = 1000,
}, ("type", "updated"));

long removed = await redis.Streams.TrimAsync("events", new StreamTrimOptions
{
    MinId = "1700000000000-0", Approximate = true, Limit = 0,
});
```

`StreamAddOptions.ApproximateTrim` defaults to `true`; `StreamTrimOptions.Approximate` and
the existing `TrimByMaxLengthAsync` shortcut default to `false`. Approximate trimming removes
whole internal stream nodes and can retain entries beyond the threshold. `Limit` is allowed
only with approximate trimming and a threshold; it bounds trimming work rather than promising
an exact removed count. Zero disables the work limit, while null leaves the server default.
Lengths and limits must be non-negative, and `MinId` accepts numeric IDs (including a
milliseconds-only ID), not range or consumer-read sentinels. Invalid combinations fail before I/O.

MINID and LIMIT require Redis 6.2+; MAXLEN is available from Redis 5.0. Unsupported servers
return their normal command errors. See the [XADD](https://redis.io/docs/latest/commands/xadd/)
and [XTRIM](https://redis.io/docs/latest/commands/xtrim/) references. Existing MAXLEN overloads
retain their signatures and defaults.

Batches and transactions expose the same options through `Streams.Add` and `Streams.Trim`.
They validate at enqueue time and copy or serialize supplied keys and values then. Immediate
calls borrow binary keys and field values until their returned operation completes. Stream IDs
are immutable strings. Prefixing and Cluster slot routing apply to the stream key only.
Custom `IStreamCommands` and `IBatchStreamCommands` implementations must add `TrimAsync` and
`Trim`, respectively; `StreamAddOptions` equality includes `MinId` and `Limit`.

### Reading without consumer groups

```csharp
RespireStreamEntry[] entries = await redis.Streams.ReadAsync("events", after: "0", count: 100);
RespireStreamReadResult[] streams = await redis.Streams.ReadAsync(
    [("{jobs}:events", "12-0"), ("{jobs}:audit", "8-0")],
    count: 100, waitFor: TimeSpan.FromSeconds(5), cancellationToken: stoppingToken);

await foreach (var item in redis.Streams.ReadAllAsync(
    [("{jobs}:events", "12-0"), ("{jobs}:audit", "8-0")], cancellationToken: stoppingToken))
{
    await HandleAsync(item.Entry.GetString("type"));
    // Persist item.Key and item.Entry.Id if this application needs a durable checkpoint.
}
```

`ReadAsync` implements [XREAD](https://redis.io/docs/latest/commands/xread/) (Redis 5.0+).
The default start id is `0`; only entries newer than each supplied id are returned. `count`
limits entries **per stream**. Missing/empty streams are omitted from multi-stream results;
an empty or timed-out response returns an empty array. Keys, ids, field names, and binary
values are owned after the call or deferred execution completes. Returned keys have the
client prefix removed. Cluster calls require all effective, prefixed keys in one hash slot.

Omit `waitFor` for a nonblocking read. A finite wait rounds up to milliseconds (zero becomes
one millisecond); `Timeout.InfiniteTimeSpan` sends `BLOCK 0`. Blocking reads use dedicated
connections, leaving ordinary commands responsive. Pass cancellation to interrupt a wait.
Batches and transactions expose only the nonblocking `Streams.Read` forms.

`ReadAllAsync` keeps an independent last-delivered id for each stream. It drains each owned
batch before reading again, using one-second blocking polls. Transient connection/server
failures retry indefinitely until cancellation/disposal, with delays from 100 ms to 3.2 seconds;
authentication, ACL, configuration,
and other non-transient errors terminate enumeration. Cancellation interrupts reads and retry
delays; disposing an enumerator between entries releases its buffered batch. Blocking polls
are exempt from `CommandTimeout`; the caller's cancellation token bounds an active wait.

For a one-shot read, `RespireStreamId.New` (`$`) uses Redis's current tail. For enumeration,
each `$` is resolved once using `XREVRANGE ... COUNT 1` before the first `XREAD` (requiring
`XREVRANGE` permission). An empty stream starts at `0`. An initial lookup failure is surfaced
without retry, so reconnecting cannot silently move the starting point forward. Multi-stream
tail lookups happen independently, not atomically. Numeric ids avoid this extra lookup.

Enumeration checkpoints are in memory and advance when an entry is delivered, not when your
handler succeeds. Persist checkpoints yourself for process restarts. Trimming, deleting, or
recreating a stream during an outage can remove unread entries; reconnect does not guarantee
lossless delivery or detect every gap. These reads do not create pending entries or support
consumer-group acknowledgement.

### Consumer groups

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

### Stream metadata

`Streams.CreateConsumerAsync(key, group, consumer)` explicitly creates a consumer in
an existing stream group (Redis 6.2+, `XGROUP CREATECONSUMER`). It returns `true` for
a new consumer and `false` for an existing consumer. It does not read entries or
create a missing stream or group; missing groups and wrong key types are server errors.

`Streams.SetLastIdAsync(key, lastId, entriesAdded: ..., maxDeletedId: ...)` exposes
`XSETID` (Redis 5.0+). Both optional metadata arguments require Redis 7.0+ and can be
supplied independently. Omitted metadata remains unchanged. This is an advanced
restoration operation: Redis documents XSETID as an internal replication command.
It changes the existing stream's last-generated ID without adding entries, trimming
entries, or changing a consumer group's position. Use `SetGroupPositionAsync` for
the group's last-delivered ID instead.

Negative `entriesAdded` values fail locally. Redis validates IDs, rejects a last ID
below the stream's highest existing entry, an entries-added count below its length,
or a supplied maximum-deleted ID above the last ID. Redis 7.0 treats a supplied
maximum-deleted ID of `0-0` as leaving that metadata unchanged.
Server/version/permission errors propagate without emulation.

Both methods accept cancellation, apply the client view's key prefix once, and route
to that key's Cluster owner. Group and consumer names are not prefixed. Existing
stream entries and binary field values are unchanged. These immediate metadata
methods follow the existing group-management APIs; they are not batch operations.

See Redis's [CREATECONSUMER](https://redis.io/docs/latest/commands/xgroup-createconsumer/)
and [XSETID](https://redis.io/docs/latest/commands/xsetid/) references.

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
