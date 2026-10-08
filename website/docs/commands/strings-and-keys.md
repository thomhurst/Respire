---
title: Strings and keys
description: Read, write, expire, scan, and manage Redis keys.
---

# Strings and keys

Frequent operations are available directly on `IRespireClient`; complete string and key surfaces live under `Strings` and `Keys`.

## Bounded counters with expiry

Redis 8.8+ `INCREX` combines an increment, optional inclusive bounds, and an expiry
update in one atomic command. `Strings.IncrementExtendedAsync` has integer (`long`)
and floating-point (`double`) overloads. Both return `Value` and `AppliedIncrement`:

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

A missing key starts at zero. Without `Saturate`, a result outside the bounds or the
server's numeric range leaves the value and TTL unchanged and reports an applied
increment of zero. With `Saturate`, Redis clamps the result and reports the actual
delta; an unrepresentable delta still errors. Zero is not a general success flag:
a zero increment or an already saturated value can still update expiry.

`Expiry` accepts a positive relative TTL, a positive absolute Unix instant, or
`RespireExpiry.Persist`. `None` and `Keep` preserve the existing TTL. Relative and
absolute forms send `PX` and `PXAT`; raw `INCREX` also exposes `EX` and `EXAT`.
`ExpireOnlyWhenPersistent` sends `ENX`, requires a relative or absolute expiry, and
changes only expiry eligibility: the increment still runs on keys with a TTL.
An expiry already in the past deletes the key after calculating the returned pair.

Integer replies retain all 64 bits. Floating increments must be finite; bounds
may be infinite but cannot be NaN. Redis computes with its platform's `long double`; the
typed API converts replies to .NET `double`, so precision and range can differ.
Both RESP2 bulk-string and RESP3 double replies are supported. Transactions expose
the same `Strings.IncrementExtended` methods as batches. Deferred commands capture
binary keys when queued, and writes invalidate only the affected client-cache key.

Older Redis and Valkey servers without `INCREX` return an unsupported-command
error. The typed command has no fallback. The coordination fixed-window limiter
already uses its own atomic Lua fallback, caches unsupported-command results for
five minutes across its partitions, and then probes again to detect server upgrades.

## Read and write

```csharp
await redis.SetAsync("greeting", "hello");
string? greeting = await redis.GetStringAsync("greeting");

await redis.SetAsync("visits", 1);
long visits = await redis.IncrementAsync("visits");
```

Large array-backed binary values can be sent directly from caller memory on plain socket connections.
Keep the input unchanged until `SetAsync` completes, including cancellation, timeout, or failure.
Cancellation and command deadlines can wait for an accepted socket write to finish before the
operation returns. TLS, custom streams, and unsupported command shapes retain the copying path.

For large binary values held in a stream, stream the payload without building a payload-sized command buffer:

```csharp
await using var file = File.OpenRead("archive.bin");
await redis.Strings.SetAsync("archive", file, file.Length);
```

The stream overload requires an exact, non-negative byte length. Respire reads no more than
that length and leaves the stream open. Normal streaming does not seek it; any surplus bytes stay unread in the
stream. A seekable stream with fewer remaining bytes than the declared length is rejected with
`ArgumentOutOfRangeException` before anything is sent; any other source that ends early throws
`EndOfStreamException`. Respire reads the first chunk (up to 32 KiB) before it sends anything, so a
source that fails, ends, is cancelled or times out within that chunk throws without sending a command
header. The client discards the rented upload connection; multiplexed connections are unaffected.
The stream has still been read, so retry with a fresh or rewound source. On a cluster
client, if the node loses its slots while that first chunk is being read, Respire keeps the chunk
and sends the upload to the new owner without reading those bytes again when every completed read
reported its byte count. A canceled or faulted read with an unknown byte count fails instead of
retrying a potentially shifted payload. The
`ReadOnlySequence<byte>` overload copies its segments straight into
the write buffer in 32 KiB chunks without combining them first; keep its memory unchanged until the
returned task completes. Both overloads always take the streaming path, which costs a few small
allocations per call, so use the ordinary `SetAsync` overloads for small values.

