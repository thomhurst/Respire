---
title: In-memory testing
description: Exercise real Respire client code against a deterministic strings and keys server without Docker.
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
Unexpected command/clock failures close the affected connection and are reported again by
server disposal. Failed connections remain retained until disposal so their errors cannot
be lost after a client disconnects. Keep fixture lifetimes bounded and dispose the server
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
| Keys | `DEL`, `UNLINK`, `EXISTS`, `TYPE`, `PERSIST` |
| Expiry | `EXPIRE`, `PEXPIRE`, `EXPIREAT`, `PEXPIREAT` with `NX`, `XX`, `GT`, `LT`; `TTL`, `PTTL`, `EXPIRETIME`, `PEXPIRETIME` |
| Connection | `HELLO 2/3` without authentication, `PING`, `ECHO`, `SELECT 0`, `CLIENT ID`, `CLIENT GETNAME`, `CLIENT SETNAME` |

`GETEX` supports its expiry options and `PERSIST`. Binary keys and values are preserved,
including empty values and embedded zero bytes. Multi-key mutations are atomic; integer
parsing and overflow errors leave the original value unchanged. `UNLINK` removes data
synchronously because this fake does not model background memory reclamation.

Only database zero and standalone operation are supported. Authentication, TLS, Cluster,
Sentinel, scripts/functions, client-side tracking, collection commands, pub/sub, transactions,
persistence and administrative diagnostics are not simulated. Unsupported handshake features
fail initialization. Do not enable these modes and infer production behavior from the fake.
Individual RESP requests are limited to 16 MiB; larger requests close their connection
and report the size-limit error when the server is disposed.
Arguments are copied into owned arrays and commands execute under one server lock.
This fixture is not a throughput benchmark tool.
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
notifications, background expiry task, or injected transport faults in this foundation.
Cancellation retains the real client's contract: it abandons waiting and does not undo a
command accepted for sending. Do not infer from cancellation that a mutation was absent.

Supported option behavior follows [Redis SET](https://redis.io/docs/latest/commands/set/),
[EXPIRE](https://redis.io/docs/latest/commands/expire/), and the
[Redis 8.6 expiry implementation](https://github.com/redis/redis/blob/8.6.0/src/expire.c).
Run real-server integration tests for version compatibility, unsupported commands, and
operational behavior. Collections, pub/sub/transactions, and deterministic fault injection
remain tracked in [#540](https://github.com/thomhurst/Respire/issues/540),
[#541](https://github.com/thomhurst/Respire/issues/541), and
[#542](https://github.com/thomhurst/Respire/issues/542).
