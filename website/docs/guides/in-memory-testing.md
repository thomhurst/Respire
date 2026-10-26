---
title: In-memory testing
description: Test real Respire client code against a deterministic in-memory server, without Docker.
---

# In-memory testing

`Respire.Testing` provides a small RESP server connected through in-memory pipes. Use it for fast,
deterministic tests of application code. Your code uses a real `RespireClient`, so serialization,
dispatch, prefixing, and batches run normally. No TCP socket or Docker daemon is needed.

Keep `Respire.Testing` and `Respire` at the same package version; the test transport uses an
internal core hook.

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

## Server and client lifetime

The [RESP010 lifetime warning](../diagnostics/RESP010.md) is disabled in projects
referencing `Respire.Testing`. Test methods and fixture helpers can own isolated
clients without suppressing the warning, including multiple clients for one server.

- Each server has independent data. All clients created from its options share that data, and each command runs atomically.
- `CreateOptions()` returns fresh options each call. Use a `with` expression to set protocol (RESP2/RESP3), connection count, serializers, and timeouts.
- Do not change the endpoint: it throws `NotSupportedException` on connection. A connection string cannot represent the fake server.
- Both `ConnectAsync` and lazy `Create` clients work.
- Dispose clients before the server. Server disposal closes all connections but does not dispose your clients. Options cannot connect after server disposal.
- Malformed requests and unexpected failures close the affected connection only. Server disposal reports them, so always dispose the server, even when a test fails.

## StackExchange.Redis test fixtures

Install `Respire.Testing.StackExchangeCompat` to keep application tests that use the
supported `IConnectionMultiplexer` / `IDatabase` members and replace their connection fixture:

```bash
dotnet add package Respire.Testing.StackExchangeCompat
```

The helper shares the adapter's experimental [RSP0006](../diagnostics/RSP0006.md) opt-in.
Keep the Respire packages at matching versions.

```csharp
using Respire.Testing.StackExchangeCompat;
using StackExchange.Redis;

await using var fixture = new RespireFakeConnection();
IConnectionMultiplexer connection = fixture.Connection;
IDatabase database = connection.GetDatabase(); // Also available as fixture.Database.
await database.HashSetAsync("session", "status", "active");
var session = await database.HashGetAsync("session", "status");
```

The default fixture owns an isolated server. Pass an existing `RespireFakeServer` to share
state between fixtures or supply a `RespireFakeClock`. The fixture always owns its multiplexer
and native client; it never disposes a server passed to its constructor. Dispose every fixture
before a caller-owned server. Disposing the fixture drains pending operations, closes its clients,
then disposes its owned server. Release any paused fault gates before awaiting disposal.
Disposing `Connection` alone closes the clients; still dispose the fixture to clean up its server.

You can also compose the existing packages directly, without the helper:

```csharp
using Respire.StackExchangeCompat;
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var connection = RespireConnectionMultiplexer.Create(server.CreateOptions());
var database = connection.GetDatabase();
await database.HashSetAsync("session", "status", "active");
```

