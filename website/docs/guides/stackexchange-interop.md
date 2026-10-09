---
title: Incremental StackExchange.Redis migration
description: Reuse StackExchange.Redis values and run the official distributed cache and DataProtection integrations on Respire.
---

# Incremental StackExchange.Redis migration

Prefer Respire's native typed APIs for normal application code. Install
`Respire.StackExchangeCompat` when migrating code that still passes `RedisKey`,
`RedisValue`, or consumes `RedisResult`. The package uses the repository's pinned
StackExchange.Redis version, currently 3.4.0, and targets .NET 8 and .NET 10.

```csharp
using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
RedisKey key = "customer:42";
RedisValue value = new byte[] { 0xff, 0, 0x80 };
CancellationToken cancellationToken = default;

await client.Strings.SetAsync(key.ToRespireKey(), value.ToRespireValue());
RedisResult reply = await client.ExecuteStackExchangeAsync(
    RespireCommands.String.GET, [(byte[]?)key!],
    cancellationToken: cancellationToken);
byte[]? bytes = (byte[]?)reply;
```

The bridge accepts an `IRespireClient`, a native `RespireCommand`, and
`ReadOnlySpan<RedisValue>` arguments. It forwards `RespireCommandFlags` and
`CancellationToken` unchanged. Routing, key-prefix handling, blocking commands,
and server exceptions follow native `ExecuteAsync` semantics. Catalog descriptors
retain catalog routing metadata; a caller-supplied command string retains the
native raw-command behavior. This does not translate StackExchange.Redis
`CommandFlags` or emulate its database API.

Native key-prefixed views can reject raw/catalog commands whose layouts are not
prefixable. Use typed facets with `ToRespireKey`/`ToRespireValue` for those calls;
the bridge preserves the rejection rather than applying additional rewriting.

## Arguments and buffer ownership

`ToRespireKey` rejects null keys and preserves empty keys. `ToRespireValue`
preserves null versus empty values. The bridge rejects null arguments before
I/O because Redis command arguments cannot be null.

Conversions use StackExchange.Redis's byte representation. Binary keys and
values are never decoded as UTF-8. Integers, unsigned integers, doubles, booleans,
strings, memory slices, and sequences retain StackExchange.Redis's wire bytes
without conversion through floating point or the current culture.

Binary storage can be borrowed from the caller. Do not mutate or release the
underlying array, memory, or sequence until the native command completes. The
bridge creates its own argument array; changing the caller's `RedisValue` array
after invocation does not change that array, but its binary buffers remain
subject to the same lifetime requirement. Copy binary buffers first when an
independent snapshot is needed.

## Supported replies

`ToStackExchangeResult` copies supported replies into standalone `RedisResult`
instances. The copy remains valid after the native root is disposed, including
nested aggregates and binary payloads.

| Native reply | `RedisResult.Resp3Type` |
| --- | --- |
| Simple string, bulk string | `SimpleString`, `BulkString` |
| Integer, boolean, double, big number | `Integer`, `Boolean`, `Double`, `BigInteger` |
| Array, map, set | `Array`, `Map`, `Set` |
| RESP2 null bulk string, null array; RESP3 null | `Null` |

Null bulk strings retain `Resp2Type.BulkString`; null arrays retain
`Resp2Type.Array`. RESP3 null uses the factory's scalar null identity. Empty
strings and empty aggregates remain non-null, and map pairs remain flattened
key/value elements. `RespireResult.NullWireType` exposes the original null
framing while `Type` remains `RespDataType.Null` for every null reply.

