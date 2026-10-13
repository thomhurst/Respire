---
title: Incremental StackExchange.Redis migration
description: Supported library versions, upstream coverage, ownership contracts, and migration from StackExchange.Redis to native Respire APIs.
---

# Incremental StackExchange.Redis migration

Prefer Respire's native typed APIs for normal application code. Install
`Respire.StackExchangeCompat` when migrating code that still passes `RedisKey`,
`RedisValue`, or consumes `RedisResult`. The package depends on StackExchange.Redis
3.4.0 or later and targets .NET 8 and .NET 10.

```bash
dotnet add package Respire.StackExchangeCompat
```

## Supported libraries and versions

The compatibility adapter supports the integration call sites listed below.
Respire tests against these versions; verify other versions yourself. All
acceptance runs use standalone Redis. This evidence does not establish every
feature or general StackExchange.Redis interface parity.

| Library | Pinned version | Verified scope | Native migration |
| --- | --- | --- | --- |
| `Microsoft.Extensions.Caching.StackExchangeRedis` | 10.0.12 on both frameworks | Public upstream cache and expiration classes, six service registration facts, buffer cache | `Respire.Caching` via `AddRespireDistributedCache` |
| `Microsoft.AspNetCore.DataProtection.StackExchangeRedis` | 10.0.12 on both frameworks | All four repository scenarios and the builder extension fact, shared persisted key rings | `Respire.DataProtection` via `PersistKeysToRespire` |
| `Hangfire.Redis.StackExchange` | 1.12.0 | 93 upstream facts and two worker lifecycle cases; [exact exclusions](./hangfire-acceptance) | Keep the shim at the Hangfire boundary; use native typed facets in application code |
| `Microsoft.AspNetCore.SignalR.StackExchangeRedis` | 8.0.31 / 10.0.12 | **Not supported:** requires queue-returning subscriptions, which the adapter does not provide | `Respire.SignalR` via `AddRespire` |

