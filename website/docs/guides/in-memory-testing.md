---
title: In-memory testing
description: Exercise real Respire client code against a deterministic in-memory server with collections, pub/sub, and transactions, without Docker.
---

# In-memory testing

`Respire.Testing` supplies a small RESP server connected through two in-memory pipes.
Your application uses a real `RespireClient`: command serialization, coalescing, FIFO
response dispatch, result leases, serialization settings, prefixing, and batches all run
through the normal client code. The fake opens no TCP socket and requires no Docker daemon.
Keep `Respire.Testing` and `Respire` at the same package version: the test transport uses an
internal core hook. The repository builds and publishes them with one generated version.

```csharp
using Respire.Testing;

var clock = new RespireFakeClock();
await using var server = new RespireFakeServer(clock);
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.SetAsync("greeting", "hello");
using var expiration = await client.ExecuteAsync(RespireCommands.Key.PEXPIRE, "greeting", 1000);
clock.Advance(TimeSpan.FromSeconds(1));
if (await client.GetStringAsync("greeting") is not null)
    throw new InvalidOperationException("The key should have expired.");
```

Each server has independent data. Multiple clients connected through its options share
that data and execute each command atomically. `CreateOptions()` returns fresh endpoint
collections; clone options to select RESP2/RESP3, connection count, serializers, and client
timeouts. Keep the original endpoint and options object lineage: a connection string cannot
represent an in-memory server. Changing the endpoint causes `NotSupportedException` on connection.
Both eager `ConnectAsync` and lazy `Create` clients work.

Dispose clients before their server. Disposing one client leaves other clients and server
data intact. Server disposal closes all owned pipe endpoints and joins server loops;
concurrent calls join the same cleanup task. It does not dispose your client objects.
Previously obtained options cannot create connections after server disposal.
Unexpected command/clock failures and malformed requests close the affected connection and are
reported by server disposal. Completed connections are removed; their exceptions remain retained
until disposal so errors cannot be lost after a client disconnects. Keep fixture lifetimes bounded
and dispose the server
even when a test fails. Other connections remain independent; a command failure does not
make future connections throw a historical error.

## Supported subset

For `INCREX BYFLOAT`, the fake calculates with .NET `double`, while Redis uses
platform-dependent `long double`. Rounding near bounds can therefore differ.
Increments, stored values, and results outside the fake's finite `double` range
fail before mutation; bounds may be infinite.
Use a real Redis fixture when testing floating-point precision or extreme values.

This is a test double with explicit limits, not a Redis implementation or a compatibility
oracle. Unsupported commands return server errors containing the command name.
Unsupported options also fail explicitly instead of silently changing behavior.
An error consumes exactly one response slot, so later valid commands still work.

