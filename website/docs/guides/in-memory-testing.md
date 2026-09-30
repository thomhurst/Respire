---
title: In-memory testing
description: Exercise real Respire client code against a deterministic strings, keys, hashes, and sets server without Docker.
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

This is a test double with explicit limits, not a Redis implementation or a compatibility
oracle. Unsupported commands and options return server errors containing the command name.
An error consumes exactly one response slot, so later valid commands still work.

| Area | Supported commands and options |
| --- | --- |
| Strings | `GET`, `SET` with `NX`, `XX`, `GET`, `KEEPTTL`, `EX`, `PX`, `EXAT`, `PXAT`; `MGET`, `MSET`, `MSETNX`, `GETDEL`, `GETSET`, `GETEX`, `STRLEN`, `APPEND` |
| Integer strings | `INCR`, `DECR`, `INCRBY`, `DECRBY`, with checked signed 64-bit arithmetic |
| Hashes | `HSET`, `HSETNX`, `HMSET`, `HGET`, `HMGET`, `HGETALL`, `HDEL`, `HEXISTS`, `HLEN`, `HKEYS`, `HVALS`, `HSTRLEN`, `HINCRBY` |
| Sets | `SADD`, `SREM`, `SMEMBERS`, `SCARD`, `SISMEMBER`, `SMISMEMBER`, `SMOVE`, `SINTER`, `SUNION`, `SDIFF`, their `STORE` forms, and `SINTERCARD` with `LIMIT` |
| Keys | `DEL`, `UNLINK`, `EXISTS`, `TYPE`, `PERSIST` |
| Expiry | `EXPIRE`, `PEXPIRE`, `EXPIREAT`, `PEXPIREAT` with `NX`, `XX`, `GT`, `LT`; `TTL`, `PTTL`, `EXPIRETIME`, `PEXPIRETIME` |
| Connection | `HELLO 2/3` without authentication, `PING`, `ECHO`, `SELECT 0`, `CLIENT ID`, `CLIENT GETNAME`, `CLIENT SETNAME` |

`GETEX` supports its expiry options and `PERSIST`. Binary keys and values are preserved,
including empty values and embedded zero bytes. Multi-key mutations are atomic; integer
parsing and overflow errors leave the original value unchanged. `UNLINK` removes data
synchronously because this fake does not model background memory reclamation.

Hash fields and values preserve binary bytes. `HSET` counts newly added fields, including
duplicate fields within one command only once; `HMGET` preserves requested field order and
nulls. `HGETALL` emits a RESP2 array or RESP3 map. Hash enumeration order is unspecified.
`HINCRBY` uses checked signed 64-bit arithmetic. Hash mutations retain key-level TTL; removing
the last field deletes the key and its TTL. String operations reject hashes and sets with `WRONGTYPE`,
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

Only database zero and standalone operation are supported. Authentication, TLS, Cluster,
Sentinel, scripts/functions, client-side tracking, lists, sorted sets, pub/sub, transactions,
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
Run real-server integration tests for version compatibility, unsupported commands, and
operational behavior. Remaining collections and pub/sub/transactions remain tracked in
[#540](https://github.com/thomhurst/Respire/issues/540) and
[#541](https://github.com/thomhurst/Respire/issues/541).

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
