---
title: Strings and keys
description: Read, write, expire, scan, and manage Redis keys.
---

# Strings and keys

Frequent operations are available directly on `IRespireClient`; complete string and key surfaces live under `Strings` and `Keys`.

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