| Area | Supported commands and options |
| --- | --- |
| Strings | `GET`, `SET` with `NX`, `XX`, `GET`, `KEEPTTL`, `EX`, `PX`, `EXAT`, `PXAT`; `MGET`, `MSET`, `MSETNX`, `GETDEL`, `GETSET`, `GETEX`, `STRLEN`, `APPEND` |
| Integer strings | `INCR`, `DECR`, `INCRBY`, `DECRBY`, with checked signed 64-bit arithmetic |
| Extended counters | `INCREX` with integer/floating increments, bounds, `SATURATE`, expiry, and `ENX`; integer arithmetic preserves the full signed 64-bit range |
| Hashes | `HSET`, `HSETNX`, `HMSET`, `HGET`, `HMGET`, `HGETALL`, `HDEL`, `HEXISTS`, `HLEN`, `HKEYS`, `HVALS`, `HSTRLEN`, `HINCRBY` |
| Lists | `LPUSH`, `RPUSH`, `LPUSHX`, `RPUSHX`, `LPOP`/`RPOP` with optional count, `LLEN`, `LRANGE`, `LINDEX`, `LSET`, `LTRIM`, `LREM`, `LINSERT BEFORE/AFTER`, `LPOS RANK/COUNT/MAXLEN` |
| Sets | `SADD`, `SREM`, `SMEMBERS`, `SCARD`, `SISMEMBER`, `SMISMEMBER`, `SMOVE`, `SINTER`, `SUNION`, `SDIFF`, their `STORE` forms, and `SINTERCARD` with `LIMIT` |
| Sorted sets | `ZADD` with `NX`, `XX`, `GT`, `LT`, `CH`, `INCR`; `ZINCRBY`, `ZREM`, `ZCARD`, `ZSCORE`, `ZMSCORE`, `ZRANK`, `ZREVRANK`, `ZCOUNT`, `ZLEXCOUNT`; `ZRANGE` with `BYSCORE`/`BYLEX`, `REV`, `LIMIT`, `WITHSCORES`; legacy `ZREVRANGE`, `ZRANGEBYSCORE`, `ZREVRANGEBYSCORE`, `ZRANGEBYLEX`, `ZREVRANGEBYLEX`; `ZPOPMIN`, `ZPOPMAX`; `ZREMRANGEBYRANK`, `ZREMRANGEBYSCORE`, `ZREMRANGEBYLEX`; `ZINTERCARD` with `LIMIT` |
| Pub/sub | `SUBSCRIBE`, `UNSUBSCRIBE`, `PUBLISH`, with binary channel names and payloads |
| Keys | `DEL`, `UNLINK`, `EXISTS`, `TYPE`, `PERSIST` |
| Expiry | `EXPIRE`, `PEXPIRE`, `EXPIREAT`, `PEXPIREAT` with `NX`, `XX`, `GT`, `LT`; `TTL`, `PTTL`, `EXPIRETIME`, `PEXPIRETIME` |
| Connection | `HELLO 2/3` without authentication, `PING`, `ECHO`, `SELECT 0`, `CLIENT ID`, `CLIENT GETNAME`, `CLIENT SETNAME` |
| Transactions | `MULTI`, `EXEC`, `DISCARD`, `WATCH`, `UNWATCH` with connection-owned queues and optimistic concurrency |

`GETEX` supports its expiry options and `PERSIST`. Binary keys and values are preserved,
including empty values and embedded zero bytes. Multi-key mutations are atomic; integer
parsing and overflow errors leave the original value unchanged. `UNLINK` removes data
synchronously because this fake does not model background memory reclamation.

Hash fields and values preserve binary bytes. `HSET` counts newly added fields, including
duplicate fields within one command only once; `HMGET` preserves requested field order and
nulls. `HGETALL` emits a RESP2 array or RESP3 map. Hash enumeration order is unspecified.
`HINCRBY` uses checked signed 64-bit arithmetic. Hash mutations retain key-level TTL; removing
the last field deletes the key and its TTL. String operations reject hashes, lists, sets, and sorted sets with `WRONGTYPE`,
except `MGET` returns null for non-string keys and ordinary `SET`/`MSET` may replace their type.
Failed operations preserve existing fields and TTL. Hash-field expiry, `HINCRBYFLOAT`,
`HRANDFIELD`, and `HSCAN` are explicitly unsupported.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.Hashes.SetAsync("user:1", "name", "Ada");
await client.Hashes.IncrementAsync("user:1", "visits");
var fields = await client.Hashes.GetAllAsync("user:1");
if (fields["name"] != "Ada" || fields["visits"] != "1")
    throw new InvalidOperationException("Hash fields did not round-trip.");
```

Set members preserve binary bytes and uniqueness. `SMEMBERS` and set algebra return a
RESP2 array or RESP3 set; enumeration order is unspecified. `SMISMEMBER` preserves input
order and duplicates. `SMOVE` validates both existing types before mutation; moving within
the same set only tests membership. A missing source returns zero even if the destination
has another type. `SINTERCARD LIMIT 0` counts the complete intersection; a positive limit
stops counting at that bound.

Ordinary set mutations retain existing key TTLs. Removing or moving the last member deletes
the source key. STORE commands calculate from all source sets before replacing the destination,
so source/destination aliasing is supported. STORE replaces the destination type, clears its TTL,
and deletes it when the result is empty. Wrong-type failures leave existing members and TTLs
unchanged. Random sampling (`SPOP`, `SRANDMEMBER`) and `SSCAN` remain unsupported.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.Sets.AddAsync("online", "Ada", "Grace");
await client.Sets.AddAsync("subscribers", "Grace", "Linus");
var onlineSubscribers = await client.Sets.IntersectAsync("online", "subscribers");
if (onlineSubscribers.Length != 1 || onlineSubscribers[0] != "Grace")
    throw new InvalidOperationException("Set intersection did not match.");
```

