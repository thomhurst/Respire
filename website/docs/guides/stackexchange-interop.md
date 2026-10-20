---
title: Incremental StackExchange.Redis migration
description: Use Respire with libraries that expect StackExchange.Redis types, and migrate to native Respire APIs.
---

# Incremental StackExchange.Redis migration

:::warning Experimental API: RSP0006
This feature requires an explicit compiler opt-in and is outside the 2.0 compatibility promise.
See [RSP0006](../diagnostics/RSP0006.md) for the affected APIs and the exact `NoWarn` or `#pragma` opt-in.
:::

`Respire.StackExchangeCompat` lets code and libraries that use StackExchange.Redis types (`RedisKey`,
`RedisValue`, `RedisResult`, `IConnectionMultiplexer`) run on Respire. Use it during migration or at
library boundaries; use Respire's native typed APIs everywhere else.

The package targets .NET 8 and .NET 10 and depends on StackExchange.Redis 3.4.0 or later.

```bash
dotnet add package Respire.StackExchangeCompat
```

## Supported libraries and versions

The adapter supports the call sites these libraries use, tested against the pinned versions on standalone Redis.
Verify other versions yourself. It does not provide general StackExchange.Redis parity.

| Library | Pinned version | Status | Native replacement |
| --- | --- | --- | --- |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | 10.0.13 | Supported (upstream cache, expiration, and registration tests) | `Respire.Caching` via `AddRespireDistributedCache` |
| `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` | 10.0.13 | Supported (upstream repository and builder tests) | `Respire.DataProtection` via `PersistKeysToRespire` |
| `Hangfire.Redis.StackExchange` | 1.12.0 | Supported; see [exclusions](./hangfire-acceptance) | Keep the adapter at the Hangfire boundary |
| `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | 8.0.32 / 10.0.13 | **Not supported** | `Respire.SignalR` via `AddRespire` |
| Orleans Redis providers | 10.2.2 | Supported; see [Orleans compatibility](./orleans-compatibility) | |

SignalR's backplane needs queue-returning `ISubscriber` overloads (`ChannelMessageQueue`), which depend on
StackExchange.Redis internals. Use the native [`Respire.SignalR` backplane](../integrations/signalr) instead;
it interoperates with servers still running Microsoft's backplane.

The [upstream test inventory](https://github.com/thomhurst/Respire/blob/main/tests/Respire.StackExchangeCompat.Tests/Upstream/NOTICE.md)
lists each ported test class and exclusion.

General rules:

- `IConnectionMultiplexer`, `IDatabase`/`IDatabaseAsync`, `IBatch`, `ITransaction`/`ITransactionAsync`, `IServer`, and `ISubscriber` support only the members listed on this page. Check the lists before migrating a new library.
- Unsupported members throw `NotSupportedException` with guidance on the native API.
- Exceptions are native Respire types (for example `RespireServerException`). Update handlers that catch StackExchange.Redis exceptions.

## Test without a Redis server

`Respire.Testing.StackExchangeCompat` provides `RespireFakeConnection`, an async-disposable
fixture exposing `IConnectionMultiplexer` and `IDatabase` over in-memory pipes:

```csharp
using Respire.Testing.StackExchangeCompat;

