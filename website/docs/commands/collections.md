---
title: Collections and streams
description: Work with hashes, lists, sets, sorted sets, and streams.
---

# Collections and streams

Each Redis data type has a facet: `Hashes`, `Lists`, `Sets`, `SortedSets`, `Streams`, `Bitmaps`, `HyperLogLog`, and `Geo`. Method names omit the Redis prefix. Use `ExistsAsync` for a key or hash field, and `ContainsAsync` for a member value.

```csharp
bool keyExists = await redis.Keys.ExistsAsync("user:42");
bool fieldExists = await redis.Hashes.ExistsAsync("user:42", "name");
bool containsMember = await redis.Sets.ContainsAsync("team:red", "ada");
```

Rules that apply across this page unless a section says otherwise:

- Batch and transaction facets have the same methods without the `Async` suffix.
- Queued commands copy key spans when queued. Caller-owned byte buffers must stay unchanged until execution completes.
- Results, including deferred ones, are owned and stay valid after response disposal.
- Key prefixes apply to keys only, not to fields, members, scan match patterns, or group and consumer names.
- In Cluster, all keys in one command must share a hash slot after prefixing.
- Unsupported servers return their normal Redis errors; Respire does not emulate missing commands.

## Binary key serialization

`Keys.DumpAsync(key)` returns Redis's serialized bytes as an owned `byte[]`, or `null` for a missing key. These are not Respire serializer payloads. [DUMP](https://redis.io/docs/latest/commands/dump/) omits expiry; read `Keys.ExpiryAsync` separately.

```csharp
byte[]? payload = await redis.Keys.DumpAsync("source");
if (payload is not null)
{
    await redis.Keys.RestoreAsync("copy", payload,
        expiry: TimeSpan.FromMinutes(5),
        options: new RespireRestoreOptions { Replace = true });
}
```

`RestoreAsync` returns true on `OK`. Expiry rules:

- The default and `RespireExpiry.Persist` create a persistent key, even when replacing an expiring key.
- A relative expiry must stay positive after millisecond truncation.
- An absolute `DateTimeOffset` must be after the Unix epoch and sends `ABSTTL`. Past timestamps expire immediately.
- `RespireExpiry.Keep` and nonpositive values are rejected before sending, so Redis's zero TTL cannot silently make the key persistent.

| `RespireRestoreOptions` | Meaning | Redis |
|---|---|---|
| `Replace` | Overwrite an existing key. | 3.0+ |
| `IdleTimeSeconds` | Nonnegative LRU idle time. | 5.0+ |
| `Frequency` | LFU frequency, 0–255. | 5.0+ |