Lists retain binary elements, duplicates, insertion order, and key TTL across mutations.
Negative indexes count from the tail. `LRANGE`/`LTRIM` use inclusive end indexes;
`LREM` uses positive counts from the head, negative counts from the tail, or zero for all matches.
`LPOS` supports reverse ranks, unlimited `COUNT 0`, and a `MAXLEN` scan bound. Without
`COUNT`, a missing match returns null; with it, the reply is an array. Count-pop returns
null for a missing key and an empty array for count zero on an existing list.
Ranks must be nonzero and have a magnitude no greater than `long.MaxValue`, matching Redis 7.2+.
Repeated valid options use their final values, but every occurrence is validated immediately:
`RANK 0 RANK 1`, `COUNT -1 COUNT 1`, and `MAXLEN -1 MAXLEN 0` still fail.
Removing the final element deletes the key and its TTL. Wrong types and malformed options
fail without changing data. Immediate commands and batches use the same handlers.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.Lists.RightPushAsync("jobs", 10, 20, 30);
using var batch = client.CreateBatch();
var first = batch.Lists.LeftPop<int>("jobs");
var rest = batch.Lists.RightPopMany<int>("jobs", 2);
await batch.ExecuteAsync();
if (first.Result != 10 || !rest.Result.SequenceEqual(new[] { 30, 20 }))
    throw new InvalidOperationException("List ordering did not round-trip.");
```

Multi-element moves (`LMOVEM`, `BLMOVEM`) support COUNT/EXACTLY and OBO/BULK, including
same-list ordering, wrong-type validation, TTL preservation, and WATCH invalidation.
BLMOVEM waits for the required source length without holding the server-state lock. Its
timeouts use wall-clock time; `RespireFakeClock` still controls only key expiry. Cancellation
or server disposal releases the wait. Inside MULTI, BLMOVEM runs immediately as Redis does.

Other blocking pops, multi-key pops, and moves (`BLPOP`, `BRPOP`, `LMPOP`, `BLMPOP`, `LMOVE`,
`BLMOVE`, `RPOPLPUSH`, and `BRPOPLPUSH`) remain explicitly unsupported. Use the nonblocking
typed overloads without `waitFor`; a populated list does not make a blocking command supported.

Sorted sets preserve binary members and order equal scores by unsigned member bytes. Rank
ranges have inclusive endpoints and support negative indexes; score and lex ranges support
exclusive and infinite bounds. Lex ranges require equal scores for meaningful Redis parity.
`REV` reverses both score order and tie order; score/lex bounds then appear maximum first.
`LIMIT` applies to score/lex ranges; a negative offset yields no entries, and a negative count
means all remaining entries. `WITHSCORES` cannot combine with `BYLEX`. Repeated `LIMIT`
uses its final values. `ZINTERCARD` validates every input type before taking an empty-input
shortcut, accepts both set and sorted-set inputs, and treats zero `LIMIT` as unlimited.

Scores accept decimal/exponent and hexadecimal floating-point input, including subnormal
values and explicit infinities. NaN, malformed numbers, and numeric overflow or underflow
to zero are rejected before mutation. Adding finite scores can produce infinity;
an increment producing NaN fails without changing the member. `ZADD CH` counts successful
additions and score changes, including successive changes to a repeated member. Mutations
retain expiry; removing the final member deletes the key and its expiry. Missing scores/ranks
are null, and missing ranges/pops are empty arrays. RESP3 scored ranges and counted pops
contain nested member/score pairs; a pop with no count remains flat on both protocols.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.SortedSets.AddAsync("scores", ("Ada", 10), ("Grace", 20));
using var batch = client.CreateBatch();
var count = batch.SortedSets.Count("scores");
var members = batch.SortedSets.Range("scores", descending: true);
await batch.ExecuteAsync();
if (count.Result != 2 || members.Result[0] != "Grace")
    throw new InvalidOperationException("Sorted-set batch results did not match.");
```