await using var fixture = new RespireFakeConnection();
var database = fixture.Connection.GetDatabase();
await database.HashSetAsync("session", "status", "active");
```

Only the intersection of this adapter and the fake server is supported, in database zero.
The default fixture owns the server; a constructor-supplied `RespireFakeServer` stays caller-owned.
Dispose fixtures before borrowed servers. Unsupported members, commands and database selections
fail explicitly. This does not promise general StackExchange.Redis test-suite compatibility.
See [in-memory testing](in-memory-testing.md#stackexchangeredis-test-fixtures) for setup,
ownership, limitations, and direct composition with
`RespireConnectionMultiplexer.Create(server.CreateOptions())`.

## Execute native commands with StackExchange.Redis values

### Public type names during migration

Native public types use the `Respire` prefix (`IRespire` for interfaces). Native types
must not share a simple name with a public StackExchange.Redis type, so importing both
namespaces leaves each library's types unambiguous. The [public API naming guide](../fundamentals/api-naming.md)
covers the complete prefix and result-suffix rules, exceptions, and noncolliding renames.
The following pre-2.0 renames remove all current collisions:

| Previous native name | New native name |
| --- | --- |
| `BitFieldEncoding` | `RespireBitFieldEncoding` |
| `BitFieldOffset` | `RespireBitFieldOffset` |
| `BitFieldOperation` | `RespireBitFieldOperation` |
| `BitFieldOverflow` | `RespireBitFieldOverflow` |
| `ExpireWhen` | `RespireExpireWhen` |
| `GeoEntry` | `RespireGeoEntry` |
| `GeoPosition` | `RespireGeoPosition` |
| `GeoSearchShape` | `RespireGeoSearchShape` |
| `GeoUnit` | `RespireGeoUnit` |
| `ListMoveOrder` | `RespireListMoveOrder` |
| `ListSide` | `RespireListSide` |
| `SortedSetEntry` / `SortedSetEntry<T>` | `RespireSortedSetEntry` / `RespireSortedSetEntry<T>` |
| `StreamAddOptions` | `RespireStreamAddOptions` |
| `StreamNackMode` | `RespireStreamNackMode` |

Update native type references when upgrading. StackExchange.Redis types and the adapter's
StackExchange.Redis signatures retain their names. The old native names have no compatibility
aliases because those would preserve the ambiguity.

```csharp
using Respire;
using StackExchange.Redis;