`IdleTimeSeconds` and `Frequency` are mutually exclusive. [RESTORE](https://redis.io/docs/latest/commands/restore/) requires Redis 2.6+; `ABSTTL` requires 5.0+. Bad payloads and existing-key conflicts return Redis server errors.

Queued forms are `Keys.Dump` and `Keys.Restore`. RESTORE borrows its input bytes until execution completes. DUMP does not invalidate cached data; RESTORE invalidates its target key.

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

- Methods that remove fields or stream entries are `RemoveAsync` and `Hashes.GetAndRemoveAsync`. `Delete` names mean whole-key deletion or stream group/consumer lifecycle.
- `LengthAsync(key, field)` returns the value's byte length (`HSTRLEN`), or zero for an empty or missing field.
- `RandomFieldAsync` returns one field, or null for a missing hash.
- `RandomFieldsAsync` and `RandomFieldsWithValuesAsync` take a count. Positive returns up to that many distinct fields, negative allows repeats, zero returns an empty array. Field/value results are an array of pairs, so repeats are kept.
- Bound `count`: a negative count can return more entries than the hash holds.
- The random methods require Redis 6.2+.

```csharp
long nameBytes = await redis.Hashes.LengthAsync("user:42", "name");
string? randomField = await redis.Hashes.RandomFieldAsync("user:42");
string[] randomFields = await redis.Hashes.RandomFieldsAsync("user:42", count: 2);
KeyValuePair<string, string>[] samples =
    await redis.Hashes.RandomFieldsWithValuesAsync("user:42", count: -5);

RespireExpiryTimeResult[] expiryTimes = await redis.Hashes.ExpiryTimeAsync("user:42", "name", "role");
RespireExpiryTimeResult[] secondResolution = await redis.Hashes.ExpiryTimeAsync(
    "user:42", RespireExpiryTimePrecision.Seconds, "name", "role");
```

`ExpiryTimeAsync` (Redis 7.4+) uses `HPEXPIRETIME`. Pass `RespireExpiryTimePrecision.Seconds` for `HEXPIRETIME`, which rounds fractional seconds up. Results follow input order, including repeated fields.

For a single field's remaining lifetime, `Hashes.ExpiryAsync(key, field, cancellationToken)` returns one `RespireTtlResult`; a string field and a `RespireKey` field both select the scalar result. Pass a `string[]` or `ReadOnlySpan<string>` for the array result, even when the collection contains one field. Other collection commands such as `GetManyAsync` retain their array result when called with a single item and a cancellation token.

| `RespireExpiryTimeResult` member | Meaning |
|---|---|
| `Exists` | False for a missing field. |
| `HasExpiry` | True when an expiration is set. |
| `UnixTimeMilliseconds` | Always milliseconds, even with `Seconds` precision. Null when missing or persistent. Holds values beyond `DateTimeOffset` range. |
| `GetExpiresAt()` | Nullable UTC `DateTimeOffset`. Throws when the timestamp is out of range. |
| `TryGetExpiresAt(out DateTimeOffset expiresAt)` | Returns false for missing or persistent fields and out-of-range timestamps. |

### Scanning fields

`ScanFieldsAsync` enumerates field names without values, using [`HSCAN NOVALUES`](https://redis.io/docs/latest/commands/hscan/) (Redis 7.4+). `ScanAsync` returns field/value pairs.

```csharp
await foreach (var field in redis.Hashes.ScanFieldsAsync("user:42", match: "profile:*", countHint: 100))
{
    Console.WriteLine(field);
}
```

For manual pages, `ScanFieldsPageAsync(key, cursor, match, countHint)` returns a `RespireHashScanPage` with `Fields`, an unsigned 64-bit `Cursor`, and `IsComplete`:

1. Start with cursor zero.
2. Pass each returned cursor to the next call.
3. Stop when `IsComplete` is true. An empty page with a nonzero cursor is not the end.

A null count omits `COUNT`; enumeration defaults to 250. Counts are hints, not page-size limits. Queued `Hashes.ScanFieldsPage(...)` returns the page as a pending result: execute and read it before queuing the next page. Queued pages use the queue's cache policy.

Scans are not snapshots. Duplicates are possible, and concurrent changes may or may not appear. Across pages:

- Keep the same key, match, execution form, and read policy.
- Restart from zero after topology changes.
- Use a primary read view if you move pages between execution forms.
- A continuation fails, rather than moving servers, if its replica is lost.
- A replica continuation cannot share a Cluster batch group with writes, which would route the group to the primary. Standalone batches and transactions run on the primary.

## Lists

```csharp
await redis.Lists.RightPushAsync("jobs", "invoice:42", "invoice:43");
string? next = await redis.Lists.LeftPopAsync("jobs");
string[] nextBatch = await redis.Lists.LeftPopManyAsync("jobs", count: 128);
string[] pending = await redis.Lists.RangeAsync("jobs", 0, 99);
```

`PositionAsync` returns one index or `null`. `PositionsAsync` returns an array, empty when nothing matches (Redis 6.0.6+). Indexes always count from the head.

- Negative `rank` searches from the tail and returns matches in that order. `rank` cannot be zero or `long.MinValue`.
- `count: 0` returns all matches; `maxLength: 0` removes the scan limit. Negative values are rejected.

```csharp
long? position = await redis.Lists.PositionAsync("jobs", "invoice:42", rank: -1);
long[] positions = await redis.Lists.PositionsAsync("jobs", "invoice:42", count: 10, maxLength: 1000);
long inserted = await redis.Lists.InsertBeforeAsync("jobs", "invoice:43", "invoice:42a");
await redis.Lists.InsertAfterAsync("jobs", "invoice:43", "invoice:43a");
await redis.Lists.SetAsync("jobs", -1, "invoice:44");
long existingLength = await redis.Lists.RightPushIfExistsAsync("jobs", "invoice:45", "invoice:46");
```

- `InsertBeforeAsync` and `InsertAfterAsync` target the first matching pivot. They return the new length, `0` for a missing key, or `-1` for a missing pivot.
- `SetAsync` accepts negative indexes. Missing keys and out-of-range indexes return Redis errors.
- `LeftPushIfExistsAsync` and `RightPushIfExistsAsync` need at least one value. They return `0` without creating a missing list.
- Values and pivots accept binary `RespireValue` inputs.

### Multi-key pops

Multi-key pops return the selected key with its values, or `null` when no list has data before the timeout.

- `PopManyAsync` uses LMPOP (Redis 7.0+). It pops up to `count` (positive) from the `side` of the first nonempty list, in input order. Supplying `waitFor` uses BLMPOP.
- `PopAsync` always waits, using BLPOP or BRPOP, and returns one value.
- Explicit `waitFor: null` waits indefinitely; omit `waitFor` for an immediate call. A zero wait uses Redis's minimum one-millisecond timeout.

```csharp
RespireListPopManyResult? jobs = await redis.Lists.PopManyAsync(
    ["{jobs}:urgent", "{jobs}:normal"], count: 10, side: RespireListSide.Left);
RespireListPopResult? job = await redis.Lists.PopAsync(
    ["{jobs}:urgent", "{jobs}:normal"], waitFor: TimeSpan.FromSeconds(5));
if (job is { } popped)
{
    long remaining = await redis.Lists.CountAsync(popped.Key);
}
```

Returned keys keep their binary bytes. A prefixed view strips its prefix, so you can pass the key back through that view. Values decode as UTF-8. Empty Redis keys are valid. Blocking calls use dedicated connections; cancellation discards the blocked connection without stalling other traffic.

Queued `Lists.PopMany(keys, count, side)` sends nonblocking LMPOP. Blocking pops have no queued form.

### Moving several elements (Redis 8.10+)

`MoveManyAsync` uses LMOVEM, or BLMOVEM when `waitFor` is set. It returns `null` for a missing source, an unmet exact count, or a timeout.

| `countMode` | Without `waitFor` | With `waitFor` |
|---|---|---|
| `UpTo` | Moves up to `count` available elements. | Waits for at least one. |
| `Exactly` | Moves nothing unless `count` are available. | Waits for `count`. |

```csharp
string[]? moved = await redis.Lists.MoveManyAsync(
    "{jobs}:ready", "{jobs}:processing", count: 10,
    from: RespireListSide.Left, to: RespireListSide.Right,
    countMode: RespireListMoveCountMode.Exactly, order: RespireListMoveOrder.Bulk,
    waitFor: TimeSpan.FromSeconds(5));
```

- The result holds UTF-8 strings in destination order.
- `Bulk` keeps source order. `OneByOne` uses pop order, reversed when inserting at the head.
- When source and destination are the same key, Redis removes the block before reinserting it. A same-end `OneByOne` move reverses the block; an opposite-end move can rotate the list.
- Waits behave as for multi-key pops.
- Queued `Lists.MoveMany` sends only LMOVEM and never waits.

See the [LMOVEM](https://redis.io/docs/latest/commands/lmovem/) and [BLMOVEM](https://redis.io/docs/latest/commands/blmovem/) references. For `waitFor` and dedicated connections, see [blocking queues](../guides/blocking-queues.md).

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

- `ContainsManyAsync` ([SMISMEMBER](https://redis.io/docs/latest/commands/smismember/), Redis 6.2+) keeps input order and duplicates. Missing members are `false`. It needs at least one member.
- `MoveAsync` moves one member atomically. It returns `false` if the member was not in the source, including a missing source. A member already in the destination still counts as moved.
- `IntersectCountAsync` ([SINTERCARD](https://redis.io/docs/latest/commands/sintercard/), Redis 7+) counts without fetching members. The limit overload puts `limit` before the keys; zero means unlimited, and negative is rejected. It needs at least one key.
- For cancellation, pass a key span and a trailing token: `IntersectCountAsync(1, ["team:red", "on-call"], cancellationToken)`.
- `PopManyAsync` returns an empty array for a missing key; `PopAsync` returns null. Queued forms are `PopMany` and `Pop`.

`DifferenceCountAsync` ([SDIFFCARD](https://redis.io/docs/latest/commands/sdiffcard/)) and `UnionCountAsync` ([SUNIONCARD](https://redis.io/docs/latest/commands/sunioncard/)) count without returning members (Redis 8.10+). Missing keys are empty sets. A difference reduces the first set by every later set. At least one key is required. Limits must be nonnegative; zero means unlimited.

```csharp
long exclusive = await redis.Sets.DifferenceCountAsync("team:red", "on-call");
long limited = await redis.Sets.DifferenceCountAsync(10, "team:red", "on-call");
long total = await redis.Sets.UnionCountAsync("team:red", "on-call");
long estimate = await redis.Sets.UnionCountAsync(
    new RespireSetUnionCountOptions(Limit: 100, Approximate: true), "team:red", "on-call");
```

`Approximate` returns Redis's union cardinality estimate; leave it false for an exact count. The fake server always counts exactly. Immediate overloads also accept a key span and cancellation token.

## Sorted sets

### Conditional adds and increments

Pass `RespireSortedSetAddOptions` right after the key to use ZADD conditions:

```csharp
bool added = await redis.SortedSets.AddAsync("scores", RespireSortedSetAddOptions.Nx, "ada", 98.5);
bool improved = await redis.SortedSets.AddAsync("scores",
    RespireSortedSetAddOptions.Xx | RespireSortedSetAddOptions.Gt | RespireSortedSetAddOptions.Ch,
    "ada", 100);
long changed = await redis.SortedSets.AddAsync("scores", RespireSortedSetAddOptions.Ch,
    ("ada", 101), ("grace", 99));
double? increased = await redis.SortedSets.IncrementAsync("scores",
    RespireSortedSetAddOptions.Xx | RespireSortedSetAddOptions.Gt, "ada", 2);
```

| Flag | Effect |
|---|---|
| `Nx` | Only insert missing members. |
| `Xx` | Only update existing members. |
| `Gt` / `Lt` | Only update when the new score is greater / less. New members are still added unless combined with `Xx`. Redis 6.2+. |
| `Ch` | Count changed scores as well as new members. An unchanged score never counts. |

- Invalid combinations (`Nx` with `Xx`, `Gt`, or `Lt`; `Gt` with `Lt`) are rejected before sending.
- Bulk adds need at least one entry.
- The options overload of `IncrementAsync` uses `ZADD INCR` for one member. It returns the new score, or null when a condition rejects the update. `Gt`/`Lt` compare the resulting score; `Ch` does not affect the result.
- The options-free `IncrementAsync` uses `ZINCRBY` and returns `double`.
- Queued `SortedSets.Add` and `SortedSets.Increment` return `RespirePending<bool>`, `RespirePending<long>`, or `RespirePending<double?>`.
- Generic single-member adds use typed serialization, including `1`/`0` for booleans.

### Reads and other operations

- `RandomMemberAsync(key)` returns one member or `null`.
- `RandomMembersAsync(key, count)` and `RandomMembersWithScoresAsync(key, count)` return arrays (Redis 6.2+). Positive counts give distinct members up to the set size. Negative counts allow duplicates and return exactly the absolute count for a nonempty set. Zero returns empty. `long.MinValue` is rejected.
- Sampling never removes members. Generic variants deserialize members, including `byte[]`. A missing scalar result is `default(T)`; a missing counted result is an empty array.
- `RankWithScoreAsync(key, member, descending: false)` (Redis 7.2+, `ZRANK`/`ZREVRANK WITHSCORE`) returns a nullable `RespireSortedSetRankResult` with zero-based `Rank` and `Score`. It returns `null` for a missing key or member. `RankAsync` returns only the rank.

```csharp
RespireSortedSetRankResult? position = await redis.SortedSets.RankWithScoreAsync("scores", "ada", descending: true);
```

- `CountByLexAsync` and `RemoveRangeByLexAsync` take a `RespireLexRange` with inclusive, exclusive, or infinite bounds. Lex operations need all members to share one score.
- `IntersectCountAsync` (Redis 7+) returns only the cardinality. A nonnegative `limit` caps the count; zero means unlimited. It needs at least one key.

```csharp
RespireSortedSetEntry[] sample = await redis.SortedSets.RandomMembersWithScoresAsync("scores", 3);
long common = await redis.SortedSets.IntersectCountAsync(100, "{scores}:today", "{scores}:yesterday");
var range = new RespireLexRange(RespireLexBound.Inclusive("a"), RespireLexBound.Exclusive("m"));
long matches = await redis.SortedSets.CountByLexAsync("names", range);
long removed = await redis.SortedSets.RemoveRangeByLexAsync("names", range);
```

### Ranges, pops, and stores

```csharp
await redis.SortedSets.AddAsync("scores", "ada", 98.5);
await redis.SortedSets.AddAsync("scores", ("grace", 97.5), ("linus", 96.0));
await redis.SortedSets.IncrementAsync("scores", "ada", 1.5);
double?[] scores = await redis.SortedSets.ScoresManyAsync("scores", "ada", "missing");
RespireSortedSetEntry? next = await redis.SortedSets.PopAsync("ready:scores");
RespireSortedSetEntry[] nextBatch = await redis.SortedSets.PopManyAsync("ready:scores", count: 2);
RespireSortedSetEntry<int>[] nextPlayers = await redis.SortedSets.PopManyAsync<int>("player:scores", count: 2);
RespireSortedSetEntry<int>? highest = await redis.SortedSets.PopAsync<int>("player:scores", descending: true);

RespireSortedSetEntry[] top = await redis.SortedSets.RangeWithScoresAsync(
    "scores",
    start: 0,
    stop: 9,
    descending: true);

var finalists = new RespireScoreRange(
    RespireScoreBound.Exclusive(90),
    RespireScoreBound.Max);
RespireSortedSetEntry[] page = await redis.SortedSets.RangeByScoreWithScoresAsync(
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

RespireSortedSetEntry<int>[] players =
    await redis.SortedSets.RangeWithScoresAsync<int>("player:scores", descending: true);
```

- Score and lex bounds are inclusive by default. Use `Exclusive(...)` for an open bound, and `Min` / `Max` for infinity.
- `offset` requires `count`, because Redis sends them together as `LIMIT offset count`.
- Intersection, union, and difference each have read and `Store` forms.
- Typed rank ranges, score ranges, and pops return `RespireSortedSetEntry<T>`, keeping scores.

### Binary members

Bulk adds take `(RespireValue Member, double Score)` tuples. Members can be text, numbers, or bytes. `RespireSortedSetEntry` is a read result, not an input.

Byte members borrow your memory. In batches and transactions, keep them unchanged until `ExecuteAsync` or `CommitAsync` completes, or copy first, for example `(buffer.ToArray(), score)`.

```csharp
byte[] member = [0xff, 0x00, 0x80];
await redis.SortedSets.AddAsync("binary:scores", (member, 1.5), ("text", 2.5));

// Use an explicit tuple array when retaining inputs for reuse or passing cancellation.
(RespireValue Member, double Score)[] entries = [(member, 3.5), ("text", 4.5)];
await redis.SortedSets.AddAsync("binary:scores", entries, cancellationToken);
```

## Streams

### Idempotent production (Redis 8.6+)

Set `RespireStreamAddOptions.Idempotency` to deduplicate appends:

- `RespireStreamIdempotency.Manual(producerId, messageId)` sends IDMP.
- `RespireStreamIdempotency.Automatic(producerId)` sends IDMPAUTO and derives the identity from the field/value payload.

Null keeps ordinary XADD. Both modes need an automatic entry ID (`Id = null` or `"*"`) and nonempty identifiers. Factories copy binary identifiers, which are never key-prefixed. Equality compares the mode and identifier bytes.

```csharp
var options = new RespireStreamAddOptions
{
    Idempotency = RespireStreamIdempotency.Manual("orders-producer", "event-42"),
};
RespireStreamId? first = await redis.Streams.AddAsync("orders", options, ("status", "paid"));
RespireStreamId? duplicate = await redis.Streams.AddAsync("orders", options, ("status", "paid"));
```

Within Redis's retained history, a repeated producer/message identity returns the original entry ID, even when a manual-mode payload differs. Each producer has its own history.

:::warning
This is not unlimited exactly-once delivery. Duration and size limits can evict identities, and deleting the stream removes its history. See [XADD](https://redis.io/docs/latest/commands/xadd/) and [Redis's implementation](https://github.com/redis/redis/blob/8.8/src/t_stream.c).
:::

- Queued `Streams.Add` accepts the same options. Trimming, reference policies, and NOMKSTREAM still combine with it.
- An idempotent XADD is audited as `WriteChecked`, because a resend returns the original entry instead of appending again.
- Cancellation after dispatch cannot prove that the write did not run.
- When a [retry policy](../guides/command-retries.md#idempotent-stream-appends) can resend a possibly executed checked write, typed `Streams.AddAsync` attaches an identity automatically. Set `RespireOptions.StreamProducerId` to choose the producer.

### Stream configuration (Redis 8.6+)

`Streams.ConfigureAsync` changes an existing stream's idempotency settings with [XCFGSET](https://redis.io/docs/latest/commands/xcfgset/). Supply at least one setting; null keeps the current value.

| Option | Range | Redis default |
|---|---|---|
| `IdempotencyDurationSeconds` | 1–86400 whole seconds | 100 |
| `IdempotencyMaxSize` | 1–10000 identities per producer | 100 |

Capacity eviction can remove identities before their duration ends.

```csharp
await redis.Streams.AddAsync("orders", ("status", "created"));
bool configured = await redis.Streams.ConfigureAsync("orders", new RespireStreamConfigurationOptions
{
    IdempotencyDurationSeconds = 300,
    IdempotencyMaxSize = 1000,
});
```

- Changing either value clears all producer deduplication records for the stream. Reapplying unchanged values keeps them. Entries are untouched.
- Redis does not invalidate `WATCH` for these changes.
- Queued `Streams.Configure` returns `RespirePending<bool>`. Invalid options fail locally before queuing.
- Cancellation cannot undo a change already sent.

### Negative acknowledgements (Redis 8.8+)

`NegativeAcknowledgeAsync` runs [XNACK](https://redis.io/docs/latest/commands/xnack/). It releases pending entries from their consumers without acknowledging or deleting them, so they are claimable at once.

| `RespireStreamNackMode` | Delivery count |
|---|---|
| `Silent` | Decremented, stopping at zero. |
| `Fail` | Unchanged. |
| `Fatal` | Set to `long.MaxValue`. |

```csharp
long released = await redis.Streams.NegativeAcknowledgeAsync(
    "orders", "workers", RespireStreamNackMode.Fail, "1710000000000-0");
```

- The result is Redis's total count, not per-ID outcomes. Duplicate IDs count repeatedly.
- `PendingAsync` shows released entries with an empty consumer and a `-1 ms` idle time.
- Absent pending entries are skipped unless `RespireStreamNackOptions.Force` creates unowned references for existing stream entries. New forced entries start with zero deliveries, except in `Fatal` mode.
- `RetryCount` overrides any mode with a nonnegative count. Ordinary consumers rarely need `Force` or `RetryCount`.
- Cancellation overloads take an ID span (for example `["1-0"]`) followed by the token. In variadic calls, write `new RespireStreamNackOptions { ... }`, not target-typed `new()`.
- IDs must be numeric. Group names accept raw binary and are copied.
- Queued form: `Streams.NegativeAcknowledge`. A pre-cancelled call sends nothing; later cancellation cannot undo the write. Missing groups return server errors.

### Trimming

`RespireStreamAddOptions.MinId` or `MaxLength` trims while appending. `Streams.TrimAsync` with `RespireStreamTrimOptions` trims an existing stream and returns the number of entries removed.

- `MinId` removes entries with lower IDs. `MaxLength` keeps the newest entries up to that length.
- The two thresholds are mutually exclusive. `TrimAsync` requires one; `AddAsync` does not trim without one.

```csharp
await redis.Streams.AddAsync("events", new RespireStreamAddOptions
{
    MinId = "1700000000000-0", Limit = 1000,
}, ("type", "updated"));

long removed = await redis.Streams.TrimAsync("events", new RespireStreamTrimOptions
{
    MinId = "1700000000000-0", Approximate = true, Limit = 0,
});
```

| Option | Default | Meaning |
|---|---|---|
| `RespireStreamAddOptions.ApproximateTrim` | `true` | Approximate trimming removes whole internal nodes and can keep entries past the threshold. |
| `RespireStreamTrimOptions.Approximate` | `false` | Same, for `TrimAsync`. `TrimByMaxLengthAsync` also defaults to exact. |
| `Limit` | null (server default) | Caps trimming work, not the removed count. Only valid with approximate trimming and a threshold. `0` disables the cap. |

- Lengths and limits must be nonnegative.
- `MinId` takes numeric IDs, including milliseconds-only IDs, not range or consumer-read sentinels.
- Invalid combinations fail before I/O.
- MINID and LIMIT require Redis 6.2+; MAXLEN requires Redis 5.0+. See [XADD](https://redis.io/docs/latest/commands/xadd/) and [XTRIM](https://redis.io/docs/latest/commands/xtrim/).
- Queued `Streams.Add` and `Streams.Trim` validate and copy keys and values when queued. Immediate calls borrow binary keys and values until the call completes.

### Reference-aware removal (Redis 8.2+)

`RespireStreamReferencePolicy` controls pending-entry references for XDELEX, XACKDEL, and trimming (`RespireStreamAddOptions.ReferencePolicy`, `RespireStreamTrimOptions.ReferencePolicy`). Null omits the token, which works on older servers. Any explicit policy, including `KeepReferences`, requires Redis 8.2+.

| Policy | Wire token | Effect |
|---|---|---|
| `KeepReferences` | `KEEPREF` | Deletes stream entries but retains other pending references. |
| `DeleteReferences` | `DELREF` | Deletes entries and removes their pending references from all groups. |
| `Acknowledged` | `ACKED` | Deletes only when every group has read and acknowledged the entry. |

- `Streams.RemoveAsync(key, policy, ids)` runs XDELEX. `RemoveAsync(key, ids)` runs XDEL.
- `AcknowledgeAndRemoveAsync` runs XACKDEL: it acknowledges entries in the named group and applies the policy atomically.

```csharp
RespireStreamDeletionResult[] outcomes = await redis.Streams.AcknowledgeAndRemoveAsync(
    "events", "processors", RespireStreamReferencePolicy.Acknowledged, "1700000000000-0");

await redis.Streams.RemoveAsync("events", RespireStreamReferencePolicy.DeleteReferences,
    "1700000000000-0", "1700000000001-0");

await redis.Streams.TrimAsync("events", new RespireStreamTrimOptions
{
    MaxLength = 1000, ReferencePolicy = RespireStreamReferencePolicy.Acknowledged,
});
```

Results keep the requested ID order, including duplicates:

| Result | Meaning |
|---|---|
| `Deleted` (1) | Removed. XACKDEL can also return it when clearing a pending reference to an already-deleted entry. |
| `NotFound` (-1) | Not found. For XACKDEL, it also means the ID was not pending in the group or the group is absent, so the entry may still exist. DELREF XDELEX can return it while removing dangling references. |
| `Retained` (2) | The ACKED condition was not met, including for unread entries. XACKDEL still acknowledged the group's pending entry. |

:::warning
KEEPREF can leave pending references to deleted data. ACKED is not a hard retention bound: unread or unacknowledged entries can keep a stream above its trim threshold. With no consumer groups, Redis 8.2 allows ACKED deletion and trimming.
:::

Both commands need at least one numeric ID. Invalid policies and sentinel or malformed IDs fail before I/O. Queued forms are `Remove(key, policy, ids)` and `AcknowledgeAndRemove`. Respire never falls back to older write commands. See [XDELEX](https://redis.io/docs/latest/commands/xdelex/), [XACKDEL](https://redis.io/docs/latest/commands/xackdel/), and [XTRIM](https://redis.io/docs/latest/commands/xtrim/).

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

`ReadAsync` runs [XREAD](https://redis.io/docs/latest/commands/xread/) (Redis 5.0+).

- The default start ID is `0`. Only entries newer than each ID are returned. `count` limits entries **per stream**.
- Missing or empty streams are left out of multi-stream results. An empty or timed-out read returns an empty array.
- Returned keys have the client prefix removed.
- Omit `waitFor` for a nonblocking read. A finite wait rounds up to milliseconds (zero becomes one millisecond). Explicit `waitFor: null` sends `BLOCK 0`; `Timeout.InfiniteTimeSpan` remains a compatibility alias.
- Blocking reads use dedicated connections, so other commands stay responsive. Pass cancellation to interrupt a wait.
- Queued `Streams.Read` is nonblocking only.

#### Reply limits (Redis 8.10+)

Pass `RespireStreamReadOptions` to the options-first `ReadAsync` overload or queued `Read` overload:

```csharp
var page = await redis.Streams.ReadAsync(
    new RespireStreamReadOptions { Count = 50, MaxCount = 80, MaxSize = 65_536 },
    [("{events}:one", "0"), ("{events}:two", "0")]);
```

| Option | Meaning |
|---|---|
| `Count` | Entries per stream. |
| `MaxCount` | Total entries across streams. Positive, and at least `Count` when both are set. |
| `MaxSize` | Positive byte budget for the whole reply. Redis still returns the first entry even if it is larger. |

- The first limit reached wins. These are server-side limits, not client-side truncation.
- Nonblocking reads visit streams in request order, so earlier streams can use the whole budget.
- Leave `RespireStreamReadOptions.WaitFor` unset for immediate reads. Assigning `WaitFor = null` waits indefinitely; `IsBlocking` reports whether the option was assigned. Queued reads reject any assigned `WaitFor`, including null, locally. See [blocking-wait migration](../guides/blocking-queues.md#migrating-blocking-waits).
- Without `MaxCount` and `MaxSize`, the server requirements are unchanged.
- `XREADGROUP` accepts the same limits; see [Consumer groups](#consumer-groups).

#### Following streams with `ReadAllAsync`

- Tracks a last-delivered ID per stream, in memory. It advances when an entry is delivered, not when your handler succeeds. Persist checkpoints yourself to survive restarts.
- Drains each batch, then polls with one-second blocking reads. Polls are exempt from `CommandTimeout`; your cancellation token bounds a wait.
- Retries transient connection and server failures indefinitely, with delays from 100 ms to 3.2 seconds, until cancellation or disposal. Configure the client logger to see retries.
- Stops on authentication, ACL, configuration, and other non-transient errors, and when Cluster MOVED/ASK redirects exceed their limit.
- Disposing the enumerator between entries releases its buffered batch.
- `RespireStreamId.New` (`$`) in a one-shot read means the current tail. In enumeration, each `$` is resolved once with `XREVRANGE ... COUNT 1` (requires `XREVRANGE` permission); an empty stream starts at `0`. A failed initial lookup throws without retry, so a reconnect cannot silently skip ahead. Multi-stream lookups are not atomic. Numeric IDs avoid the lookup.

:::warning
`ReadAllAsync` is not lossless. Trimming, deleting, or recreating a stream during an outage can remove unread entries, and reconnecting does not detect every gap. These reads create no pending entries and support no acknowledgement.
:::

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

- The single-stream `ReadGroupAsync` loop blocks for five seconds per poll when `WaitFor` is omitted. Its options-first overload accepts the [reply limits](#reply-limits-redis-810).
- Pass an explicit `startAt` to replay this consumer's pending entries. Replay does not block and ends when the pending entries run out.
- `ReadGroupOnceAsync(key, group, consumer, options)` reads one page. The multi-stream `ReadGroupAsync(streams, group, consumer, options)` also reads one page: use `>` for new entries or numeric cursors for this consumer's pending entries.
- Entries support `AckAsync`. Queued `Streams.ReadGroup` provides the nonblocking forms.
- Reply limits also apply to pending history and after a blocking read wakes. Entries excluded by the byte budget are not added to the pending list.
- Use `ClaimAsync` when you know the pending IDs. `ClaimPendingAsync` returns the `XAUTOCLAIM` next position and deleted IDs for iterative recovery.

See [XREADGROUP](https://redis.io/docs/latest/commands/xreadgroup/).

### Consumer recovery options

| `RespireStreamReadOptions` | Effect |
|---|---|
| `NoAck` | New group entries skip the pending list. Existing pending entries are not acknowledged. |
| `ClaimMinIdle` | Uses Redis 8.4 `XREADGROUP CLAIM`. Eligible pending entries arrive before new entries, longest idle first. |

Both work with single- and multi-stream pages, the options-first consumer loop, and queued reads. Plain `XREAD` methods reject them before sending.

```csharp
var page = await redis.Streams.ReadGroupAsync(
    [("{events}:one", ">"), ("{events}:two", ">")], "processors", "worker-2",
    new RespireStreamReadOptions
    {
        Count = 100,
        ClaimMinIdle = TimeSpan.FromMinutes(1),
        WaitFor = TimeSpan.FromSeconds(5),
    }, stoppingToken);
```

- `PreviousIdleTime` and `PreviousDeliveryCount` on a reclaimed entry describe it before this delivery. They are null when the server omits them. Redis 8.4 sends zero for both on new entries delivered with `CLAIM`.
- Reclaimed entries support `AckAsync`. With `CLAIM` and `NOACK` together, only new entries skip the pending list; reclaimed entries still need acknowledgement.
- Redis ignores `CLAIM`, `NOACK`, and `BLOCK` for numeric pending-history cursors.

Use options-first `PendingAsync` to filter by idle time (Redis 6.2+), consumer, and ID bounds. The options-based `PendingAsync`, `ClaimAsync`, and `ClaimIdsAsync` overloads take the options before the key.

```csharp
var pending = await redis.Streams.PendingAsync(
    new RespireStreamPendingOptions { MinIdle = TimeSpan.FromMinutes(1), Count = 100 },
    "events", "processors", stoppingToken);

if (pending.Length > 0)
{
    var ids = await redis.Streams.ClaimIdsAsync(
        new RespireStreamClaimOptions { IdleTime = TimeSpan.Zero, RetryCount = 1 },
        "events", "processors", "worker-2", TimeSpan.FromMinutes(1),
        pending.Select(entry => entry.Id).ToArray(), cancellationToken: stoppingToken);
}

var recoveredIds = await redis.Streams.ClaimPendingIdsAsync(
    "events", "processors", "worker-2", TimeSpan.FromMinutes(1),
    count: 100, cancellationToken: stoppingToken);
```

`RespireStreamClaimOptions` works with `ClaimAsync` and `ClaimIdsAsync`:

| Option | Meaning |
|---|---|
| `IdleTime` | Sends `IDLE`. Truncated to whole milliseconds. |
| `DeliveryTime` | Sends an absolute Unix-millisecond `TIME`. Use either this or `IdleTime`. |
| `RetryCount` | Sets the delivery counter. |
| `Force` | Creates a pending record, only if the stream entry exists. |
| `LastId` | Advances the group's last-delivered ID when greater. |

- Durations and counters must be nonnegative.
- The `minIdle` threshold on `ClaimAsync`, `ClaimIdsAsync`, `ClaimPendingAsync`, and `ClaimPendingIdsAsync` rounds fractional milliseconds up, including at `TimeSpan.MaxValue`.
- For cancellable `ClaimIdsAsync`, pass the IDs as a span or array followed by `cancellationToken:`, as above. The `params` overload has no cancellation.
- The IDs-only methods send `JUSTID`: no field payloads and no automatic delivery-counter increment. An explicit `RetryCount` still sets the counter.
- `ClaimPendingIdsAsync` (Redis 6.2+) returns `NextStart`, `Ids`, and `DeletedIds`. Continue until `NextStart` is `0-0`, even when a page has no IDs. `DeletedIds` is filled on Redis 7+ and empty on older servers.
- Recovery commands have no queued form. The in-memory fake does not implement these options; test them against Redis containers.

See [XCLAIM](https://redis.io/docs/latest/commands/xclaim/), [XAUTOCLAIM](https://redis.io/docs/latest/commands/xautoclaim/), and [XPENDING](https://redis.io/docs/latest/commands/xpending/).

### Stream metadata

`Streams.CreateConsumerAsync(key, group, consumer)` creates a consumer in an existing group (Redis 6.2+, `XGROUP CREATECONSUMER`). It returns `true` for a new consumer and `false` for an existing one. It does not read entries or create a missing stream or group; those cases and wrong key types are server errors.

`Streams.SetLastIdAsync(key, lastId, entriesAdded: ..., maxDeletedId: ...)` runs `XSETID` (Redis 5.0+). Each optional argument requires Redis 7.0+ and can be set alone; omitted values stay unchanged.

:::caution
`XSETID` is an advanced restoration command that Redis documents as internal to replication. It changes only the stream's last-generated ID. It does not add or trim entries or move consumer groups; use `SetGroupPositionAsync` for a group's last-delivered ID.
:::

- Negative `entriesAdded` fails locally.
- Redis rejects invalid IDs, a last ID below the highest entry, an `entriesAdded` below the stream length, and a `maxDeletedId` above the last ID.
- Redis 7.0 treats a `maxDeletedId` of `0-0` as unchanged.

Both methods accept cancellation and have no queued form. See [CREATECONSUMER](https://redis.io/docs/latest/commands/xgroup-createconsumer/) and [XSETID](https://redis.io/docs/latest/commands/xsetid/).

## Bitmaps, HyperLogLogs, and geo indexes

See the dedicated [bitmap](bitmaps.md), [HyperLogLog](hyperloglog.md), and
[geospatial](geo.md) guides for commands, result shapes, and server requirements.

```csharp
bool wasActive = await redis.Bitmaps.SetAsync("active:2026-08-09", userId, true);
long active = await redis.Bitmaps.SetBitCountAsync("active:2026-08-09");
long? firstActive = await redis.Bitmaps.PositionAsync("active:2026-08-09", true);
long?[] bytes = await redis.Bitmaps.FieldReadOnlyAsync(
    "packed:counters",
    RespireBitFieldOperation.Get(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Fields(2)));
long?[] updated = await redis.Bitmaps.FieldAsync(
    "packed:counters",
    RespireBitFieldOperation.Set(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Bits(16), 10),
    RespireBitFieldOperation.Increment(RespireBitFieldEncoding.Unsigned(8), RespireBitFieldOffset.Fields(2), 1));

await redis.HyperLogLog.AddAsync("visitors", sessionId);
long estimate = await redis.HyperLogLog.CountAsync("visitors");

await redis.Geo.AddAsync("cafes", new RespireGeoEntry(-0.1276, 51.5072, "london"));
RespireGeoSearchResult[] nearby = await redis.Geo.SearchAsync(
    "cafes",
    RespireGeoSearchOrigin.FromCoordinates(-0.1, 51.5),
    RespireGeoSearchShape.Circle(10, RespireGeoUnit.Kilometers));
```

BITFIELD types:

- `RespireBitFieldEncoding.Signed(width)` takes 1–64 bits; `Unsigned(width)` takes 1–63.
- `RespireBitFieldOffset.Bits(n)` is an absolute bit offset. `Fields(n)` multiplies the index by the encoding width (Redis `#n`). Both reject negative values. The default offset is bit zero.
- `Get`, `Set`, and `Increment` accept these types. `FieldReadOnlyAsync` accepts only `Get`.

For vector similarity, use the [VectorSets facet](../guides/vector-sets.md). For uncommon operations and modules, use the [complete command catalog](../guides/raw-commands.md).