Sorted-set random sampling (`ZRANDMEMBER`), scanning (`ZSCAN`), blocking/multi-key pops
(`BZPOPMIN`, `BZPOPMAX`, `ZMPOP`, `BZMPOP`), aggregate result/store commands
(`ZUNION`, `ZINTER`, `ZDIFF` and their `STORE` forms), `ZRANGESTORE`, and the newer
`ZRANK`/`ZREVRANK WITHSCORE` option are explicitly unsupported. Sorting is performed
on demand for bounded test data; it does not model Redis's indexing or performance.

Pub/sub subscriptions belong to their connection. A duplicate `SUBSCRIBE` acknowledges each
argument but counts each distinct channel once. `UNSUBSCRIBE` acknowledges missing channels;
without arguments it removes every subscription, or acknowledges a null channel if none exist.
`PUBLISH` counts subscribed connections, not the number of client-side subscription readers.
Messages and confirmations use RESP2 arrays or RESP3 pushes. In RESP2 subscribed mode, this
subset permits only `SUBSCRIBE`, `UNSUBSCRIBE`, and `PING`; RESP3 permits ordinary commands
while subscribed. A RESP3 connection publishing to its own subscribed channel receives the
complete command reply before the message, including the complete `EXEC` array for queued
publications. This follows Redis 7.2+ ordering; older Redis versions can interleave pushes
inside transaction replies. Deferred messages count toward the same 16 MiB output limit.
Pattern and sharded commands (`PSUBSCRIBE`, `PUNSUBSCRIBE`, `SSUBSCRIBE`,
`SUNSUBSCRIBE`, `SPUBLISH`), `PUBSUB` diagnostics, `QUIT`, and `RESET` are unsupported.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await using var subscription = await client.SubscribeAsync("events");
if (await client.PublishAsync("events", "ready") != 1)
    throw new InvalidOperationException("The subscription should be active.");
await using var messages = subscription.GetAsyncEnumerator();
if (!await messages.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
    || messages.Current.Text != "ready")
    throw new InvalidOperationException("The publication did not round-trip.");
```

Each connection has one ordered output writer. Acknowledgements precede later publications,
including when an after-execution fault holds the acknowledgement. Publications never wait
for a subscriber to read. The fake disconnects a slow subscriber when its pending encoded
push bytes would exceed 16 MiB, including a push currently waiting for the pipe to flush.
The bound applies to complete encoded frames, including framing bytes, even for an empty queue.
This is a fixed test-fixture bound, not Redis's configurable output-buffer policy. Receiver
counts do not guarantee delivery: a connection can close after being counted. The publication
that exceeds the bound still counts the disconnected subscriber; subsequent publications do not.
Closing a
connection removes its routes; client/server disposal also cancels and joins pending output.
The real client can reconnect and resubscribe, emitting its normal gap marker. Publications
during a gap are lost; the fake does not retain or replay them. Caller cancellation alone
does not undo an accepted subscription; release held replies or dispose the client to let
its normal cleanup finish.

## Transactions and WATCH

Use `CreateTransaction()` or `CreateTransactionAsync(watchedKeys)` so the real client keeps
all transaction commands on the same connection. The fake copies queued arguments and does
not execute them until `EXEC`. The whole sequence runs atomically under the server lock and
uses one expiry-clock sample. Results retain command order; execution errors occupy their
own array elements and do not roll back successful commands.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await using var other = await RespireClient.ConnectAsync(server.CreateOptions());
await client.SetAsync("version", "1");
await using var transaction = await client.CreateTransactionAsync(["version"]);
var pending = transaction.Set("result", "committed");
await other.SetAsync("version", "2");
if (await transaction.CommitAsync())
    throw new InvalidOperationException("WATCH should have aborted the transaction.");
if (pending.Status != RespirePendingStatus.Aborted || await client.ExistsAsync("result"))
    throw new InvalidOperationException("An aborted transaction must not mutate data.");
```

`WATCH` observes supported string, key, hash, list, set, and sorted-set mutations and expiry, including
expiry detected at `EXEC` without an intervening read. Rejected conditional writes and true
no-ops do not invalidate a watch. Redis treats some equal-value operations as writes: `SET`,
`HSET`, `LSET`, and an unchanged `LTRIM` still invalidate watches. Watching an already expired
key treats it as absent. `UNWATCH`, `DISCARD`, completed or aborted `EXEC`, and connection
closure release watch state. `UNWATCH` inside `MULTI` is itself queued.