Respire sends each upload through a dedicated pooled connection. Uploads and blocking commands
share a pool owner, but active rentals have no fixed connection limit: when no compatible idle
connection exists, the client opens another connection instead of waiting for a free lease.
The pool retains at most four idle connections in total. Bound concurrent uploads in your application
when you need to limit Redis connections. Maintenance-enabled upload connections negotiate the
configured notifications and are kept separate from ordinary blocking leases.
Both lease kinds share the four-connection idle limit. Either kind can use all four slots when
the other kind has no idle connections. When the pool is full, a returning lease can reclaim
up to half the slots from the other kind, so neither kind continually reconnects under mixed demand.

After a standalone, Cluster, or Sentinel `MOVING` handoff publishes a replacement endpoint, new uploads use a new pool
for that endpoint. The old pool stops accepting rentals and drains uploads already in progress.

With Sentinel, an upload already assigned to a primary drains through its original connection
during failover. New uploads use the newly discovered primary. An interrupted upload is not
automatically replayed on the new primary, because the old primary may already have accepted it.

A slow source does not block
commands sent through the client's multiplexed connections. Once the header is queued, cancellation,
a read failure or a timeout before the complete frame has been written closes only the upload
connection to preserve RESP framing. Prefer seekable or in-memory sources. `CommandTimeout` covers
the whole upload, including every source read and socket write, so raise it (or set it to `null`)
for payloads that take longer than the timeout to transmit. If the upload connection closes while
Respire is reading the source, the call fails with
`RespireConnectionException` instead of waiting for the source. After the complete frame has been
written to the socket, cancellation only cancels the wait for its reply, and Redis may still apply
the write. In either case, Respire discards the cancelled upload's rented connection instead of
returning it to the idle pool, so a later upload cannot consume its abandoned reply.

After a transport failure once the header is sent, Respire does not replay the write: Redis may
or may not have applied it. Explicit Cluster `MOVED` and `ASK` replies reject the attempted command.
Respire follows those redirects for seekable streams and unchanged `ReadOnlySequence<byte>` sources,
replaying a seekable stream from its original position and sending `ASKING` when required.
Non-seekable streams return the redirect to the caller because their consumed bytes cannot be replayed.

Conditional writes use `SetWhen`:

```csharp
bool created = await redis.SetAsync(
    "lock:invoice:42",
    requestId,
    expiry: TimeSpan.FromSeconds(20),
    when: SetWhen.NotExists);
```

## Compare values before writing or deleting

`Strings.SetConditionalAsync` compares the stored bytes or digest atomically before
writing. `RespireValueCondition` keeps the comparison separate from the existing
`SetWhen.Always`, `NotExists`, and `Exists` overloads; their bindings do not change.

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

These APIs send the requested command directly. Unsupported commands, conditions,
malformed digests, and wrong-type keys retain server errors. There is no version
negotiation, script fallback, or substitution between Redis `DELEX` and Valkey
`DELIFEQ`. If the server flavor is unknown, determine its flavor and supported
version before choosing either conditional-delete API; neither is portable across
all Redis and Valkey versions. Use ordinary `Keys.DeleteAsync` for unconditional deletion.

Conditional SET returns `true` when it writes and `false` when the comparison fails.
For a missing key, equality conditions fail; Redis inequality conditions create it.
Conditional deletion returns `false` for missing keys or mismatches. A failed SET
preserves both the value and expiry. Successful SET supports relative and absolute
expiry or `RespireExpiry.Keep`; the default clears an existing expiry.
`RespireExpiry.Persist` is not a SET option.
Relative expiry truncates to whole milliseconds, matching ordinary SET. A positive
duration below one millisecond becomes `PX 0`, which the server rejects without changing
the stored value or expiry; Respire does not round it up silently.

