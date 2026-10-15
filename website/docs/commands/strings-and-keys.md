---
title: Strings and keys
description: Read, write, expire, scan, and manage Redis keys.
---

# Strings and keys

Multi-key commands accept one key and a cancellation token without a collection
expression, for example `redis.DeleteAsync("key", cancellationToken)`,
`redis.Keys.UnlinkAsync("key", cancellationToken)`, and
`redis.Strings.GetManyAsync("key", cancellationToken)`. The single-key overload
keeps the multi-key result shape: `GetManyAsync` still returns an array.
Collection facets also accept a single member, field, or other item followed by
a token, such as `redis.Lists.LeftPushAsync("jobs", "job", cancellationToken)`.

Common string and key operations are on `IRespireClient` directly. The full surfaces are `redis.Strings` and `redis.Keys`.

## Read and write

```csharp
await redis.SetAsync("greeting", "hello");
string? greeting = await redis.GetStringAsync("greeting");

await redis.SetAsync("visits", 1);
long visits = await redis.IncrementAsync("visits");
```

Conditional writes use `SetWhen`:

```csharp
bool created = await redis.SetAsync(
    "lock:invoice:42",
    requestId,
    expiry: TimeSpan.FromSeconds(20),
    when: SetWhen.NotExists);
```

### Large binary values

On plain socket connections, large array-backed binary values are sent straight from your memory. **Keep the input unchanged until `SetAsync` completes**, including after cancellation, timeout, or failure. Cancellation and command deadlines can wait for an accepted socket write to finish. TLS, custom streams, and other command shapes copy the value instead.

`GatheredWriteStallTimeout` bounds a stalled borrowed write:

| Value | Behavior |
| --- | --- |
| `null` (default) | Inherits `ConnectionIdleReadTimeout`. |
| 1 ms or more | Overrides `ConnectionIdleReadTimeout`, even when receive checks are disabled. |
| `Timeout.InfiniteTimeSpan` | Disables only this bound. A configured receive timeout still applies. |

When no socket send completes for that period, the watchdog closes the socket, waits for the kernel to release your memory, and then completes the operation.

- The connection is discarded. Other pending commands on it can fail.
- Redis may have received part or all of the SET.
- The timer starts when the borrowed memory is queued and runs until the whole owning buffer is sent. Completed sends reset it; new enqueues do not.
- A paused `GetStreamAsync` consumer does not suspend it. Cancellation alone does not abort an accepted frame.

