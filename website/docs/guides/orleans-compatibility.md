---
title: Orleans Redis provider compatibility
description: Run the official Orleans Redis clustering, persistence, and reminder providers on Respire through the StackExchange.Redis adapter.
---

# Orleans Redis provider compatibility

Run the official Orleans Redis providers on Respire by injecting the `Respire.StackExchangeCompat`
adapter. You keep the official providers and their Redis storage format; no separate native provider is needed.

Tested packages, all at **10.2.2** (Orleans commit [`a575888`](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997)):

- `Microsoft.Orleans.Clustering.Redis`
- `Microsoft.Orleans.Persistence.Redis`
- `Microsoft.Orleans.Reminders.Redis`
- `Microsoft.Orleans.Server` (the acceptance suite's silo runtime and grain code generator)

Other versions, including **10.4.0**, are not assessed. A new version needs a new compatibility check.

## Configure the official providers

Install `Respire.StackExchangeCompat` and the three providers at 10.2.2. Pass the adapter through each
provider's `CreateMultiplexer` option:

```csharp
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Respire;
using Respire.StackExchangeCompat;
using StackExchange.Redis;

// Keep this multiplexer alive until the silo has stopped, then dispose it.
IConnectionMultiplexer multiplexer = RespireConnectionMultiplexer.Create(
    RespireOptions.Parse("redis://localhost:6379/0"));
var configuration = ConfigurationOptions.Parse("localhost:6379,defaultDatabase=0");

var builder = Host.CreateApplicationBuilder();
builder.UseOrleans(siloBuilder =>
{
    siloBuilder.UseRedisClustering(options =>
    {
        options.ConfigurationOptions = configuration;
        options.CreateMultiplexer = _ => Task.FromResult((multiplexer, true));
    });
    siloBuilder.AddRedisGrainStorageAsDefault(options =>
    {
        options.ConfigurationOptions = configuration;
        options.CreateMultiplexer = _ => Task.FromResult((multiplexer, true));
    });
    siloBuilder.UseRedisReminderService(options =>
    {
        options.ConfigurationOptions = configuration;
        options.CreateMultiplexer = _ => Task.FromResult((multiplexer, true));
    });
});
```

- `true` in the tuple is `IsShared`: Orleans borrows the multiplexer and does not dispose it.
- For provider-owned connections, return a **separate** adapter instance from each factory with `IsShared = false`.
  `RespireConnectionMultiplexer.Create(options)` owns its native clients; when using `Wrap`, pass
  `ownsClient: true` if the provider should also dispose the native client. Never return one shared
  instance with `false` from several providers: the first provider to stop would close the others' connection.
- Orleans' validators still require `ConfigurationOptions`. Keep its endpoint, database, and credentials the same as the Respire options.
- Configure service and cluster IDs, serializer, expiry, and clear mode as usual.
- For client gateway discovery, set the same clustering `CreateMultiplexer` option on the Orleans client builder.
- A `ServiceKey` can supply a DI-registered multiplexer through the same factory.

## What the providers use

All calls use `CommandFlags.None` and `GetDatabase()` with no arguments, so the configured default database applies.

| Provider/source | Members actually called |
| --- | --- |
| [RedisMembershipTable](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Clustering.Redis/Storage/RedisMembershipTable.cs) | `IDatabase.KeyDeleteAsync(RedisKey)`, `HashSetAsync(RedisKey, RedisValue, RedisValue, When)`, `KeyExpireAsync(RedisKey, TimeSpan?)`, `HashGetAllAsync`, `HashDeleteAsync(RedisKey, RedisValue)`, `CreateTransaction()` |
| Membership transactions | `ITransaction.HashSetAsync` with `When.Always`, `HashGetAsync(RedisKey, RedisValue)`, `AddCondition(Condition.HashEqual(...))`, `AddCondition(Condition.HashNotExists(...))`, `ExecuteAsync()`, `ConditionResult.WasSatisfied` after abort |
| [RedisGatewayListProvider](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Clustering.Redis/Providers/RedisGatewayListProvider.cs) | No direct Redis interface calls; uses the membership table |
| [RedisGrainStorage](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Persistence.Redis/Storage/RedisGrainStorage.cs) | `IDatabase.HashGetAllAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` for write, clear, and delete-on-clear |
| [RedisReminderTable](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Reminders.Redis/Storage/RedisReminderTable.cs) | `IDatabase.KeyExpireAsync(RedisKey, TimeSpan?)`, `KeyDeleteAsync(RedisKey)`, `SortedSetRangeByValueAsync(RedisKey, RedisValue, RedisValue, Exclude, long, long, CommandFlags)`, `SortedSetRemoveRangeByValueAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` |

The adapter implements all of these. Key behaviors:

- `ScriptEvaluateAsync` sends the provider's Lua script in one `EVAL`, so ETag compare-and-write, clear, and delete stay atomic.
- `SortedSetRangeByValueAsync` uses `ZRANGEBYLEX` with the same bounds, including wrapped hash-ring ranges.
- `SortedSetRemoveRangeByValueAsync` uses `ZREMRANGEBYLEX`, so a stale ETag cannot remove a replacement reminder.

None of the providers use `IBatch`, `ISubscriber`, server discovery, profiling, or `ChannelMessageQueue`.
See the [adapter guide](stackexchange-interop.md) for script and flag limits.

## Tested scope

`tests/Respire.Orleans.Acceptance.Tests` runs upstream provider scenarios against StackExchange.Redis and then
the adapter, on .NET 8 and .NET 10 with RESP2 and RESP3. Ten additional adapter-only cases start real
Orleans silo and client hosts. All containers use Redis **7.0.15**. Its `NOTICE.md` records sources and exclusions.

| Provider | Covered |
| --- | --- |
| Clustering | Insert/read, stale versions, duplicate inserts, update, heartbeat, cleanup, gateway filtering |
| Persistence | Write/read, both clear modes, stale/null ETag rejection, concurrent writers, expiry (TTL set, not waited out), binary state, interoperability with StackExchange.Redis |
| Reminders | Upsert/read, removal, interoperability, multiple grains, hash-ring ranges, concurrent upserts |

Interoperability tests write with one client and read with the other, so data written by the official
StackExchange.Redis setup works with the adapter and back.

### Silo lifecycle and Redis recovery

The real-host scenarios configure Redis membership, default grain storage, reminders, and client gateway
discovery through the adapter. No provider calls fall back to StackExchange.Redis. A small integer-state
grain uses the Orleans binary serializer; test-only reminder settings lower the minimum period to one second.

| Deployment | Covered |
| --- | --- |
| Standalone, shared multiplexer | Grain activation, persisted write/read, real reminder delivery, graceful silo shutdown, complete silo restart with the same service/cluster IDs, reminder-driven reactivation without re-registering, state surviving restart |
| Standalone, provider-owned multiplexers | The same lifecycle; membership, storage, reminders, and client discovery each own a separate adapter/native client. Shutdown asserts both layers are disposed, while stopping the silo leaves the still-running client's gateway connection usable |
| Three-primary Redis Cluster, database 0 | The shared lifecycle scenario, including membership transactions, persistence scripts, reminder delivery and restart through native Cluster routing |
| Standalone socket loss | Disconnect live Redis TCP connections with `CLIENT KILL`, reconnect with the same adapter and grain activation, write state and deliver reminders after recovery, join a second silo, then reactivate there and read the saved state |
| Sentinel (one primary, one replica, three Sentinels) | Fence completed writes to the replica, stop the primary, observe the same native client connect to the promoted replica, continue grain writes/reminders, join a second silo, return the old primary as a replica, then verify state from a new activation |
| Cluster slot migration | Move the membership key between primaries, finish slot ownership changes, then join another silo and use grain storage through the existing adapter. A separate case exercises ASK while the slot is importing/migrating: membership insertion fails without inserting the row, then recovers once migration completes |
| Cluster with three primaries and three replicas | Coordinated `CLUSTER FAILOVER` on all three replicas while a silo is running; retain its activation, write state, receive reminders, join another silo, then reactivate there and read saved state |
| Abrupt silo process death, standalone Redis | Start a second silo in a child process, kill it without graceful shutdown, and require the surviving silo to mark its row `Dead` within 60 seconds. The test logs elapsed detection time, verifies the survivor remains active, and calls a grain through client discovery |

The topology cases use `Respire.Testing.Containers`, which runs each deployment in an isolated disposable
container. They model server processes and protocol routing, not independent host or network failures.
The socket-loss and replication-fence setup uses a separate administrative control connection only.
Recovery hosts run in a child process with a four-minute budget after Redis startup. The parent
captures server logs and removes its container even if Orleans shutdown ignores cancellation.
Diagnostics, process termination, output drain and container disposal extend the maximum wait to
310 seconds after child startup; Docker cleanup failures are reported. A separate failed-election
regression removes the only replica before stopping the primary and requires recovery to fail.
It leaves Sentinel TILT protection enabled and retains the healthy failover assertions.
The tests do not claim lossless writes during failover: Redis replication is asynchronous. The Sentinel
scenario fences the writes it expects to retain before stopping the primary; the Cluster scenario uses
coordinated promotion, which synchronizes the replica before switching roles. Unplanned Cluster primary
crashes and writes racing the promotion are not covered by that test.

The silo-death deadline is a test bound, not a production eviction guarantee. Tests set one-second probes,
membership refreshes and heartbeats, two missed probes, and one death vote. Production detection time
depends on Orleans membership settings, cluster size, scheduling and connectivity. Eviction here means
the membership row becomes `Dead`; historical rows need not be deleted immediately.

### Supported topologies and CI limits

Standalone Redis, database-0 Redis Cluster with stable slot ownership, and Sentinel are supported through
the adapter within the coverage above. Cluster membership writes during **ASK migration are unsupported**:
the adapter cannot move an in-progress watched transaction to an importing node without losing its watch.
Schedule resharding outside membership changes, or expect provider operations to fail until the slot move
completes. Successful post-move recovery does not establish uninterrupted membership availability.
Support during the ASK phase is tracked in [#1820](https://github.com/thomhurst/Respire/issues/1820).

Multi-host network partitions are **not validated in CI**. The Redis fixture publishes loopback endpoints
and runs nodes in one container; silo hosts also advertise loopback. Disconnecting that container does not
model asymmetric silo-to-silo, silo-to-Redis, or independent host failures. Such a test needs silos and Redis
nodes with independently routable addresses and selective network fault injection (for example network
namespaces and packet filters). The current CI fixture has no such harness. No partition-tolerance or
split-brain claim follows from the socket-loss, process-death, or failover cases.

For Cluster, set `RespireOptions.UseCluster = true`, configure reachable seed/advertised node endpoints,
and use database **0**. Nonzero Cluster databases are unsupported. Provider transactions and scripts each
operate on one Redis key, so the tested providers do not require a common hash tag across their keys.
For Sentinel, configure the native adapter with Sentinel endpoints and `SentinelPrimaryName`; keep
`ConfigurationOptions.ServiceName` and the provider configuration consistent. Combining Cluster and
Sentinel routing in one adapter is unsupported. Provider configuration alone does not configure native routing.

Run the suite from a repository checkout (requires Docker):

```powershell
$env:RESPIRE_TEST_PROTOCOL = '2' # Repeat with '3'.
dotnet run --project tests/Respire.Orleans.Acceptance.Tests -c Release -f net10.0 -- --minimum-expected-tests 28
# Repeat with -f net8.0 as well.
```

Not validated:

- Multi-host partitions, long outages, sustained stress, other serializers and datasets.
- Unplanned Cluster primary crashes, failover racing provider transactions, and uninterrupted writes during ASK migration.
- Provider-owned topology recovery, TLS/authentication changes, Redis data loss/restart without retained data,
  and simultaneous Redis and silo failures.
- Grain directory, streaming, and journaling providers, and other Orleans versions.

[Topology and silo-death assessment](https://github.com/thomhurst/Respire/issues/1799) extends the
[original lifecycle assessment](https://github.com/thomhurst/Respire/issues/1695).
Neither assessment establishes support for the untested deployment scenarios above.