The usable surface is the **intersection** of the [adapter's supported members](stackexchange-interop.md)
and this page's fake command list, including each command's options. Examples include string
reads and integer increments, supported hash/list/set/sorted-set operations, key expiry,
supported batches and transactions, and literal-channel pub/sub. Support by either package
alone is insufficient.

- Only database zero exists. The helper rejects `GetDatabase(1)` immediately with
  `NotSupportedException`; direct `Create(options)` defers rejection until the fake receives
  `SELECT` during connection establishment.
- Unimplemented adapter members (for example `StringSet`, `StringGetLease`, and `ListLeftPop`)
  throw `NotSupportedException`, even though the fake supports their underlying commands.
- Commands the adapter can send but the fake cannot execute (for example arbitrary
  `ScriptEvaluateAsync("return 1")`) throw native `RespireServerException`. A failed command
  does not make later supported commands fail.
- No authentication, TLS, Cluster, Sentinel, persistence, eviction, modules or realistic
  network/performance behavior is simulated. The adapter's real-Redis acceptance results for
  third-party libraries do not imply those libraries run on the fake.

The [testing sample](testing-sample.md#stackexchangeredis-fixture-replacement) runs the same
`IDatabase` assertions with this helper and with a real StackExchange.Redis connection.
Arbitrary existing StackExchange.Redis suites are not guaranteed to work unchanged.

## Supported subset

The fake is a test double, not a Redis implementation or compatibility oracle.
Unsupported commands and options return a server error that names the command. Each error
uses one response slot, so later commands still work.

| Area | Supported commands and options |
| --- | --- |
| Strings | `GET`, `SET` with `NX`, `XX`, `GET`, `KEEPTTL`, `EX`, `PX`, `EXAT`, `PXAT`; `MGET`, `MSET`, `MSETNX`, `GETDEL`, `GETSET`, `GETEX`, `STRLEN`, `APPEND` |
| Integer strings | `INCR`, `DECR`, `INCRBY`, `DECRBY`, with checked signed 64-bit arithmetic |
| Extended counters | `INCREX` with integer/floating increments, bounds, `SATURATE`, expiry, and `ENX`; integer arithmetic preserves the full signed 64-bit range |
| Sparse arrays | `ARCOUNT`, `ARLEN`, `ARGET`, `ARMGET`, `ARSET`, `ARMSET`, `ARDEL`, `ARDELRANGE`, `ARGETRANGE`, `ARSCAN`, `ARSEEK`, `ARNEXT`, `ARINSERT`, `ARRING`, `ARLASTITEMS`, `ARINFO`, `AROP`, `ARGREP`; see [sparse-array testing limits](../commands/arrays.md#batches-transactions-and-the-fake-server) |
| Hashes | `HSET`, `HSETNX`, `HMSET`, `HGET`, `HMGET`, `HGETALL`, `HDEL`, `HEXISTS`, `HLEN`, `HKEYS`, `HVALS`, `HSTRLEN`, `HINCRBY`; `HIMPORT PREPARE`, `SET`, `DISCARD`, `DISCARDALL` (see [hash import](hash-import.md)) |
| Lists | `LPUSH`, `RPUSH`, `LPUSHX`, `RPUSHX`, `LPOP`/`RPOP` with optional count, `LLEN`, `LRANGE`, `LINDEX`, `LSET`, `LTRIM`, `LREM`, `LINSERT BEFORE/AFTER`, `LPOS RANK/COUNT/MAXLEN`, `LMOVEM`, `BLMOVEM` |
| Sets | `SADD`, `SREM`, `SMEMBERS`, `SCARD`, `SISMEMBER`, `SMISMEMBER`, `SMOVE`, `SINTER`, `SUNION`, `SDIFF`, their `STORE` forms; `SINTERCARD`, `SDIFFCARD`, and `SUNIONCARD` with `LIMIT` (`SUNIONCARD APPROX` returns the exact count) |
| Sorted sets | `ZADD` with `NX`, `XX`, `GT`, `LT`, `CH`, `INCR`; `ZINCRBY`, `ZREM`, `ZCARD`, `ZSCORE`, `ZMSCORE`, `ZRANK`, `ZREVRANK`, `ZCOUNT`, `ZLEXCOUNT`; `ZRANGE` with `BYSCORE`/`BYLEX`, `REV`, `LIMIT`, `WITHSCORES`; legacy `ZREVRANGE`, `ZRANGEBYSCORE`, `ZREVRANGEBYSCORE`, `ZRANGEBYLEX`, `ZREVRANGEBYLEX`; `ZPOPMIN`, `ZPOPMAX`; `ZREMRANGEBYRANK`, `ZREMRANGEBYSCORE`, `ZREMRANGEBYLEX`; `ZINTERCARD` with `LIMIT` |
| Pub/sub | `SUBSCRIBE`, `UNSUBSCRIBE`, `PUBLISH`, with binary channel names and payloads |
| Streams | `XADD` with numeric IDs or `*`, optional `NOMKSTREAM`, and manual `IDMP` with `*`; `XLEN`; `XCFGSET IDMP-DURATION/IDMP-MAXSIZE`; `XGROUP CREATE` with optional `MKSTREAM`; `XREAD`/`XREADGROUP` with `COUNT`, `MAXCOUNT`, `MAXSIZE`, `BLOCK`; `XACK`; `XPENDING` summary and ranges with `IDLE` and consumer filters; `XAUTOCLAIM` with `COUNT` and `JUSTID`; `XDEL`; `XINFO GROUPS`; `EVAL`/`EVALSHA` for the built-in [stream worker](stream-workers.md) scripts only |
| Keys | `DEL`, `UNLINK`, `EXISTS`, `TYPE`, `PERSIST`, `KEYS`, `SCAN` with `MATCH` and `COUNT` |
| Server time | `TIME`, using the supplied `TimeProvider` with microsecond precision |
| Expiry | `EXPIRE`, `PEXPIRE`, `EXPIREAT`, `PEXPIREAT` with `NX`, `XX`, `GT`, `LT`; `TTL`, `PTTL`, `EXPIRETIME`, `PEXPIRETIME` |
| Connection | `HELLO 2/3` without authentication, `PING`, `ECHO`, `SELECT 0`, `CLIENT ID`, `CLIENT GETNAME`, `CLIENT SETNAME`, `CLIENT KILL ID` (optional `SKIPME yes`) |
| Locks | `EVAL`/`EVALSHA` for Respire's built-in fenced acquisition and owner-checked lock renewal/release scripts; supports [leader-election](coordination.md#hosted-leader-election) tests |
| Transactions | `MULTI`, `EXEC`, `DISCARD`, `WATCH`, `UNWATCH` with connection-owned queues and optimistic concurrency |

### Explicitly unsupported

| Area | Unsupported |
| --- | --- |
| Key enumeration | `SCAN TYPE` |
| Scripting | General `EVAL`/`EVALSHA`, `SCRIPT LOAD`, `cjson`, and functions; see [testing script-heavy code](#testing-script-heavy-code) |
| Hashes | Hash-field expiry, `HINCRBYFLOAT`, `HRANDFIELD`, `HSCAN` |
| Sets | `SPOP`, `SRANDMEMBER`, `SSCAN` |
| Lists | `BLPOP`, `BRPOP`, `LMPOP`, `BLMPOP`, `LMOVE`, `BLMOVE`, `RPOPLPUSH`, `BRPOPLPUSH`. Use the nonblocking typed overloads without `waitFor`. |
| Sorted sets | `ZRANDMEMBER`, `ZSCAN`, `BZPOPMIN`, `BZPOPMAX`, `ZMPOP`, `BZMPOP`, `ZUNION`, `ZINTER`, `ZDIFF` and their `STORE` forms, `ZRANGESTORE`, `ZRANK`/`ZREVRANK WITHSCORE` |
| Streams | Trimming, `XCLAIM`, `XREADGROUP` `NOACK` and `CLAIM`, `IDMPAUTO`, range commands, stream metadata, other group-administration commands |
| Pub/sub | `PSUBSCRIBE`, `PUNSUBSCRIBE`, `SSUBSCRIBE`, `SUNSUBSCRIBE`, `SPUBLISH`, `PUBSUB`, `QUIT`, `RESET` |

### Strings, keys, and counters

- Binary keys and values are preserved, including empty values and zero bytes.
- Multi-key mutations are atomic. Integer parse and overflow errors leave the value unchanged.
- String commands reject other types with `WRONGTYPE`. Exceptions: `MGET` returns null for non-string keys, and plain `SET`/`MSET` replace the type.
- `UNLINK` deletes synchronously.
- `INCREX BYFLOAT` uses .NET `double`; Redis uses platform-dependent `long double`, so rounding near bounds can differ. Values outside the finite `double` range fail before mutation. Use real Redis to test floating-point precision or extreme values.

### Enumerating keys

`KEYS` and `SCAN MATCH` support byte-based, case-sensitive Redis glob patterns: `*`, `?`,
character classes such as `[a-z]` and `[^a-z]`, and backslash escaping. Both enumerate all
supported key types and exclude expired keys.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
await client.SetAsync("coordinator:job", "pending");

await foreach (var key in client.Keys.ScanAsync("coordinator:*", countHint: 10))
    await client.DeleteAsync(key);

await foreach (var key in client.Keys.ScanAsync("coordinator:*"))
    throw new InvalidOperationException($"Cleanup left key {key} behind.");
```

As with [Redis SCAN](https://redis.io/docs/latest/commands/scan/), `COUNT` is a positive work
hint, not a page-size guarantee. Raw callers start with cursor `0` and pass back the returned
cursor until it is `0` again, even after an empty page. The fake uses fixed hash buckets so
deleting returned keys does not skip surviving keys. Order, cursor values, and page boundaries
are unspecified and differ from Redis. Each call examines an in-memory key snapshot; this
does not model Redis performance. Glob matching has a shared one-million-step limit per command;
patterns that exceed it return an explicit error.

### Hashes

- Fields and values preserve binary bytes. Enumeration order is unspecified.
- `HSET` counts duplicate fields in one command once. `HMGET` keeps request order and nulls.
- `HGETALL` returns a RESP2 array or RESP3 map.
- Mutations keep the key TTL. Removing the last field deletes the key. Failed operations change nothing.

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

### Sets

- Members preserve binary bytes. `SMEMBERS` and set algebra return a RESP2 array or RESP3 set, in unspecified order.
- `SMISMEMBER` keeps input order and duplicates.
- `SMOVE` checks both types before mutating. A missing source returns zero even if the destination has another type.
- `SINTERCARD LIMIT 0` counts the whole intersection.
- Mutations keep the key TTL. Removing the last member deletes the key.
- `STORE` forms read all sources first, so the destination can also be a source. They replace the destination type, clear its TTL, and delete it when the result is empty.

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

### Lists

- Lists keep binary elements, duplicates, order, and key TTL. Removing the last element deletes the key.
- Negative indexes count from the tail. `LRANGE` and `LTRIM` end indexes are inclusive.
- `LREM` count: positive from the head, negative from the tail, zero for all matches.
- `LPOS` supports negative ranks, `COUNT 0` (unlimited), and `MAXLEN`. Without `COUNT`, no match returns null; with it, an array. `RANK` must be nonzero and within `long.MaxValue` (Redis 7.2+).
- Repeated options use the last value, but each is validated: `RANK 0 RANK 1` fails.
- Count-pop returns null for a missing key and an empty array for count zero.
- `LMOVEM` and `BLMOVEM` support `COUNT`/`EXACTLY` and `OBO`/`BULK`. `BLMOVEM` timeouts use wall-clock time; cancellation or server disposal releases the wait. Inside `MULTI` it runs immediately, as in Redis.

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

### Sorted sets

- Members preserve binary bytes. Equal scores sort by unsigned member bytes.
- Rank ranges are inclusive and accept negative indexes. Score and lex ranges accept exclusive and infinite bounds. Lex ranges need equal scores to match Redis.
- `REV` reverses score and tie order; bounds are then given maximum first.
- `LIMIT` applies to score/lex ranges. A negative offset returns nothing; a negative count returns all remaining. Repeated `LIMIT` uses the last value.
- `WITHSCORES` cannot combine with `BYLEX`.
- `ZINTERCARD` accepts sets and sorted sets and treats `LIMIT 0` as unlimited.
- Scores accept decimal, exponent, hexadecimal, subnormal, and infinite values. NaN, malformed numbers, and overflow or underflow to zero fail before mutation. An increment that produces NaN fails.
- `ZADD CH` counts additions and score changes.
- Mutations keep expiry. Removing the last member deletes the key.
- Missing scores and ranks return null; missing ranges and pops return empty arrays. In RESP3, scored ranges and counted pops return nested member/score pairs; a pop without a count stays flat.
- Sorting runs on demand. It does not model Redis performance.

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

### Streams

- Reads support multiple keys, new group entries, pending history, acknowledgements, and Redis 8.10 count/byte budgets.
- Blocking reads wake after appends and resolve `$` once. Their timeouts use wall-clock time, not `RespireFakeClock`. Inside a transaction, reads do not block.
- Manual deduplication (`IDMP`) keeps identities up to the configured duration and size. Changing a setting clears them; setting the same value keeps them. Identities expire on access using `RespireFakeClock`; Redis cleans up periodically, so it can expire them later.
- `XCFGSET` does not invalidate `WATCH`. Configuration and appends keep key TTLs, entries, groups, and pending entries.

### Pub/sub

- Subscriptions belong to their connection. `PUBLISH` returns the number of subscribed connections, not client-side readers.
- Duplicate `SUBSCRIBE` acknowledges each argument but counts each channel once. `UNSUBSCRIBE` with no arguments removes all subscriptions.
- Messages use RESP2 arrays or RESP3 pushes. In RESP2 subscribed mode only `SUBSCRIBE`, `UNSUBSCRIBE`, and `PING` are allowed; RESP3 allows all commands.
- A RESP3 connection publishing to its own channel gets the full reply (including a full `EXEC` array) before the message, as in Redis 7.2+.

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

Delivery rules:

- Publishing never waits for a subscriber to read.
- A subscriber whose pending output exceeds 16 MiB is disconnected. This is a fixed limit, not Redis's output-buffer policy. The publication that exceeds it still counts that subscriber.
- A receiver count does not guarantee delivery; the connection can close after being counted.
- The real client reconnects and resubscribes with its normal gap marker. Messages published during the gap are lost.
- Cancelling the caller does not undo an accepted subscription. Dispose the subscription or client to clean up.

## Transactions and WATCH

Use `CreateTransaction()` or `CreateTransactionAsync(watchedKeys)` so all transaction commands use one
connection. Queued commands run atomically at `EXEC`, in order. A failed command returns its error in its
own result slot and does not roll back the others.

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

- `WATCH` detects supported mutations and expiry, including expiry found at `EXEC`. Rejected conditional writes and no-ops do not invalidate it. `SET`, `HSET`, `LSET`, and an unchanged `LTRIM` do, as in Redis.
- A watched key that has already expired counts as absent.
- `UNWATCH`, `DISCARD`, `EXEC`, and connection close release watches. `UNWATCH` inside `MULTI` is queued.
- A changed watch aborts the commit, which returns `false`.
- Unknown commands, arity errors, `HELLO`, `SUBSCRIBE`, and `UNSUBSCRIBE` inside `MULTI` invalidate the queue; `EXEC` then returns `EXECABORT`. Option and value errors stay queued and fail individually at `EXEC`.
- Nested `MULTI` and `WATCH` inside `MULTI` fail without invalidating the queue.
- Each connection can queue at most 16 MiB of arguments; exceeding it invalidates the queue.
- A disconnect after `EXEC` loses the reply but keeps all mutations. The client never replays an ambiguous transaction, and cancellation does not undo one.

Behavior follows [Redis transactions](https://redis.io/docs/latest/develop/using-commands/transactions/).

## Expiry and determinism

- Without an injected clock, expiry and `TIME` use `TimeProvider.System`.
- `RespireFakeClock` starts at the Unix epoch and moves only when you call `Advance`. Each command reads the time once; keys expire on access at or after their deadline.
- `TTL` rounds to the nearest second; `PTTL` is in milliseconds. Missing and persistent keys return `-2` and `-1`.
- `Server.TimeAsync()` uses the same injected clock as expiry. `TIME` returns Unix seconds and the microsecond fraction, truncating sub-microsecond ticks. Commands within `EXEC` share its execution-time sample.
- The clock controls server time, expiry, and pending stream idle times. It does not advance client timeouts or order concurrent callers; await operations or use batches when order matters.
- There are no expiry notifications or background expiry.
- Cancellation only stops waiting. Do not assume a cancelled mutation did not run.

## Remaining limits

- Only database 0 and standalone mode.
- Not simulated: authentication, TLS, Cluster, Sentinel, general scripts and functions (only the built-in stream worker and lock scripts), client-side tracking, persistence, and admin diagnostics. Unsupported handshake features fail initialization.
- Requests are limited to 16 MiB; larger requests close the connection.
- No eviction or memory limit: keep test data small.
- Not a throughput benchmark.

Run real-server integration tests for version compatibility, unsupported commands, and operational behavior.
The [shared testing sample](testing-sample.md) runs the same scenarios against the fake and real servers.

## Testing script-heavy code

The fake recognizes only Respire's built-in stream worker and lock scripts. It does not embed a Lua
interpreter: arbitrary `RespireScript` execution, `SCRIPT LOAD`, `redis.call`/`redis.pcall`, and
`cjson` require a real server. An embedded interpreter alone would not reproduce Redis's Lua
environment, conversion rules, and command behavior.

Use `Respire.Testing` for storage and fault-handling tests that stay within its supported
commands. Run coordinator contract tests (claims, lease renewal/requeueing, and atomic result
publication) with the production scripts against a [container fixture](testing-containers.md):

```csharp
using Respire.Testing.Containers;

await using var fixture = await RespireContainerFixture.StartAsync();
await using var client = await RespireClient.ConnectAsync(fixture.CreateOptions());
var script = RespireScript.Create("return redis.call('GET', KEYS[1])");
await client.SetAsync("coordinator:result", "done");
var result = await client.Scripts.ExecuteStringAsync(script, ["coordinator:result"]);
if (result != "done") throw new InvalidOperationException("Script result did not match.");
```

Pass this client into the same application code exercised by the fake tests. Pin the fixture's
Redis or Valkey image to your deployed version, isolate each test's keys (or use one fixture per
test), and dispose clients before the fixture. Container server time follows its real clock;
`RespireFakeClock` cannot advance Lua `TIME` or expiry there. Keep Docker-enabled contract tests
in a separate test project or CI job so they run alongside fast fake tests instead of remaining
silently skipped.

## Command expectations

Use expectations when a test needs to verify the exact commands your application sends or return a
configured response. They run through the real client transport, so typed facets, raw commands, batches,
and transactions share the same matching rules. Arguments are the **serialized wire bytes**, including
key prefixes and options, rather than the original application objects.

```csharp
using Respire.Testing;

await using var server = new RespireFakeServer();
await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
server.ExpectationMode = RespireFakeExpectationMode.Strict;
server.Expect("SET", ["user:42", RespireFakeArgument.Any]);
server.Expect("GET", ["user:42"], occurrences: 2,
    response: RespireFakeResponse.Bulk("configured value"));

await client.SetAsync("user:42", "stored value");
var first = await client.GetStringAsync("user:42");
var second = await client.GetStringAsync("user:42");
server.VerifyAllExpectations();
```

`Expect` enables loose matching if recording was disabled. Enable expectations **after connecting** to
exclude initialization commands. When enabled before connection or during reconnect, handshake commands
are recorded and must also have expectations in strict mode. There are no implicit command exemptions.

| Mode | Unmatched command | Verification |
| --- | --- | --- |
| `Disabled` (default) | Normal fake behavior; no history | Existing counts and history remain available |
| `Loose` | Normal fake behavior | Checks every configured count; unrelated calls are allowed |
| `Strict` | Returns `ERR Unexpected command` without executing | Also fails for every strict rejection |

- Names ignore case; arguments match exact, case-sensitive bytes and the complete argument count.
  Strings convert to UTF-8 matchers. Use `RespireFakeArgument.Exact(binaryMemory)` for binary arguments.
  `Any` matches **one** argument, including empty bytes; it does not match a variable number of arguments.
- Counts must be positive and are exact. The first matching expectation with occurrences left wins, in
  registration order. Once all matching expectations are exhausted, further matching calls fail verification
  even in loose mode (and run normal fake behavior there). An unmatched argument list does not consume a rule.
- Expectations impose no call ordering. Matching, counts, and history order are atomic across connections;
  concurrent callers have no predetermined order. Await calls or use one batch when order matters.
- `VerifyAllExpectations()` throws `InvalidOperationException` with expected/received counts and the command
  history, including mismatched arguments and unexpected commands. Await your callers first: verification
  observes receipt, not successful execution, and does not wait. Disposal does not verify automatically.
- `CommandHistory` returns an owned snapshot with `Sequence`, `ConnectionId`, `Command`, `Arguments`
  (excluding the name), and `Matched`. Diagnostics escape binary bytes. History can contain sensitive keys
  and values and grows until reset; keep test workloads small.
- `ResetExpectations()` clears rules and history and disables recording. It keeps server data, existing
  faults, and response overrides already queued in transactions.

### Reply overrides and faults

Omit `response` to verify a command while using its normal fake handler. Supply `RespireFakeResponse.Simple`,
`Bulk`, `Integer`, `Array`, `Null`, or `Error` to replace execution entirely: an overridden write does **not**
change stored data or invalidate watches. Arrays can contain nested replies and errors. Choose the RESP
shape expected by the client API. Overrides can supply replies for commands the fake does not implement;
this tests your application's interaction, not that command's Redis behavior.

Connection, subscription, and transaction control (`HELLO`, `CLIENT`, `SUBSCRIBE`, `UNSUBSCRIBE`, `MULTI`,
`EXEC`, `DISCARD`, `WATCH`, `UNWATCH`) can be observed but cannot have reply overrides. Overrides are also
rejected while a connection is in RESP2 subscribed mode, except `PING`. A subscribed RESP2 `PING`
override must use its normal array shape (for example, `Array(Bulk("pong"), Bulk("payload"))`).

Commands are observed before fault selection. A strict rejection wins without consuming a fault rule.
For accepted commands, existing `InjectFault` rules still apply: a before-execution error or disconnect
wins over a configured response; delays and pauses surround normal execution or the override. Counts
remain consumed even when a fault prevents execution. Use the existing fault API for transport timing and
disconnect scenarios; `RespireFakeResponse.Error("ERR rejected")` supports complete-argument error matching.

### Transactions and batches

Every received command is counted once, including `MULTI`, each queued command, and `EXEC` or `DISCARD`.
Internal execution at `EXEC` does not match again or add history. Register control-command expectations
when using strict mode. Batches use the same rule: one receipt per command, preserving wire order within
their connection.

A queued reply override returns `QUEUED` initially and replaces its result slot at `EXEC`. An overridden
error is an execution error in that slot and does not roll back other commands. A strict unexpected
command instead rejects queueing and invalidates the transaction, so `EXEC` fails with `EXECABORT`.
`DISCARD`, a failed watch, or disconnection do not undo receipt counts; verification alone does not prove
a transaction committed. Assert `CommitAsync()` and its results separately. Existing fault rules continue
to act on receipt/queueing and on `EXEC`, never on its internal command traversal.

### Choosing a test tool

| Tool | Use it for |
| --- | --- |
| Fake behavior without expectations | Stateful application tests using supported Redis commands |
| `InjectFault` | Timing, disconnects, and command/first-argument rejection rules |
| Expectations | Complete-argument and count assertions, strict interaction tests, configured replies |
| [Containers](testing-containers.md) | Redis/Valkey compatibility, real scripts, unsupported operations, and operational behavior |

## Controlled faults

Inject faults to test how your code handles paused, delayed, disconnected, or rejected commands.
Install them after connecting unless you are testing the handshake.

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

Matching:

- A rule matches a command name (case-insensitive) and, optionally, the exact bytes of the first argument (not necessarily a key).
- The first matching rule in registration order wins, across all connections.
- `occurrences` defaults to 1. Pass a positive count, or `null` to match forever.

Fault scope members:

| Member | Meaning |
| --- | --- |
| `Matched` | Completes when the action is reached. After execution, this proves the handler returned. Cancelled if the rule is removed first, so always use a deadline. |
| `MatchedCount` | Matched commands. |
| `ExecutionCount` | Handler invocations, including ones that returned an error. Does not prove data changed. |
| `Dispose()` | Stops future matches and releases its pauses and delays. |

- `server.ResetFaults()` removes all current rules and keeps data. Removing a rule does not undo an executed command.
- A released gate stays released; create a new one for the next pause.
- `Delay` uses wall-clock time, not `RespireFakeClock`.
- A held reply also holds later pushes on that connection.
- Cancelling the caller does not cancel a server-side pause or undo a mutation. Release the gate or dispose the scope.
- After an after-execution disconnect, the client cannot tell whether the command ran. Check server state; do not retry blindly. For `PUBLISH`, subscribers may receive the message even though the reply is lost.
- With `ReconnectPolicy`, the next send can fail fast while reconnecting. Wait for `ConnectionStateChanged` before expecting a restored connection. The client does not replay the lost command.
- `LOADING` and `READONLY` are plain errors here; the client does not retry them.
- `MOVED` shows redirect failure only: the client surfaces the error and the command is not applied. Following the redirect throws `NotSupportedException`. No slot map, Cluster, `ASK`, or Sentinel behavior is simulated; use real fixtures for those.