`GetAndSetConditionalAsync` uses `SET ... GET` and returns the old string even when
the comparison fails. Null means the key was missing, so this result is **not a
write-success flag**. Its generic counterpart deserializes the old value and returns
owned data, including binary arrays. Generic SET methods serialize the new value
with the configured serializer; comparison operands always represent raw wire bytes.
Serialize a comparison operand yourself when comparing a serialized object.

```csharp
byte[] expectedBytes = [0xff, 0x00];
var condition = RespireValueCondition.EqualTo(expectedBytes);
byte[] replacement = [0x01, 0xfe];
byte[]? previous = await redis.Strings.GetAndSetConditionalAsync<byte[]>(
    "binary", replacement, condition, RespireExpiry.Keep);

// Valkey 9.0+: delete only if the stored bytes still match.
bool removed = await redis.Strings.DeleteIfEqualAsync("binary", replacement);
```

Condition factories copy binary operands, so a condition can be reused after the
original buffer changes. New-value buffers and `DeleteIfEqualAsync` operands follow
the usual borrowed-input contract: keep them unchanged until completion. Keys are
binary-safe, receive the view's prefix, and determine the Cluster slot; comparison
operands are never treated as keys. `DIGEST` returns an owned hexadecimal string or
null. Digests are finite hashes, so digest comparisons can collide; use byte equality
when exact equality is required.

Batches and transactions expose `SetConditional`, `GetAndSetConditional`,
`DeleteConditional`, `DeleteIfEqual`, and `Digest` on their `Strings` facet, including
the generic SET forms. Results become available after execution and remain owned
after the queue is disposed. Keep borrowed buffers unchanged until execution completes;
comparison conditions already own their operands. Immediate calls accept cancellation
tokens; deferred calls use the batch/transaction execution token. Cancellation does
not prove a command was not executed on the server.

```csharp
using var batch = redis.CreateBatch();
var pending = batch.Strings.SetConditional(
    "document", (RespireValue)"version-2", RespireValueCondition.EqualTo("version-1"));
await batch.ExecuteAsync();
bool changed = pending.Result;
```

Client-side caching invalidates the target key around immediate comparisons that may
mutate it, including failed comparisons. `DIGEST` does not flush cached reads.
Deferred execution retains the queue's conservative cache invalidation policy.