static async Task RefreshExpiryAsync(IRespireClient client, IDatabase database)
{
    await database.KeyExpireAsync("legacy:key", TimeSpan.FromMinutes(5), ExpireWhen.Always);
    await client.Keys.ExpireAsync("native:key", TimeSpan.FromMinutes(5), RespireExpireWhen.Always);
}
```

### Execute a command

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

`ExecuteStackExchangeAsync` takes an `IRespireClient`, a native `RespireCommand`, and `ReadOnlySpan<RedisValue>`
arguments. It behaves like native `ExecuteAsync`: `RespireCommandFlags`, cancellation, routing, key prefixes,
blocking commands, and errors all work the same way. It does not translate StackExchange.Redis `CommandFlags`.
Key-prefixed views can reject commands they cannot prefix; use typed facets with `ToRespireKey`/`ToRespireValue` for those.

### Arguments and buffer ownership

- `ToRespireKey` rejects null keys and keeps empty keys. `ToRespireValue` keeps null distinct from empty.
- The bridge rejects null arguments before sending.
- Conversions use StackExchange.Redis's exact wire bytes. Binary data is never decoded as UTF-8, and numbers are not reformatted.
- Binary buffers can be borrowed. Do not mutate or release them until the command completes; copy them first if you need a snapshot.

### Supported replies

`ToStackExchangeResult` copies a reply into a standalone `RedisResult` that stays valid after the native reply is disposed.

| Native reply | `RedisResult.Resp3Type` |
| --- | --- |
| Simple string, bulk string | `SimpleString`, `BulkString` |
| Integer, boolean, double, big number | `Integer`, `Boolean`, `Double`, `BigInteger` |
| Array, map, set | `Array`, `Map`, `Set` |
| RESP2 null bulk string, null array; RESP3 null | `Null` |

- Null bulk strings keep `Resp2Type.BulkString`; null arrays keep `Resp2Type.Array`. `RespireResult.NullWireType` exposes the original null framing.
- Empty strings and aggregates stay non-null. Maps are flattened key/value elements.
- Error elements, bulk errors, verbatim strings, pushes, and attributes throw `NotSupportedException`. Errors are never converted to values. Top-level server errors throw `RespireServerException`.
- With `ToStackExchangeResult`, you dispose the native reply. `ExecuteStackExchangeAsync` disposes it for you, but never disposes the client.

## Distributed cache and DataProtection adapter

`RespireConnectionMultiplexer` implements the parts of `IConnectionMultiplexer`, `IDatabase`, `IDatabaseAsync`,
`IBatch`, and `ITransaction` used by `Microsoft.Extensions.Caching.StackExchangeRedis` and
`Microsoft.AspNetCore.DataProtection.StackExchangeRedis`. Prefer the native `Respire.Caching` and
`Respire.DataProtection` packages when they fit.

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

### Ownership and databases

- The cache closes its multiplexer when disposed. Do not share that adapter with longer-lived consumers.
- `Create` owns one lazy native client per database. `GetDatabase()` uses the configured default; `GetDatabase(n)` creates a separate client for `n`.
- `RespireConnectionMultiplexer.Wrap(client)` borrows an existing client and never disposes it unless you pass `ownsClient: true`. It accepts only that client's database.
- `Configuration` shows endpoints and the default database without credentials. It is not a reconnectable connection string.

### Supported commands

Each member has sync and async variants.

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
| `SortedSetLength` | Inclusive or exclusive score bounds; the default `-Infinity` to `+Infinity` range uses `ZCARD` and ignores endpoint exclusions, matching StackExchange.Redis |
| `SortedSetRangeByRank`, `SortedSetRangeByRankWithScores` | Inclusive signed indexes and ascending/descending order, with original binary members and optional scores |
| `SortedSetRangeByScore`, `SortedSetRangeByScoreWithScores` | Minimum/maximum score bounds, exclusion flags, order, skip/take, and optional scores; `take = -1` means unlimited |
| `SortedSetRangeByValue`, `SortedSetRemoveRangeByValue` | Ascending/descending, inclusive/exclusive binary bounds, open bounds, `skip`/`take`. All members need the same score for defined order |
| `SortedSetScan` | Lazy `ZSCAN` with binary patterns, page size, cursor, page offset, and asynchronous enumeration; see scan contracts below |
| `ListRange` | Start/stop indexes, including Redis negative indexes |
| `ListLeftPush`, `ListRightPush` | Scalar or array with `When.Always`/`When.Exists`; original list length, including an empty-array length query |
| `ListLength`, `ListGetByIndex` | Original length and binary indexed value; negative indexes supported, null for a missing index |
| `ListRemove`, `ListTrim` | Signed removal count and inclusive start/stop indexes, including negative indexes; Redis removal count and trimming behavior |
| `ListRightPopLeftPush` | Atomic move between lists; original binary value or null when the source is empty |
| `ScriptEvaluate` | String scripts only; see [locks and scripts](#locks-and-scripts) |
| `LockTake`, `LockExtend`, `LockRelease` | See [locks and scripts](#locks-and-scripts) |
| `Wait`, `WaitAll`, `TryWait` | Wait helpers used by the official cache; synchronous timeout follows native `CommandTimeout` |

Unsupported: floating-point and bounded/expiring string increments, conditional sorted-set additions,
profiling registration, library-name suffixes, events, non-null `asyncState`, and every unlisted member.

### Command rules

- Database commands require native `Connections = 1` (the default). A multi-connection configuration is rejected before sending, because separate connections can reorder a hash write and its expiry.
- Arguments are copied at call time; later buffer changes do not affect the command. Null keys and arguments are rejected.
- The adapter never replays a failed write. Native reconnect and failover still apply.
- Increments of 1 send `INCR`/`DECR`; other values send `INCRBY`/`DECRBY`, matching StackExchange.Redis for ACLs. A zero increment in fire-and-forget mode is a no-op.

Supported `CommandFlags`:

| Flag | Behavior |
| --- | --- |
| `None`, `DemandMaster` | Normal. |
| `PreferReplica`, `DemandReplica` | Read commands use native replica, Cluster, or Sentinel routing. |
| `NoRedirect` | Supported. Cannot combine with `FireAndForget`. |
| `FireAndForget` | Returns the default result and discards server errors. Cluster redirect failures can still surface. |

Other flags, including retry categories, throw `NotSupportedException`.

### Batches

`CreateBatch()` supports the listed async hash, list, key, string, set, and sorted-set methods, string
`ScriptEvaluateAsync`, and `KeyExpireAsync` with `None`/`DemandMaster`.

- Nothing runs before `Execute()`. Empty argument arrays complete immediately as no-ops.
- Each `Execute()` sends the current queue only. An empty call does not replay earlier work.
- Commands for the same key stay in order, even when a wrapped client has multiple connections.
- Batches are not transactional. Cluster slot groups can run independently.
- Each task gets its own result, error, or cancellation.

### Sorted-set scans

`SortedSetScan` and `SortedSetScanAsync` expose `IScanningCursor` on the enumerable and enumerator.

- `Cursor` is the server page of the current entry; `PageOffset` is its position in that page. Resume with both to repeat the current entry, or increment the offset to continue after it.
- `pageSize` is a Redis `COUNT` hint, not a result limit. Null, empty, and `*` patterns match all members.
- Redis scan semantics apply: no ordering or snapshot, and mutations can cause repeats or omissions.
- Pass cancellation through `GetAsyncEnumerator` or `WithCancellation`. Fire-and-forget scans are rejected.
- In a batch, each move that needs a new page queues it; call `Execute()` before awaiting the move. A filtered scan can need several executions for one move. Use a database scan if the caller cannot drive this.

### Close and dispose

- `Close`/`Dispose` reject new work and cancel queued tasks that have not run.
- The default close waits for started commands, then disposes owned clients. `Close(false)` cancels started commands first.
- Closing is idempotent. Borrowed clients stay usable.

### Transactions and conditions

`CreateTransaction()` returns a deferred `ITransaction`/`ITransactionAsync` that runs through native `MULTI`/`EXEC`.
It supports the listed async commands (except scans), literal `PublishAsync`, string `ScriptEvaluateAsync`, and
lexicographic ranges and removals. Locks, other script overloads, scans, sync calls, and nested transactions are unsupported.

- `Execute`, `ExecuteAsync`, and queued commands accept only `None` and `DemandMaster`. Other flags throw before the queue is consumed.
- `IBatch.Execute()` on a transaction starts it without returning the commit result.
- Each `Execute` takes the current commands and conditions. Overlapping executions run in order. An empty `Execute` sends `PING` and returns `true`.
- After `Execute` returns `false`, the aborted tasks stay cancelled and nothing is retried. Queue a new attempt to retry.
- A cancelled transaction may still run on Redis.

Conditions:

- `AddCondition` copies its key and value. Execution `WATCH`es every condition key, then evaluates all conditions; each `ConditionResult.WasSatisfied` reports its own check.
- A failed check discards the transaction. A Redis error during a check counts as unsatisfied.
- A change between `WATCH` and `EXEC` returns `false` and cancels all tasks. `WasWatchConflict` tells this apart from a failed check.
- In Cluster, watched and queued keys must share a slot.
- During ASK migration, rejected WATCH setup can establish a fresh watch on the importing node within
  the redirect limit and command timeout. Conditions are checked before `ASKING`/`MULTI`/`EXEC` on that
  watched socket. Unconditional transactions can follow a complete ASK rejection once on an exclusive
  importing socket. Commit failures after WATCH, partial EXEC results, and ambiguous outcomes are not replayed.

Errors:

- A command error inside `EXEC` faults only that task; `Execute` returns `true`.
- A Redis queue error, or a local error while building the queue, discards the whole transaction and faults every task.

Supported condition factories ([StackExchange.Redis Condition.cs](https://github.com/StackExchange/StackExchange.Redis/blob/3.3.1/src/StackExchange.Redis/Condition.cs), tested against 3.4.0):

| Condition family | Supported factories |
| --- | --- |
| Existence | `KeyExists`, `KeyNotExists`, `HashExists`, `HashNotExists`, `SetContains`, `SetNotContains`, `SortedSetContains`, `SortedSetNotContains` |
| Equality | `StringEqual`, `StringNotEqual`, `HashEqual`, `HashNotEqual`, `SortedSetEqual`, `SortedSetNotEqual`; null string/hash values retain upstream existence semantics |
| List index | `ListIndexEqual`, `ListIndexNotEqual`, `ListIndexExists`, `ListIndexNotExists`; signed indexes and null comparisons |
| Length | `HashLength`, `StringLength`, `ListLength`, `SetLength`, `SortedSetLength`, and `StreamLength` with `Equal`, `LessThan`, and `GreaterThan` suffixes |
| Score-range length | The three bounded `SortedSetLength` condition overloads, with inclusive minimum/maximum scores and infinity bounds |

Unsupported: `SortedSetContainsStarting`, `SortedSetNotContainsStarting`, `SortedSetScoreExists`, and
`SortedSetScoreNotExists`. Unknown conditions throw `NotSupportedException` at `AddCondition`; use native `WATCH`.
The adapter reads condition data through audited private fields, preserved for trimming.

### Servers and endpoints

- `GetEndPoints(true)` returns configured endpoints. `GetEndPoints(false)` connects and returns the live primary and replicas (standalone or Sentinel) or all Cluster nodes.
- `IdentifyEndpoint[Async]` returns the primary for a key's slot, or the connection for a keyless command. It supports `None`, `DemandMaster`, and `NoRedirect`; not replicas or `IBatch`.
- `GetServer` and `GetServers` return cached handles. Each owns a native client with the adapter's authentication, TLS, protocol, and database settings, and never redirects or fails over.
- Server members: `IsConnected` (connects and reports failures), `IsReplica` (queries `ROLE`), `InfoRaw[Async]` (`INFO` text, optional section), `Time[Async]` (`TIME` as UTC `DateTime`). Only `CommandFlags.None`.
- Closing the adapter disposes server clients; retained handles then reject calls.

### Locks and scripts

- `LockTake[Async]` sends `SET key token PX milliseconds NX`.
- `LockExtend[Async]` and `LockRelease[Async]` compare the binary token and run `PEXPIRE` or `DEL` atomically in Lua. A missing key or different owner returns `false`.
- Expiry must be positive, is truncated to milliseconds, and is at least 1 ms.
- Database flag rules apply; replica writes are rejected. `FireAndForget` returns `false`. Locks are not supported on `IBatch`.
- `ScriptEvaluate[Async]` takes a string script with binary keys and values. A 40-character hex string runs `EVALSHA`; anything else runs `EVAL`. A missing hash returns `NOSCRIPT` without retry.
- Unsupported: byte-array hashes, `LuaScript`, `LoadedLuaScript`, and read-only script overloads. Script errors throw `RespireServerException`.

### Subscriptions

- `GetSubscriber()` supports callback `Subscribe[Async]`, `Unsubscribe[Async]`, and `UnsubscribeAll[Async]` on literal channels with `None`.
- Handlers on one channel share a native subscription; duplicate handlers are ignored. Unsubscribing one handler keeps the others.
- Callback exceptions do not stop delivery.
- Native reconnect and resubscribe apply; messages sent during a gap are lost. If the reconnect limit is exhausted, subscribing throws; recreate the client and adapter.
- `Publish[Async]` works on the subscriber and database with `None` or `DemandMaster`, not in batches.
- Both apply the native pub/sub prefix.
- `ChannelMessageQueue`, patterns, and other subscriber members are unsupported.
- Closing the adapter removes only its own subscriptions.

### Call sites covered

- [ASP.NET Core RedisCache](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Caching/StackExchangeRedis/src/RedisCache.cs): hash reads and writes, leases, expiry, deletion, batches, wait helpers. It calls unsupported profiling and library-name suffix members and handles the failure.
- [RedisXmlRepository](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs): `ListRange` and `ListRightPush`.
- [`Hangfire.Redis.StackExchange` 1.12.0](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55): the listed commands, scans, transactions with notifications, server discovery, `InfoRaw`, `Time`, locks, and callback subscriptions.

Tests run against real Redis with RESP2 and RESP3 on .NET 8 and .NET 10.

## Move application code to native APIs

1. Keep one adapter at each library boundary that still needs StackExchange.Redis types (for example Hangfire storage).
2. Replace direct database calls with native facets such as `client.Strings`, `client.Hashes`, `client.Lists`, and `client.SortedSets`. Use native batches or transactions where you need order or atomicity; do not replace a transaction with separate calls.
3. Switch Microsoft integrations to the native [distributed cache](../integrations/caching), `Respire.DataProtection`, or [SignalR](../integrations/signalr) package. Keep instance and key prefixes, the database, and ownership the same. Application code can keep using `IDistributedCache`, `IDataProtectionProvider`, or SignalR hubs.
4. Replace remaining `ExecuteStackExchangeAsync` calls with native typed commands. Native APIs avoid the adapter's argument and reply copies.

For native DataProtection, pass a factory for your client and keep the same Redis list key:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var services = new ServiceCollection();
services.AddDataProtection().PersistKeysToRespire(() => client, "app:keys");
```

The repository borrows the client. Keep the same DataProtection application name, purposes, and key encryption
settings so existing protected payloads still decrypt.

Keep borrowed clients alive until all cache, storage, and subscription work finishes. Do not share a cache-owned
adapter with a longer-lived Hangfire worker or DataProtection repository.