Error elements, bulk errors, verbatim strings, push replies, attributes, and
other unsupported shapes are rejected with `NotSupportedException` and native
API guidance when presented to the converter. Public
[`RedisResult` factories](https://github.com/StackExchange/StackExchange.Redis/blob/3.4.0/src/StackExchange.Redis/RedisResult.cs)
cannot faithfully construct their full semantics. Errors are never converted
into successful values. Native connection handling consumes attributes and
routes push messages before command results reach this boundary; this bridge
does not expose those out-of-band messages. Top-level server errors keep the
native `RespireServerException` behavior.

The direct converter leaves the caller responsible for disposing the native
root. `ExecuteStackExchangeAsync` disposes its root after conversion, including
conversion failures. It never disposes the caller's client.

## Official distributed cache and DataProtection adapter

`RespireConnectionMultiplexer` implements the limited `IConnectionMultiplexer`,
`IDatabase`, `IDatabaseAsync`, and `IBatch` surface required by
`Microsoft.Extensions.Caching.StackExchangeRedis` and
`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`. Acceptance tests use
the pinned 10.0.12 packages on both .NET 8 and .NET 10, with StackExchange.Redis
3.4.0 and RESP2/RESP3. Prefer the native `Respire.Caching` and
`Respire.DataProtection` packages when their interfaces fit your application.

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

// Native configuration is validated and snapshotted. Connection establishment is lazy.
await using var connection = RespireConnectionMultiplexer.Create(new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    Database = 2,
    Connections = 1,
});