See the server contracts for Redis [SET](https://redis.io/docs/latest/commands/set/),
[DELEX](https://redis.io/docs/latest/commands/delex/), and
[DIGEST](https://redis.io/docs/latest/commands/digest/), plus Valkey
[SET](https://valkey.io/commands/set/) and [DELIFEQ](https://valkey.io/commands/delifeq/).

## Expiry

`RespireExpiry` is the single expiry argument: nothing, a relative TTL, an absolute instant, or "keep the TTL the key already has". A `TimeSpan` or `DateTimeOffset` converts implicitly.

```csharp
string token = "session-token";
await redis.SetAsync("session:42", token);                                  // no TTL (clears any existing one)
await redis.SetAsync("session:42", token, TimeSpan.FromMinutes(30));        // PX
await redis.SetAsync("session:42", token, RespireExpiry.At(midnight));         // PXAT
await redis.SetAsync("session:42", token, RespireExpiry.Keep);                 // KEEPTTL
```

`RespireExpiry` is the expiry you *send*; `RespireTtl` (returned by `Keys.ExpiryAsync`) is the expiry Redis *reports*.

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

Respire deliberately retains bare-varargs calls such as `DeleteAsync("a", "b")`, so multi-item
commands use a uniform pair of overloads rather than a single optional-token span overload.

`SetManyIfNotExistsAsync(pairs, cancellationToken)` uses atomic
[MSETNX](https://redis.io/docs/latest/commands/msetnx/), also supported by
[Valkey](https://valkey.io/commands/msetnx/). It returns `false` without changing any key when
one already exists, even if that key has another data type. New values have no expiry.
At least one pair is required; empty keys and values are valid. Duplicate keys are sent in order:
when the key was absent, the last value wins. Cluster keys must share a slot after prefixing.
A slot mismatch is rejected locally, before I/O or enqueueing, with `RespireServerException`
whose `Code` is `CROSSSLOT`.
Values use raw `RespireValue` encoding, including binary values. Keep binary buffers unchanged
until completion. Batches and transactions expose `Strings.SetManyIfNotExists(pairs)` with the
same boolean result; cancellation is passed to batch execution or transaction commit.

Variadic APIs use `params ReadOnlySpan<T>` where possible, avoiding a params-array allocation on supported C# toolchains. Because a `params` parameter must come last, each of these has a sibling overload taking the items non-params plus a required `CancellationToken`:

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
await redis.Keys.ExpireAsync("lease", TimeSpan.FromMinutes(10), ExpireWhen.GreaterThan);

var value = await redis.Strings.GetAndExpireAsync("session:42", TimeSpan.FromMinutes(30));
CachedJob? job = await redis.Strings.GetAndDeleteAsync<CachedJob>("jobs:next");
```

The generic combined-get forms deserialize through the client's configured serializer. Hash
field equivalents use `GetAndRemoveAsync` and `GetAndExpireAsync`; removing fields keeps the hash key unless it becomes empty.

`TypeAsync` returns `RespireKeyType` rather than a server string. Conditional rename and copy are
available without dropping to raw commands:

```csharp
RespireKeyType type = await redis.Keys.TypeAsync("session:42");
bool renamed = await redis.Keys.TryRenameAsync("draft", "published");
bool copied = await redis.Keys.CopyAsync("template", "working-copy", replace: true);
```

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

Use `ScanAsync("session:*", RespireKeyType.Hash)` to filter by type with the default count hint.

`countHint` maps to Redis `COUNT`; it guides work per iteration but does not guarantee page size.
Prefer `SCAN` over `KEYS` in production; each page yields control and avoids a single server-blocking sweep.

## Key-prefixed views

Create a lightweight client view when one service or tenant needs a namespace:

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

Both overloads reject empty prefixes. Views snapshot binary prefix memory when created;
changing the original array afterward cannot change the namespace. Ordinary command key
memory retains its existing borrowing contract. Nested views append prefixes in order.
Text-only composition preserves string concatenation semantics, including UTF-16 surrogate
pairs split across a boundary. Once a binary component is present, composition preserves
its exact bytes and encodes subsequent text components independently as UTF-8.

Views share connections and the client-side cache. Disposing a view does not dispose the
root client. Read-routing and cache-bypass policies survive composition. Keys are prefixed
before Cluster slot selection, including hash tags that cross prefix boundaries. Cache
invalidation and keyspace notification filters use physical keys; `ResolveKey` returns that
physical identity. Regular pub/sub channels and notification descriptors remain unchanged.
Use [WithPubSubPrefix](../guides/pub-sub.md#explicit-pubsub-prefixes) to opt application channels
into a separate namespace; composing either view preserves the other namespace.

Typed key arguments, script keys, streams, batches, and transactions use the same prefix.
Supported deferred raw layouts and explicitly registered immediate module layouts also
prefix key arguments. Immediate caller-supplied core commands and interpolated commands
still require physical keys. Immediate catalog commands without an explicitly prefixable
layout are rejected, as are unsupported deferred layouts. A prefix is not an authorization
boundary. `SORT BY`/`GET` external patterns reject prefixes containing `*`,
`->`, or NUL because Redis would reinterpret those bytes as pattern syntax.

Scans escape literal prefix metacharacters in `MATCH` and return keys with the prefix
removed. Binary prefixes are filtered and stripped before decoding the suffix, so different
invalid UTF-8 prefixes cannot collapse into the same namespace. `ScanAsync` and
`ScanClusterPageAsync` still return strings: invalid UTF-8 suffixes retain replacement
decoding and cannot be round-tripped losslessly through those string APIs. Resume a Cluster
checkpoint with the same prefix representation and filter. Existing text checkpoints keep
their format; binary-prefix checkpoints use a new format that older clients cannot read.

### Default root namespace

Set `RespireOptions.KeyPrefix` to apply the same namespace to the root client:

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

`Create` snapshots the prefix before the first connection; `ConnectAsync` snapshots it
before connecting. Each `ConnectAnyAsync` candidate keeps its own configured prefix.
Empty/default prefixes disable root prefixing, while `WithKeyPrefix` still rejects empty
view prefixes. Binary configuration preserves exact bytes and snapshots mutable input;
text configuration preserves the same UTF-16 boundary behavior as text views. Derived
views append to the default namespace and retain routing and cache policies. Dispose the
root client to close its connections, even when it has a configured prefix.

Connection strings accept `keyPrefix` in both supported forms:

```text
redis://localhost:6379/0?keyPrefix=tenant%3A42%3A
localhost:6379,keyPrefix=tenant%3A42%3A
```

Values are percent-decoded exactly once and encoded as UTF-8 text. Use
`Uri.EscapeDataString(prefixText)` when constructing a value, including for literal `%`,
commas, `&`, `=`, NUL, or leading/trailing spaces. A literal `+` stays `+`;
use `%20` for a space. Comma-delimited values are trimmed before decoding.
Decoding follows `Uri.UnescapeDataString`: malformed escapes and undecodable UTF-8
bytes remain escaped literal text (`%FF` stays `%FF`); they do not encode arbitrary binary keys.
Option names are case-insensitive, repeated `keyPrefix` entries use the last value,
and `keyPrefix=` disables prefixing. Unknown options still throw. There is no
connection-string serializer; binary prefixes that are not UTF-8 text require structured
options rather than a textual connection string.

The command-layout, scan, Cluster, cache and authorization limits above apply equally to
root namespaces. Cache tracking prefixes and invalidation observers still use physical
keys, including the root prefix; configuration does not rewrite these filters.
Regular pub/sub channels remain literal. Explicit pub/sub prefixing is tracked separately
in [#1120](https://github.com/thomhurst/Respire/issues/1120).

## Absolute expiration and object metadata

`Keys.ExpiryTimeAsync(key)` uses Redis 7's `PEXPIRETIME`; pass `ExpiryTimePrecision.Seconds`
for `EXPIRETIME`. Both return `RespireExpiryTime`, the same result used for hash-field expiration.
`Exists` distinguishes a missing key from a persistent key. `HasExpiry` and nullable
`UnixTimeMilliseconds` describe an absolute expiration, not a remaining TTL. The seconds variant
preserves the server's whole-second result and expresses it in milliseconds. Use
`TryGetExpiresAt(out var instant)` when a Redis timestamp might exceed `DateTimeOffset`'s range.

```csharp
RespireExpiryTime expiration = await redis.Keys.ExpiryTimeAsync("session:42");
string? encoding = await redis.Keys.EncodingAsync("session:42");
TimeSpan? idle = await redis.Keys.IdleTimeAsync("session:42");
long? references = await redis.Keys.ReferenceCountAsync("session:42");
```

`EncodingAsync`, `IdleTimeAsync`, `FrequencyAsync`, and `ReferenceCountAsync` map to
`OBJECT ENCODING`, `IDLETIME`, `FREQ`, and `REFCOUNT`. Missing keys return null. Idle time has
second resolution. Frequency is a logarithmic counter, not an exact access count, and requires
an LFU eviction policy; `IDLETIME` is unavailable under LFU. Unsupported policy combinations
retain the server error. Encoding names and reference counts describe Redis implementation details
and can change with value size, encoding, or server version. These methods support binary keys and
apply the client view's key prefix before routing.

Batch and transaction `Keys` facets expose `ExpiryTime`, `Encoding`, `IdleTime`, `Frequency`, and
`ReferenceCount` with the same results. Deferred strings own their data after execution; none of
these metadata results requires disposal.

Existing `Keys.ExpireAsync` / queued `Keys.Expire` support `ExpireWhen.NotExists` (`NX`), `Exists`
(`XX`), `GreaterThan` (`GT`), and `LessThan` (`LT`). Relative inputs use `PEXPIRE`; absolute inputs
use `PEXPIREAT`. `RespireExpiry.Persist` uses `PERSIST`, which rejects these conditions.

See Redis's [EXPIRETIME](https://redis.io/docs/latest/commands/expiretime/),
[OBJECT IDLETIME](https://redis.io/docs/latest/commands/object-idletime/), and
[OBJECT FREQ](https://redis.io/docs/latest/commands/object-freq/) command references.


### Resumable Cluster scans

In Cluster mode, `Keys.ScanAsync` uses the same slot-aware page engine as
`Keys.ScanClusterPageAsync`. Use the page API when work needs a durable checkpoint:

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

Each cursor is immutable. Reusing one repeats that scan position; a failed or cancelled
page call leaves its input cursor usable. Retry that input after a transient connection
failure. Save the returned cursor only after processing its keys, and make processing
idempotent: SCAN and CLUSTERSCAN may return duplicates. Empty pages can still have an incomplete
cursor. `COUNT` is a server work hint, not a maximum number of returned keys.
`CompletedSlotCount` reports progress across 16,384 slots and can decrease after resharding.
When the effective physical `MATCH` proves a single slot (a fixed hash tag such as
`{tenant}:*`, or an exact key without glob metacharacters), other slots cannot contain
matching keys and count as complete immediately. The scan starts at that slot's owner;
it does not bootstrap every preceding slot. Wildcards or escapes before the hash tag,
and text prefixes with a surrogate boundary, retain the general scan path.

Keep the same database, `match`, `type`, and key-prefix view when resuming. These are bound into the
cursor and a mismatch fails before network access; `countHint` may change. Prefixes are
glob-escaped for matching, and returned keys have the literal prefix removed, just as with
`ScanAsync`. The string API retains its existing UTF-8 decoding semantics for key names.

For a new pass, the client queries `COMMAND INFO CLUSTERSCAN` on the selected physical
primary. A recognized command entry selects Valkey's CLUSTERSCAN; a null entry selects
legacy SCAN. Evidence is cached for that connection and process run ID, so a new socket
or replacement process is checked again. Denied or unknown metadata permits a legacy
pass without caching command absence. If CLUSTERSCAN execution is denied at the initial,
unredirected bootstrap, the pass uses SCAN without caching command absence. Denied opaque
continuations and redirected execution still fail explicitly, preserving the checkpoint.
An exact unsupported-command reply from a destination permits fallback on that destination;
it does not change the redirect source's capability evidence.

Modern passes scan a contiguous primary-owned slot range. The initial SLOT request obtains
an opaque server position; subsequent requests pass it unchanged and let CLUSTERSCAN traverse
the range. Its hash tag identifies the physical slot for routing and range completion;
the client never interprets its fingerprint or local position as a numeric SCAN cursor.
A MOVED/ASK response follows normal Cluster recovery after checking the destination's
capability. A redirected page cannot certify the previous owner's range. Older servers and
mixed-version clusters retain the SCAN-per-primary path. Downgrade restarts the unfinished
pass from numeric zero, retaining unaffected completed slots and allowing duplicate keys.
An existing numeric pass finishes with SCAN before selecting a new protocol.
The literal zero bootstrap can be redirected before any keys are scanned. Its empty
slot cursor is retained for the validated slot owner without certifying progress. An
unsupported bootstrap destination permits legacy scanning on that validated owner.
Redirects and physical retirement share one bounded retry budget per page call.
ASKING and CLUSTERSCAN stay pinned to the checked destination. Socket retirement
returns to that page budget, and the replacement's process and capability are checked again.

The cursor records slot ownership/completion and the active primary's server cursor,
configuration epoch, and process run ID. Each page refreshes topology and reads primary-local
[CLUSTER NODES](https://redis.io/docs/latest/commands/cluster-nodes/) metadata; migration
annotations appear only on the queried node's own row. [INFO server](https://redis.io/docs/latest/commands/info/)
validates the process before reusing its cursor.
A completed node pass is validated again before its stable slots are marked complete.
Moved slots become pending on the new owner, including an owner scanned earlier; unaffected
completed slots stay complete. A changed process or configuration epoch restarts the active
node pass. Migrating/importing slots cannot complete until their transition settles.
Continuous epoch changes can prevent an active pass from finishing; unaffected slots already
validated remain complete. Use cancellation or an application deadline to bound a scan.
When only moving slots remain, a page sets `WaitingOnMigration`, contains no keys, and issues
no INFO, capability probe, SCAN or CLUSTERSCAN. Page callers should delay before retrying. `ScanAsync` waits 50 ms, doubling
to a maximum of 250 ms between consecutive waiting pages; stable work resets this delay.
The wait observes the enumeration's cancellation token.
Redis scans whole node dictionaries, so rescanning affected slots still traverses the new
owner's dictionary, while filtering already completed slots from the result.

This requires `SCAN`, `CLUSTER SLOTS`, `CLUSTER NODES`, and `INFO` permissions. Modern
selection additionally uses `COMMAND INFO` and `CLUSTERSCAN`; fallback does not widen ACLs. Metadata
validation adds round trips per page; use a larger count hint to amortize them. Incomplete
or contradictory ownership and unavailable primaries fail explicitly instead of silently
omitting keys. The scan follows Cluster-reported ownership and epochs; it is not a database
snapshot or a history of unreported topology changes. Topology is sampled on every page,
not only at pass boundaries. A reported migration or owner change invalidates that slot's
active pass even if its original owner returns later. A complete A-to-B-to-A move between
two metadata reads can remain invisible when the reported identities and epochs also match;
this scan cannot guarantee coverage across that unobserved history. For observed changes,
continuously present keys remain covered across completed resharding and failover. Keys
inserted/deleted during iteration have Redis SCAN's usual weak guarantees. Continuous topology changes may prevent completion;
use cancellation to bound the work. Standalone scans retain their existing cursor loop.

`Parse` rejects malformed, oversized, or unsupported-version tokens; `TryParse` returns
false instead. Tokens are versioned Base64 data, not encrypted or authenticated. They contain
filters and node identities, but no passwords, sockets, or process-local registry handles.
This client reads existing RSC1 text-prefix and RSC2 binary-prefix checkpoints, and writes
RSC3 checkpoints with database identity and separate numeric/opaque positions. Older clients
that only understand RSC1/RSC2 reject RSC3; new checkpoints are not universally portable
between client versions. Legacy tokens did not record a database: resume them against their
original database, which is bound when the next checkpoint is published. Tokens do not
identify a unique cluster, so callers must also reconnect to the original cluster.
RSC3 retains exact UTF-16 filter and text-prefix identity, including unpaired surrogates.
Legacy UTF-8 text tokens cannot reconstruct code units already replaced during serialization.
Treat checkpoints as trusted application state; do not accept arbitrary client-supplied tokens
as validated scan progress. Authenticate them at the application boundary if they cross a
trust boundary, and enforce storage/request size limits. Tokens can be several kilobytes;
the encoded ceiling is 6 MiB and fragmented slot layouts increase their size. Start a new scan
with `Start` after completion.

### Valkey 9.1 server cursors

`Keys.ScanValkeyClusterPageAsync` explicitly uses Valkey 9.1+ `CLUSTERSCAN` in Cluster mode.
It returns an owned `RespireValkeyClusterScanPage` containing an opaque string cursor and
binary-safe `RespireKey` values. Start with `"0"` and pass each returned cursor unchanged:

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

Only `"0"` means completion. Empty pages and duplicate keys are valid; make processing
idempotent. `COUNT` controls server work, not page size. Keep the same cluster, database,
prefix, `match`, `type`, and optional `slot` when resuming, including from another client.
The server cursor does not bind these options for the client. A failed or cancelled call
leaves the input string available for retry. Apply cancellation or an application deadline
to bound scanning during topology changes.

Valkey fingerprints server scan state. A changed fingerprint can restart the affected scan
range, so duplicates can occur after server or topology changes. Migration redirects follow
the current slot owner; this is not a database snapshot.

The cursor contains routing information and is never key-prefixed. Requests use primaries
regardless of `ReadFrom`, with ordinary `MOVED`/`ASK` handling. Optional `slot` restricts the
scan to a physical slot from 0 through 16383. A prefixed view escapes its literal prefix
in `MATCH`, filters returned physical keys, and removes that prefix. Pass returned keys
directly back to the same view; converting arbitrary binary keys to strings can lose data.
Page keys and cursor remain valid after the reply or client is disposed.

When a string prefix ends with a high UTF-16 surrogate, string keys can join it
to a leading low surrogate while binary keys use the prefix's replacement-byte
encoding. This namespace has no single literal UTF-8 prefix. These pages omit the
server `MATCH` filter and apply namespace and bytewise glob filtering locally to
the logical suffix. `COUNT`, `TYPE`, `SLOT`, cursor routing, and cancellation retain
their normal behavior; sparse filtered pages can be empty before completion.
Returned keys retain the boundary information needed to round-trip even binary
tails through a view with the same prefix, including on a new client.
Their equality and hashing include the original namespace, exact UTF-16 boundary,
and binary tail. Different low surrogates therefore remain distinct in a
`HashSet<RespireKey>`, while repeated pages and snapshots retain equal identities.
These boundary keys do not compare equal to ordinary replacement-byte suffixes.

One page can also be queued with `batch.Keys.ScanValkeyClusterPage(...)` or
`transaction.Keys.ScanValkeyClusterPage(...)`. Execute or commit before reading
the pending result. The next page needs the preceding result's cursor, so a whole
cursor walk cannot be queued in advance. Ordinary Cluster batch and transaction
slot constraints apply, and results own their keys after completion.
In a non-primary read view, a read-only batch containing CLUSTERSCAN still routes
its scan group to a primary without flushing the shared client cache. A mixed
write batch and a primary-view batch retain conservative cache invalidation.

This explicit API retains unsupported-command and permission errors from older servers.
Its raw server cursor is distinct from `ScanClusterPageAsync`'s immutable
`RespireClusterScanCursor` checkpoint. Do not pass one format to the other API. See Valkey's
[CLUSTERSCAN reference](https://valkey.io/commands/clusterscan/) for server scan guarantees.

#### Database ACLs

Valkey 9.1 adds database ACL rules. For example, this rule limits an existing user's
database access to databases 0 and 1 while retaining its other command and key rules:

```text
ACL SETUSER app-user resetdbs db=0,1
```

Apply matching ACLs on every cluster node. Nonzero cluster databases also require a server
`cluster-databases` setting large enough to include the selected database. Set
`RespireOptions.Database = 1` and the user's `Username`/`Password` when connecting;
`CLUSTERSCAN` scans that selected database. Database access does not grant command or key
permissions. A denied database selection can surface as a connection exception containing
the server's `NOPERM` error; command permission failures retain their server error.
Respire does not silently switch databases or broaden permissions.
See the [Valkey 9.1 announcement](https://valkey.io/blog/valkey-9-1-delivers-improvements-in-security-performance-and-more/).