With no timeout set (both options default to null), a peer that never resumes reading can hold the operation until the socket fails or the client is disposed, even after cancellation or a deadline. In every case, reuse the input only after the operation completes. See [independent liveness bounds](../fundamentals/connections.md#independent-liveness-bounds).

### Stream a value

Stream large values without building a payload-sized command buffer:

```csharp
await using var file = File.OpenRead("archive.bin");
await redis.Strings.SetAsync("archive", file, file.Length);
```

- Pass the exact, nonnegative byte length. Respire reads at most that many bytes, does not seek, and leaves the stream open.
- A seekable stream with fewer remaining bytes throws `ArgumentOutOfRangeException` before sending. Any other source that ends early throws `EndOfStreamException`.
- Respire reads the first chunk (up to 32 KiB) before sending. A failure, end, cancellation, or timeout within that chunk throws without sending anything. The stream has still been read, so retry with a fresh or rewound source.
- In Cluster, if the slot moves while the first chunk is read, Respire sends the chunk to the new owner without rereading it. A read that was canceled or faulted with an unknown byte count fails instead.
- The `ReadOnlySequence<byte>` overload writes its segments in 32 KiB chunks without combining them. Keep its memory unchanged until the task completes.
- Both overloads always stream and allocate a little per call. Use the ordinary `SetAsync` overloads for small values.
- Prefer seekable or in-memory sources.

Connections:

- Each upload uses a dedicated connection, so a slow source does not block other commands.
- Concurrent uploads have no connection limit: Respire opens a new connection when none is idle. Bound concurrent uploads yourself to limit Redis connections. At most four idle connections are kept, shared with blocking commands.
- After a `MOVING` handoff (standalone, Cluster, or Sentinel), new uploads use the new endpoint; uploads in progress finish on the old one.
- During Sentinel failover, an upload in progress finishes on its original connection. An interrupted upload is not replayed on the new primary, because the old primary may have accepted it.

Failures:

- `CommandTimeout` covers the whole upload, including every source read and socket write. Raise it, or set it to `null`, for slow uploads.
- Cancellation, a read failure, or a timeout before the full frame is written closes the upload connection. If that connection closes while Respire reads the source, the call throws `RespireConnectionException`.
- After the full frame is written, cancellation only stops the wait for the reply. Redis may still apply the write.
- After a transport failure once the header is sent, Respire does not replay the write. Redis may or may not have applied it.
- `MOVED` and `ASK` are followed for seekable streams (replayed from their original position) and unchanged `ReadOnlySequence<byte>` sources. Non-seekable streams return the redirect to you.

## Expiry

`RespireExpiry` is the single expiry argument: none, a relative TTL, an absolute instant, or "keep the current TTL". `TimeSpan` and `DateTimeOffset` convert implicitly.

```csharp
string token = "session-token";
await redis.SetAsync("session:42", token);                                  // no TTL (clears any existing one)
await redis.SetAsync("session:42", token, TimeSpan.FromMinutes(30));        // PX
await redis.SetAsync("session:42", token, RespireExpiry.At(midnight));         // PXAT
await redis.SetAsync("session:42", token, RespireExpiry.Keep);                 // KEEPTTL
```

Expiry inputs and results have different meanings:

| Meaning | Type and parameter | Examples |
| --- | --- | --- |
| Expiry policy sent to Redis | `RespireExpiry expiry` | `SetAsync`, `ExpireAsync`, `GetOrSetAsync` |
| Remaining lifetime reported by Redis | `RespireTtl` | `Keys.ExpiryAsync` (PTTL) |
| Absolute expiry instant reported by Redis | `RespireExpiryTime` | `Keys.ExpiryTimeAsync` (PEXPIRETIME/EXPIRETIME) |
| Relative lock lease | `TimeSpan leaseDuration` | `Locks.AcquireAsync`, `Locks.TryTakeAsync` |

Both result types distinguish a missing key or field from a persistent one using `Exists` and
`HasExpiry`. `RespireTtl.TimeToLive` is a duration; `RespireExpiryTime.UnixTimeMilliseconds` is the
server's deadline. They remain separate because adding a TTL to the client's clock cannot recover
the server's exact deadline. Expiry result types are never command inputs.

Individual commands accept only meaningful expiry policies. Cache-aside writes require a positive
relative expiry or a positive Unix-millisecond absolute instant. Lock leases are always relative
durations so renewals can reset the lease from now. Operation `timeout` parameters, such as MIGRATE's
I/O timeout, bound an operation rather than the lifetime of a key.

## Compare values before writing or deleting

`Strings.SetConditionalAsync` and `Strings.DeleteConditionalAsync` compare the stored bytes or digest atomically before acting.

```csharp
var expected = RespireValueCondition.EqualTo("version-1");
bool changed = await redis.Strings.SetConditionalAsync(
    "document", (RespireValue)"version-2", expected, RespireExpiry.Keep);

// Redis 8.4+: capture a digest, then condition a later write on that digest.
string? digest = await redis.Strings.DigestAsync("document");
if (digest is not null)
{
    bool deleted = await redis.Strings.DeleteConditionalAsync(
        "document", RespireValueCondition.DigestEqualTo(digest));
}
```

| API or condition | Command | Minimum supported server |
| --- | --- | --- |
| `EqualTo(value)` with conditional SET | `SET ... IFEQ value` | Redis 8.4 or Valkey 8.1 |
| `NotEqualTo(value)` with conditional SET | `SET ... IFNE value` | Redis 8.4 |
| `DigestEqualTo(digest)` / `DigestNotEqualTo(digest)` with conditional SET | `SET ... IFDEQ` / `IFDNE` | Redis 8.4 |
| `DeleteConditionalAsync` with any of these four conditions | `DELEX` | Redis 8.4 |
| `DigestAsync` | `DIGEST` | Redis 8.4 |
| `DeleteIfEqualAsync(key, value)` | `DELIFEQ` | Valkey 9.0 |

- Commands are sent as is: no version negotiation, script fallback, or substitution between Redis `DELEX` and Valkey `DELIFEQ`. Unsupported commands, malformed digests, and wrong-type keys return server errors.
- Neither conditional delete works on all Redis and Valkey versions. Check the server flavor and version first. Use `Keys.DeleteAsync` for unconditional deletes.
- Conditional SET returns `true` when it writes and `false` when the comparison fails. On a missing key, equality conditions fail and Redis inequality conditions create the key.
- Conditional delete returns `false` for a missing key or a mismatch.
- A failed SET keeps the value and expiry. A successful SET accepts a relative or absolute expiry or `RespireExpiry.Keep`; the default clears the expiry. `RespireExpiry.Persist` is not allowed.
- Relative expiry truncates to whole milliseconds. A positive duration under 1 ms becomes `PX 0`, which the server rejects without changing anything.

`GetAndSetConditionalAsync` (`SET ... GET`) returns the old string even when the comparison fails; null means the key was missing. **The result is not a write-success flag.** The generic form deserializes the old value. Generic SET methods serialize the new value, but comparison operands are always raw bytes: serialize an operand yourself to compare a serialized object.

```csharp
byte[] expectedBytes = [0xff, 0x00];
var condition = RespireValueCondition.EqualTo(expectedBytes);
byte[] replacement = [0x01, 0xfe];
byte[]? previous = await redis.Strings.GetAndSetConditionalAsync<byte[]>(
    "binary", replacement, condition, RespireExpiry.Keep);

// Valkey 9.0+: delete only if the stored bytes still match.
bool removed = await redis.Strings.DeleteIfEqualAsync("binary", replacement);
```

- Conditions copy binary operands, so you can reuse them. New-value buffers and `DeleteIfEqualAsync` operands are borrowed: keep them unchanged until completion.
- Keys get the view's prefix and determine the Cluster slot. Operands are never treated as keys.
- `DIGEST` returns a hexadecimal string or null. Digests can collide; use byte equality when exactness matters.

Batches and transactions expose `SetConditional`, `GetAndSetConditional`, `DeleteConditional`, `DeleteIfEqual`, and `Digest` (with the generic SET forms) on `Strings`. Results stay valid after the queue is disposed. Cancellation does not prove the server did not run a command.

```csharp
using var batch = redis.CreateBatch();
var pending = batch.Strings.SetConditional(
    "document", (RespireValue)"version-2", RespireValueCondition.EqualTo("version-1"));
await batch.ExecuteAsync();
bool changed = pending.Result;
```

The client-side cache invalidates the key around immediate comparisons, even failed ones. `DIGEST` does not invalidate.

References: Redis [SET](https://redis.io/docs/latest/commands/set/), [DELEX](https://redis.io/docs/latest/commands/delex/), [DIGEST](https://redis.io/docs/latest/commands/digest/); Valkey [SET](https://valkey.io/commands/set/), [DELIFEQ](https://valkey.io/commands/delifeq/).

## Bulk operations

```csharp
await redis.Strings.SetManyAsync(
    ("feature:a", "on"),
    ("feature:b", "off"));

// MSETNX writes both keys only when neither exists.
bool allCreated = await redis.Strings.SetManyIfNotExistsAsync(
    ("reservation:42:owner", "alice"),
    ("reservation:42:state", "pending"));

// One shared expiry (and optional NX/XX) for every pair — Redis MSETEX.
await redis.Strings.SetManyExpireAsync(
    TimeSpan.FromMinutes(5),
    SetWhen.NotExists,
    ("feature:a", "on"),
    ("feature:b", "off"));

string?[] values = await redis.Strings.GetManyAsync("feature:a", "feature:b");
long removed = await redis.DeleteAsync("feature:a", "feature:b");
```

`SetManyIfNotExistsAsync` uses atomic [MSETNX](https://redis.io/docs/latest/commands/msetnx/) (also in [Valkey](https://valkey.io/commands/msetnx/)):

- Returns `false` without changing anything when any key exists, whatever its type. New values have no expiry.
- Needs at least one pair. Empty keys and values are valid. With duplicate keys, the last value wins.
- In Cluster, prefixed keys must share a slot, or it throws `RespireCrossSlotException` before sending.
- Values are raw `RespireValue`s. Keep binary buffers unchanged until completion.
- Batches and transactions expose `Strings.SetManyIfNotExists(pairs)`.

Variadic APIs take `params ReadOnlySpan<T>`. Because `params` must come last, each has an overload that takes the items plus a required `CancellationToken`:

```csharp
string?[] values = await redis.Strings.GetManyAsync(keys, cancellationToken);
long removed = await redis.DeleteAsync(keys, cancellationToken);
```

## Key lifetime

```csharp
await redis.ExpireAsync("session:42", TimeSpan.FromMinutes(30));
RespireTtl ttl = await redis.Keys.ExpiryAsync("session:42");

await redis.Keys.ExpireAsync("session:42", RespireExpiry.Persist);
await redis.Keys.ExpireAsync("report", RespireExpiry.At(DateTimeOffset.UtcNow.AddDays(1)));
await redis.Keys.ExpireAsync("lease", TimeSpan.FromMinutes(10), RespireExpireWhen.GreaterThan);

var value = await redis.Strings.GetAndExpireAsync("session:42", TimeSpan.FromMinutes(30));
CachedJob? job = await redis.Strings.GetAndDeleteAsync<CachedJob>("jobs:next");
```

- `Keys.ExpireAsync` and queued `Keys.Expire` accept `RespireExpireWhen.NotExists` (`NX`), `Exists` (`XX`), `GreaterThan` (`GT`), and `LessThan` (`LT`). Relative inputs send `PEXPIRE` and absolute inputs `PEXPIREAT`. `RespireExpiry.Persist` sends `PERSIST`, which rejects conditions.
- Generic get-and forms deserialize with the client serializer.
- Hash fields use `GetAndRemoveAsync` and `GetAndExpireAsync`. Removing the last field deletes the hash.

`TypeAsync` returns `RespireKeyType`. Rename and copy have conditional forms:

```csharp
RespireKeyType type = await redis.Keys.TypeAsync("session:42");
bool renamed = await redis.Keys.TryRenameAsync("draft", "published");
bool copied = await redis.Keys.CopyAsync("template", "working-copy", replace: true);
```

To copy into another database, pass `destinationDatabase` (Redis 6.2+ `COPY DB`):

```csharp
bool copied = await redis.Keys.CopyAsync("template", "working-copy",
    destinationDatabase: 1, replace: true);
```

Copy returns `false` when the source is missing, or the destination exists without `replace: true`. A negative database fails locally. In Cluster, both keys must share a hash slot. `batch.Keys.Copy` and `transaction.Keys.Copy` take the same options. See [Copy to another database](../guides/sort.md#copy-to-another-database) for the full rules.

## Scan safely

`ScanAsync` manages Redis cursors and returns an async stream:

```csharp
await foreach (string key in redis.Keys.ScanAsync(
    match: "session:*",
    type: RespireKeyType.Hash,
    countHint: 500,
    cancellationToken: stoppingToken))
{
    await InspectAsync(key);
}
```

- `countHint` maps to `COUNT`. It guides work per iteration; it does not set the page size.
- Use `SCAN` instead of `KEYS` in production. Each page yields, so the server is never blocked by one sweep.
- In Cluster, see [resumable Cluster scans](#resumable-cluster-scans) for checkpoints and [Valkey 9.1 server cursors](#valkey-91-server-cursors) for `CLUSTERSCAN`.

## Key-prefixed views

Create a lightweight view when a service or tenant needs a namespace:

```csharp
IRespireClient tenant = redis.WithKeyPrefix("tenant:42:");
await tenant.SetAsync("settings", json); // tenant:42:settings
```

Use the `RespireKey` overload for arbitrary prefix bytes, including NUL and invalid UTF-8:

```csharp
RespireKey prefix = new byte[] { 0xff, 0x00, (byte)':' };
IRespireClient binaryTenant = redis.WithKeyPrefix(prefix);
await binaryTenant.SetAsync("settings", "enabled");
```

- Empty prefixes are rejected. Binary prefixes are copied when the view is created.
- Nested views append prefixes in order. Text-only composition behaves like string concatenation, including surrogate pairs split across a boundary. Once a binary part is present, its bytes are kept exactly and later text parts are encoded as UTF-8 separately.
- Views share the root's connections and client-side cache, and keep its read-routing and cache-bypass policies. Disposing a view does not dispose the root.
- Keys are prefixed before Cluster slot selection, so hash tags can span the prefix boundary. Cache invalidation, keyspace notification filters, and `ResolveKey` use the physical (prefixed) key.
- Pub/sub channels are not prefixed. Use [WithPubSubPrefix](../guides/pub-sub.md#explicit-pubsub-prefixes) for those; composing either view keeps the other namespace.
- A prefix is not a security boundary.

What gets prefixed:

- Typed key arguments, script keys, streams, batches, transactions, supported deferred raw layouts, and explicitly registered immediate module layouts.
- Not prefixed: immediate caller-supplied core commands and interpolated commands. Pass physical keys to them.
- Rejected: immediate catalog commands without a prefixable layout, and unsupported deferred layouts.
- `SORT BY`/`GET` external patterns reject prefixes that contain `*`, `->`, or NUL, because Redis would read them as pattern syntax.

Scans escape the prefix in `MATCH` and strip it from returned keys. Binary prefixes are matched as bytes, so different invalid UTF-8 prefixes stay distinct. `ScanAsync` and `ScanClusterPageAsync` return strings, so invalid UTF-8 suffixes are decoded with replacement characters and cannot round-trip. Resume a Cluster checkpoint with the same prefix and filter; see [resumable Cluster scans](#resumable-cluster-scans).

### Default root namespace

Set `RespireOptions.KeyPrefix` to apply a namespace to the root client:

```csharp
await using var namespaced = RespireClient.Create(new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost", 6379) },
    KeyPrefix = "tenant:42:", // Also accepts RespireKey, byte[] or ReadOnlyMemory<byte>.
});
await namespaced.SetAsync("settings", "enabled"); // tenant:42:settings
IRespireClient section = namespaced.WithKeyPrefix("section:");
await section.GetStringAsync("settings"); // tenant:42:section:settings
```

- `Create` and `ConnectAsync` capture the prefix before connecting. Each `ConnectAnyAsync` candidate uses its own prefix.
- An empty or default prefix disables root prefixing. (`WithKeyPrefix` still rejects empty prefixes.)
- Binary values keep their exact bytes and are copied. Text values follow the same UTF-16 rules as text views.
- Derived views append to the root namespace and keep its routing and cache policies.
- Every view rule above applies. Cache tracking prefixes and invalidation observers use physical keys, including the root prefix.
- `KeyPrefix` does not change pub/sub channels. Set `RespireOptions.PubSubPrefix` or use [WithPubSubPrefix](../guides/pub-sub.md#explicit-pubsub-prefixes).

Connection strings accept `keyPrefix` in both forms:

```text
redis://localhost:6379/0?keyPrefix=tenant%3A42%3A
localhost:6379,keyPrefix=tenant%3A42%3A
```

- Values are percent-decoded once and encoded as UTF-8. Build them with `Uri.EscapeDataString(prefixText)`, especially for `%`, commas, `&`, `=`, NUL, or leading or trailing spaces.
- `+` stays `+`; use `%20` for a space. Comma-form values are trimmed before decoding.
- Malformed escapes and undecodable bytes stay literal (`%FF` stays `%FF`). For prefixes that are not UTF-8 text, use `RespireOptions`.
- Option names are case-insensitive. The last `keyPrefix` wins, and `keyPrefix=` disables prefixing. Unknown options throw.

## Absolute expiration and object metadata

```csharp
RespireExpiryTime expiration = await redis.Keys.ExpiryTimeAsync("session:42");
string? encoding = await redis.Keys.EncodingAsync("session:42");
TimeSpan? idle = await redis.Keys.IdleTimeAsync("session:42");
long? references = await redis.Keys.ReferenceCountAsync("session:42");
```

`Keys.ExpiryTimeAsync(key)` sends Redis 7 `PEXPIRETIME`; pass `ExpiryTimePrecision.Seconds` for `EXPIRETIME`. Both return `RespireExpiryTime`, also used for hash-field expiration:

- `Exists` tells a missing key from a persistent one.
- `HasExpiry` and nullable `UnixTimeMilliseconds` give the absolute expiration, not the remaining TTL. The seconds variant keeps the server's whole seconds, expressed in milliseconds.
- Use `TryGetExpiresAt(out var instant)` when a timestamp might exceed the `DateTimeOffset` range.

`EncodingAsync`, `IdleTimeAsync`, `FrequencyAsync`, and `ReferenceCountAsync` send `OBJECT ENCODING`, `IDLETIME`, `FREQ`, and `REFCOUNT`:

- Missing keys return null. Idle time has one-second resolution.
- Frequency is a logarithmic counter, not an exact count. It requires an LFU eviction policy, under which `IDLETIME` is unavailable. Other combinations return the server error.
- Encodings and reference counts are Redis internals and can change with value size or server version.
- Batch and transaction `Keys` facets expose `ExpiryTime`, `Encoding`, `IdleTime`, `Frequency`, and `ReferenceCount`. None of the results needs disposal.

References: [EXPIRETIME](https://redis.io/docs/latest/commands/expiretime/), [OBJECT IDLETIME](https://redis.io/docs/latest/commands/object-idletime/), [OBJECT FREQ](https://redis.io/docs/latest/commands/object-freq/).

## Longest common subsequence

Redis 7.0+ `LCS` compares the values at two keys. `Strings.LcsAsync` returns the subsequence text, `Strings.LcsLengthAsync` its total byte length, and `Strings.LcsIndexAsync` typed match ranges:

```csharp
RespireLcsIndexResult result = await redis.Strings.LcsIndexAsync(
    "before", "after", new RespireLcsOptions
    {
        MinimumMatchLength = 4,
        IncludeMatchLength = true,
    }, cancellationToken);

foreach (RespireLcsMatch match in result.Matches)
{
    Console.WriteLine($"{match.FirstRange.Start}-{match.FirstRange.End}: " +
        $"{match.SecondRange.Start}-{match.SecondRange.End} ({match.Length} bytes)");
}
```

- Ranges are zero-based, inclusive **byte offsets**, also for UTF-8 values.
- Matches keep Redis order: the last match comes first.
- `MinimumMatchLength` sends `MINMATCHLEN` and must be nonnegative; null omits it.
- `IncludeMatchLength` sends `WITHMATCHLEN`; without it, each match's `Length` is null.
- The result's `Length` is the total length before filtering, even if every match is filtered out.
- Missing keys or no common subsequence return no matches and zero.
- Batches and transactions expose `Strings.LcsIndex`. Both keys get the view's prefix. In Cluster, both keys must share a slot, checked before sending.

For example, `ohmytext` and `mynewtext` have total length 6, with ranges `4-7` / `5-8` and `2-3` / `0-1`. Filtering at 4 bytes returns only the first pair, still with total length 6. See the [Redis LCS reference](https://redis.io/docs/latest/commands/lcs/).

## Bounded counters with expiry

Redis 8.8+ `INCREX` atomically increments, applies optional inclusive bounds, and updates the expiry. `Strings.IncrementExtendedAsync` has `long` and `double` overloads, both returning `Value` and `AppliedIncrement`:

```csharp
RespireIncrementResult<long> counter = await redis.Strings.IncrementExtendedAsync(
    "requests", by: 1, options: new IntegerIncrementOptions
    {
        UpperBound = 100,
        Expiry = TimeSpan.FromMinutes(1),
        ExpireOnlyWhenPersistent = true,
    });

RespireIncrementResult<double> balance = await redis.Strings.IncrementExtendedAsync(
    "balance", by: -0.5, options: new FloatIncrementOptions
    {
        LowerBound = 0,
        Saturate = true,
    });

using var batch = redis.CreateBatch();
var queued = batch.Strings.IncrementExtended("requests", options: new IntegerIncrementOptions
{
    UpperBound = 100,
});
await batch.ExecuteAsync();
long applied = queued.Result.AppliedIncrement;
```

- A missing key starts at zero.
- Without `Saturate`, a result outside the bounds or the server's numeric range leaves the value and TTL unchanged and reports `AppliedIncrement` 0.
- With `Saturate`, Redis clamps the result and reports the actual delta. An unrepresentable delta still errors.
- `AppliedIncrement` 0 does not mean nothing changed: the expiry can still update.
- `Expiry` takes a positive relative TTL (`PX`), a positive absolute instant (`PXAT`), or `RespireExpiry.Persist`. `None` and `Keep` keep the TTL. An expiry in the past deletes the key after computing the result.
- `ExpireOnlyWhenPersistent` sends `ENX` and needs a relative or absolute expiry. It limits only the expiry update; the increment still runs on keys with a TTL.
- Integer replies keep all 64 bits. Float increments must be finite; bounds may be infinite but not NaN. Redis computes in `long double` and replies convert to .NET `double`, so precision and range can differ.
- Batches and transactions expose `Strings.IncrementExtended`. Writes invalidate only that key in the client-side cache.
- Servers without `INCREX` (older Redis, Valkey) return an unsupported-command error. There is no fallback. The coordination fixed-window limiter has its own Lua fallback.

## Resumable Cluster scans

In Cluster mode, `Keys.ScanAsync` and `Keys.ScanClusterPageAsync` share a slot-aware page engine. Use the page API when you need a durable checkpoint:

```csharp
await using var cluster = await RespireClient.ConnectAsync(new RespireOptions
{
    UseCluster = true,
    Endpoints = { new("redis-1", 6379), new("redis-2", 6379) },
});
var cursor = RespireClusterScanCursor.Start;
do
{
    var page = await cluster.Keys.ScanClusterPageAsync(cursor, match: "user:*", countHint: 250);
    foreach (var key in page.Keys)
        Console.WriteLine(key);

    // Persist this string after processing every key in the page.
    string checkpoint = page.Cursor.ToString();
    // The same string can be parsed by another process connected to this cluster.
    cursor = RespireClusterScanCursor.Parse(checkpoint);
    if (page.WaitingOnMigration) await Task.Delay(250);
}
while (!cursor.IsComplete);
```

Using cursors:

- Cursors are immutable. A failed or cancelled page call leaves its input cursor usable; retry it after a transient failure.
- Save the returned cursor only after processing its keys. Make processing idempotent: pages can repeat keys.
- Empty pages can have an incomplete cursor. `COUNT` is a server work hint, not a maximum.
- `CompletedSlotCount` reports progress across 16,384 slots and can decrease after resharding.
- A `MATCH` that pins one slot (a fixed hash tag such as `{tenant}:*`, or an exact key) scans only that slot. Wildcards or escapes before the hash tag, and text prefixes that end in a split surrogate pair, use the general path.
- Resume with the same database, `match`, `type`, and key-prefix view. They are bound into the cursor, and a mismatch fails before network access. `countHint` may change.
- When `WaitingOnMigration` is set, the page has no keys and sends no scan; delay before the next call. `ScanAsync` waits 50 ms, doubling to 250 ms, and observes its cancellation token.
- Continuous topology changes can prevent completion. Use cancellation or a deadline to bound a scan.

Protocol:

- Each new pass checks `COMMAND INFO CLUSTERSCAN` on the primary. It uses Valkey `CLUSTERSCAN` when available and `SCAN` otherwise, including on mixed-version clusters.
- If the metadata check, or `CLUSTERSCAN` at the initial unredirected request, is denied, the pass uses `SCAN`. Denied continuations and denied redirected requests fail and keep the checkpoint.
- A downgrade to `SCAN` restarts the unfinished pass from zero. Completed slots stay complete, but keys can repeat.

Coverage:

- Each page refreshes topology and checks the node's process (`INFO server`) and migration state (`CLUSTER NODES`). Moved slots are rescanned on their new owner. A changed process or configuration epoch restarts that node's pass. Migrating slots complete only after the move settles.
- Keys present for the whole scan are covered across observed resharding and failover. Keys added or deleted during the scan have `SCAN`'s usual weak guarantees.
- An A-to-B-to-A slot move between two page reads, with matching identities and epochs, can go unseen.
- Incomplete ownership or an unavailable primary fails the page instead of skipping keys. The scan is not a snapshot.
- Required permissions: `SCAN`, `CLUSTER SLOTS`, `CLUSTER NODES`, and `INFO`, plus `COMMAND INFO` and `CLUSTERSCAN` for Valkey. Validation adds round trips per page; a larger count hint amortizes them.
- Standalone scans use a plain cursor loop.

Checkpoint tokens:

- `Parse` rejects malformed, oversized, or unsupported-version tokens; `TryParse` returns false.
- Tokens are versioned Base64, not encrypted or authenticated. They contain filters and node identities, but no passwords.
- This client writes RSC3 and reads RSC1, RSC2, and RSC3. Older clients reject RSC3.
- RSC1/RSC2 tokens have no database: resume them against their original database. Legacy UTF-8 text tokens cannot recover characters already replaced during serialization.
- Tokens do not identify the cluster. Reconnect to the original cluster to resume.
- Treat tokens as trusted state. Authenticate them if they cross a trust boundary, and enforce size limits: tokens can be several kilobytes, up to 6 MiB encoded.
- After completion, start a new scan with `Start`.

## Valkey 9.1 server cursors

`Keys.ScanValkeyClusterPageAsync` uses Valkey 9.1+ `CLUSTERSCAN` in Cluster mode. It returns a `RespireValkeyClusterScanPage` with an opaque string cursor and binary-safe `RespireKey` values. Start with `"0"` and pass each returned cursor back unchanged:

```csharp
await using var cluster = await RespireClient.ConnectAsync(new RespireOptions
{
    UseCluster = true,
    Endpoints = [new("localhost", 6379)],
}, cancellationToken);
string cursor = "0";
do
{
    var page = await cluster.Keys.ScanValkeyClusterPageAsync(
        cursor, match: "user:*", type: RespireKeyType.String, countHint: 250,
        cancellationToken: cancellationToken);
    foreach (RespireKey key in page.Keys)
        Console.WriteLine(await cluster.GetStringAsync(key, cancellationToken));

    cursor = page.Cursor; // Persist only after processing this page.
}
while (cursor != "0");
```

- Only `"0"` means completion. Empty pages and duplicate keys are valid; make processing idempotent. `COUNT` controls server work, not page size.
- Resume with the same cluster, database, prefix, `match`, `type`, and optional `slot`, even from another client. The cursor does not bind these for you.
- A failed or cancelled call leaves the input cursor available for retry. Use cancellation or a deadline to bound scans during topology changes.
- Valkey can restart a scan range after server or topology changes, so keys can repeat. This is not a snapshot.
- Requests go to primaries regardless of `ReadFrom`, with normal `MOVED`/`ASK` handling. The cursor is never key-prefixed.
- Optional `slot` limits the scan to one slot, 0 through 16383.
- A prefixed view escapes its prefix in `MATCH`, filters returned keys, and strips the prefix. Pass returned keys straight back to the same view; converting binary keys to strings can lose data.
- Page keys and cursor stay valid after the reply or client is disposed.
- If a string prefix ends with a high UTF-16 surrogate, Respire skips the server `MATCH` and filters locally, so pages can be empty before completion. Returned keys keep the boundary information and round-trip through a view with the same prefix. Their equality includes it, so they do not equal ordinary replacement-byte suffixes.

Queue one page with `batch.Keys.ScanValkeyClusterPage(...)` or `transaction.Keys.ScanValkeyClusterPage(...)`, and read the result after execution. A whole walk cannot be queued, because each page needs the previous cursor. Normal Cluster slot rules apply. In a non-primary read view, a read-only batch routes the scan to a primary without flushing the client-side cache.

Older servers return their unsupported-command or permission errors. This raw cursor is not a `RespireClusterScanCursor`; do not pass one format to the other API. See the [Valkey CLUSTERSCAN reference](https://valkey.io/commands/clusterscan/).

### Database ACLs

Valkey 9.1 adds database ACL rules. This rule limits an existing user to databases 0 and 1 and keeps its other rules:

```text
ACL SETUSER app-user resetdbs db=0,1
```

- Apply matching ACLs on every cluster node.
- Nonzero cluster databases need a server `cluster-databases` setting large enough for the selected database.
- Set `RespireOptions.Database = 1` and the user's `Username`/`Password`. `CLUSTERSCAN` scans the selected database.
- Database access does not grant command or key permissions.
- A denied database selection can surface as a connection exception containing `NOPERM`. Respire does not switch databases or broaden permissions.

See the [Valkey 9.1 announcement](https://valkey.io/blog/valkey-9-1-delivers-improvements-in-security-performance-and-more/).