Unknown commands and command-table arity errors invalidate the queue; `EXEC` then returns
`EXECABORT`. Handler-level option/value errors remain queued and become individual EXEC
errors. Nested `MULTI` and `WATCH` inside `MULTI` fail without invalidating the existing
queue. A changed watch aborts with a RESP2 null array or RESP3 null, exposed as a false
watched commit. Each connection may retain at most 16 MiB of estimated queued argument
storage; exceeding that bound rejects the command and invalidates the queue.

Protocol changes (`HELLO`) and subscription control (`SUBSCRIBE`, `UNSUBSCRIBE`) inside
`MULTI` are explicitly unsupported and invalidate the queue. Unsupported command families
remain errors in transactions too. Fault rules match received wire commands, including
queue admission and `EXEC`; executing a queued handler does not match a second fault.
A fault before `EXEC` can prevent all mutations; a disconnect after `EXEC` loses the reply
after all mutations have run. The client never replays an ambiguously accepted transaction.
Cancellation abandons the caller's wait, not an accepted transaction or its effects.
Disposing a watched transaction closes its dedicated connection if it cannot safely return
it to the pool; server disposal joins connection loops and clears queues and watches.

These semantics follow [Redis transactions](https://redis.io/docs/latest/develop/using-commands/transactions/)
and the [Redis transaction implementation](https://github.com/redis/redis/blob/7.2/src/multi.c).

## Remaining limits

Only database zero and standalone operation are supported. Authentication, TLS, Cluster,
Sentinel, scripts/functions, client-side tracking,
persistence and administrative diagnostics are not simulated. Unsupported handshake features
fail initialization. Do not enable these modes and infer production behavior from the fake.
Individual RESP requests are limited to 16 MiB; larger requests close their connection
and report the size-limit error when the server is disposed.
Arguments are copied into owned arrays and commands execute under one server lock.
Incomplete requests can reparse already received arguments; highly fragmented arrays may cost
more CPU than contiguous requests. This fixture is not a throughput benchmark tool.
There is no eviction policy or total memory budget: keep test datasets bounded.

## Expiry and determinism

Without an injected clock, expiry uses `TimeProvider.System`. `RespireFakeClock` starts at
the Unix epoch by default and advances only when requested. A command samples time once,
and keys expire on access at or after their deadline. `TTL` uses Redis's nearest-second
rounding; `PTTL` provides millisecond precision. Missing and persistent keys retain the
usual `-2` and `-1` sentinel results.
Advancing the clock takes effect when the next command samples time; it does not alter
the time already sampled by an executing command.

The controllable clock affects server expiry only. It does not advance client timeouts,
schedule asynchronous work, or guarantee ordering between simultaneous callers. Use awaited
operations or batches when a test requires a specific order. The fake has no expiry
notifications or background expiry task.
Cancellation retains the real client's contract: it abandons waiting and does not undo a
command accepted for sending. Do not infer from cancellation that a mutation was absent.

Supported option behavior follows [Redis SET](https://redis.io/docs/latest/commands/set/),
[EXPIRE](https://redis.io/docs/latest/commands/expire/), and the
[Redis 8.6 expiry implementation](https://github.com/redis/redis/blob/8.6.0/src/expire.c).
Hash behavior follows [HSET](https://redis.io/docs/latest/commands/hset/),
[HGETALL](https://redis.io/docs/latest/commands/hgetall/), and
[HINCRBY](https://redis.io/docs/latest/commands/hincrby/). Set behavior follows
[SMOVE](https://redis.io/docs/latest/commands/smove/),
[SINTERSTORE](https://redis.io/docs/latest/commands/sinterstore/), and
[SINTERCARD](https://redis.io/docs/latest/commands/sintercard/).
List option and reply behavior follows [Redis LPOS](https://redis.io/docs/latest/commands/lpos/)
and the [Redis list command implementation](https://github.com/redis/redis/blob/7.2/src/t_list.c).
Sorted-set behavior follows [ZADD](https://redis.io/docs/latest/commands/zadd/),
[ZRANGE](https://redis.io/docs/latest/commands/zrange/), and the
[Redis sorted-set implementation](https://github.com/redis/redis/blob/7.2/src/t_zset.c).
Pub/sub framing and delivery follow [Redis Pub/Sub](https://redis.io/docs/latest/develop/pubsub/),
[SUBSCRIBE](https://redis.io/docs/latest/commands/subscribe/), and
[UNSUBSCRIBE](https://redis.io/docs/latest/commands/unsubscribe/).
Run real-server integration tests for version compatibility, unsupported commands, and
operational behavior. The [shared testing sample](testing-sample.md) runs identical
binary value, hash/batch, transaction, and pub/sub scenarios against the fake and real fixtures.

## Controlled faults

Install faults after connecting when the handshake is not the subject of the test.
Rules match a case-insensitive command name and, optionally, exact bytes of its first
argument. That argument is not necessarily a key. The server copies matcher bytes.
The first available matching rule wins in registration order, atomically across all
connections. `occurrences` defaults to one; use a positive count or `null` to repeat.
A fragmented request counts only once, when its complete command has been parsed.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Connections = 1 });
var gate = new RespireFakeGate();
using var fault = server.InjectFault("SET", RespireFakeFault.Pause(gate, afterExecution: true));
var pending = client.SetAsync("key", "value").AsTask();
await fault.Matched.WaitAsync(TimeSpan.FromSeconds(5));
// SET has executed, but its reply is held. No sleep or scheduler race is needed.
gate.Release();
await pending;
if (fault.ExecutionCount != 1)
    throw new InvalidOperationException("Expected exactly one handler invocation.");
```

| Action | Before execution (default) | `afterExecution: true` |
| --- | --- | --- |
| `Pause(gate)` | Hold before invoking the handler | Execute once, then hold the reply |
| `Delay(duration)` | Delay before invoking the handler | Execute once, then delay the reply |
| `Disconnect()` | Close without invoking the handler or replying | Execute once, then close without a reply |
| `Loading()`, `ReadOnly()`, `Moved(slot, destination)` | Return the named RESP error without invoking the handler | Not supported |

`Matched` completes at the selected action's boundary, so an after-execution match
proves the handler has returned. `MatchedCount` counts reserved complete commands;
`ExecutionCount` counts handler invocations, including handlers that return an error.
Neither count claims that every invocation mutated data. A rule removed before its
first boundary cancels `Matched`; always use a deadline if matching is optional.

Disposing a scope removes future matches and releases its selected delays/pauses.
`ResetFaults()` does this for all current rules, preserving data and later registrations.
Removal does not undo an executed command or reverse a selected rejection/disconnect.
A released gate stays released; create a new gate for the next pause. Delay uses wall-clock
time, not `RespireFakeClock`; gates provide deterministic scheduling without sleeps.
Close the client or server to abort held work. Server disposal cancels connections before
releasing rules, joins server loops, and treats injected EOF as expected cleanup.

Caller cancellation retains the actual client's wait-only contract. It does not cancel a
server-side pause or undo an accepted mutation. Release the gate or dispose its scope so
the reply can drain; a later command on the same connection then verifies FIFO alignment.
A disconnect after execution deliberately leaves acceptance ambiguous to the client.
Tests must inspect independent server state, not retry the mutation blindly.
For `PUBLISH`, an after-execution disconnect may deliver to subscribers while losing the
publisher's reply. Holding a command's reply also holds later pushes on that connection;
already queued pushes retain their earlier position. Faults match commands, not individual
outgoing message frames.

These actions exercise the real response parser, error propagation, command cancellation,
FIFO draining, and connection replacement. With `ReconnectPolicy`, a subsequent standalone
send can fail fast while scheduling replacement; observe `ConnectionStateChanged` before
expecting a restored connection. The client does not replay the disconnected mutation.
`LOADING` and `READONLY` are standalone server rejections here, not automatic application retries.

`MOVED` deliberately demonstrates redirect failure: a standalone client surfaces the exact
error and the command is not applied. Trying a different destination with these fake options throws
`NotSupportedException` instead of opening a real socket. No destination mapping, slot map,
Cluster discovery, ASK handling, topology refresh, Sentinel handoff, or replica election is
simulated. Use real Cluster/Sentinel integration fixtures to validate those recovery policies.