The cache/DataProtection upstream inventory covers **52 cases across six test
classes**. Only the two rows of the cache's internal HybridCache detection test
are excluded: they need ASP.NET Core's friend assembly grant and never use Redis
or the adapter. Registration facts verify DI behavior, not Redis wire behavior.
The [source inventory and adaptations](https://github.com/thomhurst/Respire/blob/main/tests/Respire.StackExchangeCompat.Tests/Upstream/NOTICE.md)
record each class, case count and exclusion. Hangfire's excluded string constructors
create StackExchange.Redis internally and cannot test an injected adapter.

`IConnectionMultiplexer`, `IDatabase`/`IDatabaseAsync`, `IBatch`,
`ITransaction`/`ITransactionAsync`, `IServer` and `ISubscriber` have only the
documented supported members. Use the command and lifetime inventories below
before migrating a new library. Unsupported operations throw
`NotSupportedException` with native API guidance. Server errors retain native
Respire exception types; consumers catching StackExchange.Redis-specific
exceptions must update their handlers. Ordinary commands provide sync and async
variants; batches and transactions queue async commands until execution.

SignalR's `ChannelMessageQueue` construction, message delivery/completion and
owned unsubscribe require internal StackExchange.Redis APIs. Callback pub/sub
support for Hangfire does not satisfy that contract. The adapter does not reach
into StackExchange.Redis internals, so the queue-returning `ISubscriber`
overloads stay unsupported and Microsoft's SignalR backplane cannot run on the
adapter. Use the native [`Respire.SignalR` backplane](../integrations/signalr)
instead; it interoperates with servers that still run Microsoft's backplane.
Orleans is assessed separately in the [Orleans compatibility guide](./orleans-compatibility).

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
`IDatabase`, `IDatabaseAsync`, `IBatch`, and `ITransaction` surface required by
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
| `SortedSetLength` | Inclusive or exclusive score bounds; the default `-Infinity` to `+Infinity` range uses `ZCARD` and ignores endpoint exclusions, matching StackExchange.Redis |
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
Integer adjustments select `INCR`/`DECR` for changes of one and the corresponding
`INCRBY`/`DECRBY` for other signed changes, including deferred batches. Existing
command-specific Redis ACLs therefore see the same commands as StackExchange.Redis.

### Batch and lifetime behavior

`CreateBatch()` supports the listed hash/list/key/string/set/sorted-set asynchronous
methods, string-based `ScriptEvaluateAsync`, and `KeyExpireAsync` with `None`/`DemandMaster` flags. No queued command executes
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
non-null `asyncState`, and all unlisted commands are explicitly
unsupported. Unsupported members throw `NotSupportedException` with native
API guidance rather than returning fabricated success. This adapter does not
provide general StackExchange.Redis parity. Pinned Hangfire acceptance is
documented [separately](./hangfire-acceptance). Microsoft's SignalR backplane is
not supported; use [`Respire.SignalR`](../integrations/signalr).
Floating-point and bounded/expiring string increment overloads and conditional
sorted-set additions remain unsupported.

`SortedSetRangeByValue[Async]` supports both overloads, ascending/descending order,
inclusive/exclusive binary bounds, null/open bounds, and `skip`/`take` pagination.
`SortedSetRemoveRangeByValue[Async]` preserves the supplied bound order and returns
the number removed. These use Redis lexicographic commands; all members must have
the same score for Redis to provide defined lexicographic ordering.

### Transactions and conditions

`IDatabase.CreateTransaction()` and `IDatabaseAsync.CreateTransaction()` return a
deferred `ITransaction`/`ITransactionAsync`. Transactions support the listed
hash/list/key/string/set/sorted-set asynchronous commands, except cursor scans,
plus literal `PublishAsync`. They copy binary arguments when queued, preserve
command order, and execute through native `MULTI`/`EXEC`. Publish is part of that
same transaction, including the notifications used by Hangfire. String-based
`ScriptEvaluateAsync` and lexicographic ranges/removals are also supported.
Lock operations, other script overloads, scans, synchronous command calls,
and nested transactions remain unsupported.

Result-bearing `Execute` and `ExecuteAsync`, and queued commands, accept only
`None` and `DemandMaster`. Replica routing, `NoRedirect`, `FireAndForget`, and
other flags throw before consuming the pending queue. `IBatch.Execute()` on a
transaction starts execution without returning its commit result; queued tasks
still receive their results or errors. Non-null `asyncState` is unsupported.

Each Execute takes the current commands and conditions. New work can be queued
for a later Execute, and overlapping executions on one transaction run in order.
An empty Execute sends PING and returns true after its reply, without replaying
earlier commands. Repeat Execute
after false has the same queue-consumption behavior as StackExchange.Redis:
the aborted tasks stay canceled, and neither commands nor conditions are retried.
Queue a fresh attempt explicitly when a retry is needed.

`AddCondition` snapshots its key and value bytes. Execution WATCHes every condition
key before reading conditions through an uncached primary view. All conditions
are evaluated, including after a failed condition, and each returned
`ConditionResult.WasSatisfied` reports its own check. A false check discards the
transaction without running commands. A mutation between WATCH and EXEC returns
false and cancels every queued task, while already satisfied ConditionResults stay
true. `WasWatchConflict` distinguishes that Redis abort from a failed check.
The native same-slot restriction applies to watched and queued keys in Cluster.

The condition inventory follows
[StackExchange.Redis 3.3.1 Condition.cs](https://github.com/StackExchange/StackExchange.Redis/blob/3.3.1/src/StackExchange.Redis/Condition.cs),
and tests also validate the condition shapes against StackExchange.Redis 3.4.0:

| Condition family | Supported factories |
| --- | --- |
| Existence | `KeyExists`, `KeyNotExists`, `HashExists`, `HashNotExists`, `SetContains`, `SetNotContains`, `SortedSetContains`, `SortedSetNotContains` |
| Equality | `StringEqual`, `StringNotEqual`, `HashEqual`, `HashNotEqual`, `SortedSetEqual`, `SortedSetNotEqual`; null string/hash values retain upstream existence semantics |
| List index | `ListIndexEqual`, `ListIndexNotEqual`, `ListIndexExists`, `ListIndexNotExists`; signed indexes and null comparisons |
| Length | `HashLength`, `StringLength`, `ListLength`, `SetLength`, `SortedSetLength`, and `StreamLength` with `Equal`, `LessThan`, and `GreaterThan` suffixes |
| Score-range length | The three bounded `SortedSetLength` condition overloads, with inclusive minimum/maximum scores and infinity bounds |

`SortedSetContainsStarting`/`SortedSetNotContainsStarting` and all
`SortedSetScoreExists`/`SortedSetScoreNotExists` overloads are unsupported.
Unknown private condition shapes throw actionable `NotSupportedException`
at AddCondition, with guidance to use native WATCH. The public Condition API
has no visitor and ConditionResult has no factory or setter, so the bridge uses
explicitly audited private fields and accessors. It preserves supported fields
for trimming and never parses `Condition.ToString()`, which would lose binary data.

Redis errors while checking conditions count as unsatisfied conditions, matching
upstream. Runtime command errors inside EXEC fault only that command's task;
other tasks retain their results and Execute returns true. Redis queue errors
discard the entire transaction, fault all its tasks, and fail Execute. Local
queue-construction failures also discard the entire queue rather than committing
a subset. Native server exception types and messages are preserved.

Closing the adapter cancels unexecuted transaction tasks. Default close drains
admitted executions, including their condition checks; `Close(false)` cancels
started execution and settles every task. A canceled accepted transaction may
still run on Redis. Native results are disposed after conversion, watched leases
are released on every outcome, and borrowed clients remain caller-owned.

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
but string-based `ScriptEvaluate[Async]` is supported for Orleans and other callers.
Scripts execute atomically with binary `KEYS`/`ARGV`, optional key/value arrays,
and standalone scalar, null, or nested array replies. A 40-character hexadecimal
string executes `EVALSHA`; other strings execute `EVAL` without a script-cache
probe. SHA string calls on an ordinary database hold its dispatch gate until
the reply completes because native stored-procedure telemetry has no admission
signal. Lua script errors preserve native `RespireServerException`; missing
hashes return `NOSCRIPT` without retries. Byte-array hash, `LuaScript`,
`LoadedLuaScript`, and read-only script overloads remain unsupported. The existing
boundary conversion restrictions, including nested error replies, still apply.

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

### How the adapter is validated

The supported members were chosen by inventorying the call sites of each library:

- [ASP.NET Core 10.0.12 RedisCache.cs](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/Caching/StackExchangeRedis/src/RedisCache.cs)
  uses hash reads and writes, leases, expiry, deletion, batches, and wait helpers. Its setup and
  cleanup use `GetDatabase`, profiling registration, library-name suffixes, `Close`, and `Dispose`.
  Profiling is unsupported, and the cache catches the unsupported suffix calls.
- [RedisXmlRepository.cs](https://github.com/dotnet/aspnetcore/blob/v10.0.12/src/DataProtection/StackExchangeRedis/src/RedisXmlRepository.cs)
  uses `ListRange` and `ListRightPush` through a database factory.
- `Hangfire.Redis.StackExchange` 1.12.0, at repository commit
  [`da8e39a33df204900afc30aeb65110f76f081c55`](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55),
  uses the listed hash, list, key, string, set, and sorted-set commands, scans, transactions with
  queued notifications, server discovery, `InfoRaw`, `Time`, token locks, and literal callback
  subscriptions.

Tests run against real Redis with RESP2 and RESP3 on .NET 8 and .NET 10. They port the upstream
cache and DataProtection test classes listed above, and the
[Hangfire acceptance](./hangfire-acceptance) ports 93 upstream facts plus two worker lifecycle
cases. Focused tests add wire-level controls for binary data, database isolation, TTL, deferred
batch and transaction order, conditions and WATCH races, server errors, cancellation, and client
ownership. This is coverage of these integrations, not general StackExchange.Redis interface parity.

## Move application code to native APIs

Keep one adapter at each remaining library boundary. Replace direct database
calls with native facets such as `client.Strings`, `client.Hashes`, `client.Lists`
and `client.SortedSets`. Use native batches/transactions when ordering or atomicity
is required; do not replace a transaction with independent calls.

For Microsoft integrations, switch service registration to the native
[distributed cache](../integrations/caching), `Respire.DataProtection` or
[SignalR](../integrations/signalr) package. Preserve instance/key prefixes, selected database
and ownership when changing registration. Existing application consumers can
keep using `IDistributedCache`, `IDataProtectionProvider` or SignalR hub APIs.
Hangfire's storage package still consumes StackExchange.Redis interfaces, so
retain its injected adapter while migrating unrelated application commands.

For native DataProtection, install `Respire.DataProtection` and pass a factory
for the existing native client, keeping the shared Redis list key:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.DataProtection;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var services = new ServiceCollection();
services.AddDataProtection().PersistKeysToRespire(() => client, "app:keys");
```

The repository borrows this client. Preserve DataProtection application names,
purposes and key encryption settings so existing protected payloads remain usable.

Keep borrowed native clients alive until all cache/storage operations and
callback subscriptions finish. Closing the adapter removes its own subscriptions
and settles queued/in-flight work; a borrowed client remains usable. An owned
client closes with its adapter. Do not share a cache-owned adapter with a
longer-lived Hangfire worker or DataProtection key repository.

### Orleans Redis providers

The [Orleans 10.2.2 acceptance suite](./orleans-compatibility) inventories clustering,
grain persistence, and reminders against the adapter. Its 18 provider cases
exercise membership/gateways, atomic binary storage and ETag conflicts, expiry,
reminder replacement and hash-ring ranges, and bidirectional interoperability
with the official client. The factory injection uses the existing official
providers without a fork. These providers do not use SignalR's
`ChannelMessageQueue` path. The guide records setup, exact versions, and exclusions.

Migrate one call site at a time, then replace the bridge with native typed
commands when its callers no longer require StackExchange.Redis values. The
boundary allocates argument arrays and standalone reply copies; native APIs
avoid those migration costs.