var services = new ServiceCollection();
services.AddStackExchangeRedisCache(options =>
{
    options.InstanceName = "app:";
    options.ConnectionMultiplexerFactory = () =>
        Task.FromResult<IConnectionMultiplexer>(connection);
});
services.AddDataProtection().PersistKeysToStackExchangeRedis(connection, "app:keys");
await using var provider = services.BuildServiceProvider();
```

The cache closes its factory-provided multiplexer when disposed. Avoid sharing
that adapter with consumers that must outlive the cache. To retain ownership of
an existing native client, use `RespireConnectionMultiplexer.Wrap(client)`.
The wrapper reads the client's actual database and configuration; it never
disposes that client unless `ownsClient: true` is specified.

`Create` owns one lazy native client per selected database. `GetDatabase()` uses
the configured default, and `GetDatabase(n)` creates an isolated client for `n`
without sending connection-scoped `SELECT` commands on a shared connection.
`Wrap` accepts only its client's actual database and explicitly rejects other
selections. Native database restrictions, including server and Cluster support,
still apply. `Configuration` identifies the default database without exposing
credentials; it is not a reconnectable connection string.

### Supported commands

Both synchronous and asynchronous variants are supported:

| Member | Supported overloads and behavior |
| --- | --- |
| `HashGet` | One field or an array of fields; missing values remain null |
| `HashGetLease` | Independent copied lease; null for missing fields, empty lease for empty values; caller disposes the lease |
| `HashSet` | Entry array, or a field/value with `When.Always` or `When.NotExists`; null field values in the scalar overload delete the field |
| `HashGetAll`, `HashLength` | Binary field/value entries and original field count; empty array/zero for a missing hash |
| `HashDelete` | One field or an array of fields; original deletion boolean/count; empty array is a no-op |
| `KeyExpire` | Relative `TimeSpan?`, absolute local/UTC `DateTime?`, and `ExpireWhen` conditions; null and maximum-value sentinels persist unconditionally |
| `KeyDelete` | One key or an array of keys; original deletion boolean/count |
| `KeyExists`, `KeyPersist`, `KeyTimeToLive` | Existence boolean (or array count), original persistence outcome, and millisecond TTL; TTL is null for missing or persistent keys |
| `StringGet` | One key or an array; binary values and nulls in original key order, including repeated keys |
| `StringIncrement`, `StringDecrement` | Signed 64-bit integer overloads; Redis integer parsing, overflow errors, and original counter result |
| `SetAdd`, `SetRemove` | One binary member or an array; original addition/removal boolean or count |
| `SetMembers`, `SetLength` | Binary members and original cardinality; member order follows Redis and is not guaranteed |
| `SortedSetAdd`, `SortedSetRemove` | One binary member or an entry/member array; unconditional additions and original addition/removal boolean or count; `When.Always`/`SortedSetWhen.Always` only |
| `SortedSetLength` | Inclusive or exclusive minimum/maximum score bounds, including infinities |
| `SortedSetRangeByRank`, `SortedSetRangeByRankWithScores` | Inclusive signed indexes and ascending/descending order, with original binary members and optional scores |
| `SortedSetRangeByScore`, `SortedSetRangeByScoreWithScores` | Minimum/maximum score bounds, exclusion flags, order, skip/take, and optional scores; `take = -1` means unlimited |
| `SortedSetScan` | Lazy `ZSCAN` with binary patterns, page size, cursor, page offset, and asynchronous enumeration; see scan contracts below |
| `ListRange` | Start/stop indexes, including Redis negative indexes |
| `ListLeftPush`, `ListRightPush` | Scalar or array with `When.Always`/`When.Exists`; original list length, including an empty-array length query |
| `ListLength`, `ListGetByIndex` | Original length and binary indexed value; negative indexes supported, null for a missing index |
| `ListRemove`, `ListTrim` | Signed removal count and inclusive start/stop indexes, including negative indexes; Redis removal count and trimming behavior |
| `ListRightPopLeftPush` | Atomic move between lists; original binary value or null when the source is empty |
| `Wait`, `WaitAll`, `TryWait` | Wait helpers used by the official cache; synchronous timeout follows native `CommandTimeout` |

Individual database commands require native `Connections = 1` (the default).
They reject a multi-connection configuration before dispatch, because independent
pool connections can reorder a hash write and its expiry. They initialize and
enqueue commands in caller order, then await replies independently. Settings
are never silently overridden. Native reconnect and failover semantics still
apply; a failed write is not replayed by the adapter.

Keys, fields, and values are copied to their exact StackExchange.Redis wire
bytes at invocation/queue time. Mutating the original buffers afterward does
not change the queued command. Replies and leases remain independent of native
result storage. Null keys and null wire arguments are rejected; empty binary
keys/values are preserved. Server booleans, counts, nulls, and exceptions are
not replaced with successful default values.

Supported individual flags are `None`, `NoRedirect`, `DemandMaster`,
`PreferReplica`, `DemandReplica`, and `FireAndForget`. Replica flags apply to
read commands using configured native replica/Cluster/Sentinel routing.
Fire-and-forget returns StackExchange.Redis's default result and discards
ordinary server errors using native fire-and-forget behavior; native Cluster
redirect failures can still surface. Combining `FireAndForget` with
`NoRedirect` is rejected. Other flags, including retry categories, are rejected
with native API guidance. Exceptions retain native Respire types; they are not
translated into StackExchange.Redis exception types.
Zero integer increment/decrement operations in fire-and-forget mode are no-ops,
matching StackExchange.Redis without creating a missing counter.

### Batch and lifetime behavior

`CreateBatch()` supports the listed hash/list/key/string/set/sorted-set asynchronous
methods and `KeyExpireAsync` with `None`/`DemandMaster` flags. No queued command executes
before `Execute()`; empty hash field, key, member, and sorted-set entry arrays
remain immediate no-ops. Execution
uses a native `RespireBatch`, so commands for the same key share an ordered
pipeline even when a wrapped client has multiple connections. Each call to
`Execute()` takes the current queue; later calls can send newly queued work,
and an empty call does not replay earlier work. Execution is not transactional.
Different Cluster slot groups can execute independently, following native batch
rules. Every queued task observes its own result/error/cancellation; one failed
command does not hide another command's result.

`SortedSetScan` and `SortedSetScanAsync` expose `IScanningCursor` on both the
enumerable and its enumerator. `Cursor` identifies the server page containing
the current entry, and `PageOffset` identifies that entry within the page.
Resume with both values to include the current entry again, or increment the
offset to continue after it. A scan copies its key/pattern when created and
fetches pages only during enumeration. `pageSize` is a positive Redis `COUNT`
hint, not a limit on returned entries. Null, empty, and `*` patterns match all
members. Empty pages do not end a scan unless
Redis returns cursor zero. Results follow Redis scan semantics: no ordering or
snapshot guarantee, and mutations can cause repeats or omissions. Async
enumeration accepts cancellation through `GetAsyncEnumerator`/`WithCancellation`.
Fire-and-forget scans are rejected. A batch scan queues one page when its
enumerator needs it; call `Execute()` for each pending page before awaiting
that move. Buffered entries do not require another execution.
Filtered scans can cross several empty pages during a single move, requiring
several executions before that move completes. Use an ordinary database scan
when the caller cannot drive those deferred pages.

`Close`/`Dispose` and their asynchronous variants reject new work and cancel
unexecuted queued tasks. The default close drains started commands before
disposing owned clients. `Close(false)` cancels started commands, observes their
completion, and disposes owned clients. Closing is idempotent. Borrowed clients
remain usable. Disposing a native view retains that view's native ownership
behavior; use the owning root client when transferring ownership.

Profiling registration, library-name suffixes, events, unlisted subscriber/server APIs,
transactions, non-null `asyncState`, and all unlisted commands are explicitly
unsupported. Unsupported members throw `NotSupportedException` with native
API guidance rather than returning fabricated success. This adapter does not
provide general StackExchange.Redis parity. SignalR and Hangfire acceptance
remain pending under [#889](https://github.com/thomhurst/Respire/issues/889).
Floating-point and bounded/expiring string increment overloads, conditional
sorted-set additions, and lexicographic sorted-set ranges remain unsupported.

### Server discovery, locks, and storage subscriptions

`GetEndPoints(true)` returns configured endpoints (including configured standalone
replicas). `GetEndPoints(false)` connects and returns the actual standalone or
Sentinel primary and replicas, or discovered Cluster nodes including replicas.
`IdentifyEndpoint[Async]` returns the selected primary for the supplied key's slot;
without a key it returns the native connection selected for a keyless command.
It supports `None`, `DemandMaster`, and `NoRedirect`; replica selection and use on
`IBatch` are explicitly unsupported. DNS, IP, and Unix socket endpoint identities
are retained. Discovery reports live topology rather than inventing a localhost
endpoint. `Configuration` contains parseable endpoints and the default database,
without credentials; it is a diagnostic string, not a complete connection recipe.

`GetServer` overloads and `GetServers` return cached handles for physical endpoints.
Each handle owns an independent native client with the adapter's authentication,
TLS, protocol, and database settings. Server calls never redirect or fail over to
another endpoint. `IsConnected` establishes that handle's connection and reports
connection failures; `IsReplica` queries Redis `ROLE`. `InfoRaw[Async]` returns
Redis `INFO` text with an optional section; `Time[Async]` returns Redis `TIME` as
a UTC `DateTime`. These server commands support only `CommandFlags.None`.
Closing the adapter drains/cancels admitted calls, disposes its server clients,
and rejects subsequent calls on retained handles. A caller-owned wrapped client
remains usable.

`LockTake[Async]` sends atomic `SET key token PX milliseconds NX`.
`LockExtend[Async]` and `LockRelease[Async]` use Lua to compare the original binary
token and perform `PEXPIRE` or `DEL` atomically. No token substitution or managed
lock handle is involved. A missing key or different owner returns `false`;
an expired holder cannot extend or delete a replacement lock. Expiry must be
positive, is truncated to milliseconds, and has a one-millisecond minimum.
The individual database flag rules above apply, including rejection of replica
writes. `FireAndForget` returns the default `false` after admission and discards
the server reply. Lock operations on `IBatch` remain unsupported.
Lua errors preserve native `RespireServerException` and the server message;
there are no direct `ScriptEvaluate` call sites in the pinned Hangfire source,
and general script evaluation remains explicitly unsupported.

`GetSubscriber()` supports literal, binary channel callback
`Subscribe[Async]`, `Unsubscribe[Async]`, and `UnsubscribeAll[Async]` with `None`.
Handlers for the same channel share a native subscription; duplicate handlers
are ignored. Channel buffers are copied at the adapter boundary.
Unsubscribing a handler retains other handlers. Native reconnect
and resubscription behavior applies; delivery gaps cannot replay messages.
If the native reconnect limit is exhausted, subscribing again propagates the
native reconnect-limit exception; recreate the client and adapter to resume delivery.
Callbacks execute separately from subscription admission, and callback exceptions
do not terminate delivery. `ChannelMessageQueue`, patterns, and all other
subscriber calls remain unsupported. `Publish[Async]` is available on the
subscriber and database with `None` or `DemandMaster`; batches and other flags
are rejected. Subscription and publication both apply the native pub/sub prefix.
Adapter close removes only its subscriptions, even when borrowing a client.

### Pinned source inventory and validation

The hash/list and key/string/set/sorted-set facets were inventoried against `Hangfire.Redis.StackExchange`
1.12.0 at the NuGet package's repository commit
[`da8e39a33df204900afc30aeb65110f76f081c55`](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55).
Its `RedisConnection`, `RedisFetchedJob`, `RedisMonitoringApi`,
`RedisWriteDirectlyToDatabase` and `RedisWriteOnlyTransaction` use hash
deletion/count/entry reads and list pushes, lengths, indexed reads, removal,
trimming and atomic moves. Focused tests exercise these contracts against
real Redis with RESP2/RESP3 on .NET 8 and .NET 10, including binary snapshots,
database isolation, deferred batch reads/writes, server errors and shutdown.
`RedisConnection`, `RedisMonitoringApi`, `RedisWriteDirectlyToDatabase`, and
`ExpiredJobsWatcher` also use key existence/persistence/TTL, string reads and
integer counters, set membership/cardinality, sorted-set additions/removals,
score counts, rank/score ranges with scores, and scans. Tests cover this facet
on both frameworks and protocols, compare score bounds/order against
StackExchange.Redis on the same Redis server, and verify scan resume and
deferred page execution.
These facets do not establish full Hangfire compatibility. Remaining command
facets, transactions and conditions, and official upstream
suite acceptance are tracked by [#1269](https://github.com/thomhurst/Respire/issues/1269).

The server/lock inventory additionally checks `RedisStorage` discovery and
dashboard `InfoRaw`, `RedisConnection.GetUtcDateTime` (`IServer.Time`),
`RedisLock` acquisition/extension/release, and `RedisSubscription` literal
callback subscribe/unsubscribe. Real Redis tests run on .NET 8 and .NET 10 with
RESP2/RESP3, including physical replica identity, token/TTL wire controls,
competing acquisitions, stale ownership, Lua errors, and borrowed-client cleanup.
Tests also construct the pinned 1.12.0 `RedisStorage`, obtain a storage connection,
read server time, and acquire/release its distributed lock. These focused
scenarios do not establish combined upstream Hangfire acceptance.

Before implementing this surface, the integration call sites were checked in
[ASP.NET Core 10.0.12 RedisCache.cs](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Caching/StackExchangeRedis/src/RedisCache.cs)
and
[RedisXmlRepository.cs](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs).
The cache uses hash reads/writes, leases, expiry, deletion, batches and wait
helpers; its setup/cleanup uses `GetDatabase`, profiling registration,
library-name suffixes, `Close` and `Dispose`. Optional profiling is unsupported;
the cache catches unsupported suffix calls. DataProtection uses `ListRange` and
`ListRightPush` through a database factory.

The test project adapts upstream `RedisCacheSetAndRemoveTests`,
`TimeExpirationTests`, `TimeExpirationAsyncTests`, and the four
`DataProtectionRedisTests` scenarios to TUnit/Testcontainers with the adapter.
Additional tests exercise the official buffer cache and DataProtection service
registration, plus Redis wire controls for binary data, database isolation,
TTL, deferred pipeline order, command errors, cancellation and ownership.
These are the named upstream scenarios, not every test in ASP.NET Core.

Migrate one call site at a time, then replace the bridge with native typed
commands when its callers no longer require StackExchange.Redis values. The
boundary allocates argument arrays and standalone reply copies; native APIs
avoid those migration costs.
