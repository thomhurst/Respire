---
title: Use Respire with Orleans
description: Run the official Orleans Redis clustering, grain storage, and reminder providers on Respire through the StackExchange.Redis adapter.
---

# Use Respire with Orleans

The official Orleans Redis providers run on Respire unchanged. Hand them the `Respire.StackExchangeCompat`
adapter instead of a StackExchange.Redis multiplexer, and you keep the official providers, their
configuration, and their Redis storage format. No separate Respire-specific provider is needed.

Supported providers, tested at version **10.2.2**:

| Package | Feature |
| --- | --- |
| `Microsoft.Orleans.Clustering.Redis` | Silo membership and client gateway discovery |
| `Microsoft.Orleans.Persistence.Redis` | Grain storage |
| `Microsoft.Orleans.Reminders.Redis` | Reminders |

Data is interchangeable: state and reminders written through StackExchange.Redis can be read through
the adapter and the other way round, so you can switch an existing deployment without migrating data.

## Setup

Install `Respire.StackExchangeCompat` and the providers, then pass the adapter through each
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
- To let each provider own its connection, return a **separate** adapter instance from each factory with
  `IsShared = false`. `RespireConnectionMultiplexer.Create(options)` owns its native clients; with `Wrap`,
  pass `ownsClient: true` if the provider should also dispose the native client. Never return one shared
  instance with `false` from several providers: the first provider to stop would close the others' connection.
- Orleans still validates `ConfigurationOptions`. Keep its endpoint, database, and credentials in line with the Respire options.
- Configure service and cluster IDs, serializer, expiry, and clear mode as usual.
- For client gateway discovery, set the same clustering `CreateMultiplexer` option on the Orleans client builder.
- A `ServiceKey` can supply a DI-registered multiplexer through the same factory.

## What works

- **Clustering:** membership inserts and updates, version checks, heartbeats, cleanup, gateway
  filtering, and detection of a silo that dies without shutting down.
- **Grain storage:** read, write, both clear modes, ETag concurrency checks, expiry, and binary state.
  The provider's Lua scripts run as a single `EVAL`, so compare-and-write, clear, and delete stay atomic.
- **Reminders:** registration, removal, delivery, hash-ring range queries, and reactivating grains
  after a silo restart.
- **Recovery:** silos keep working through dropped connections, Sentinel failover, Redis Cluster
  failover, and Cluster slot migration, on the same adapter instance.

The providers call only adapter members that Respire implements; none of them need `IBatch`,
`ISubscriber`, server discovery, or profiling. See the [adapter guide](stackexchange-interop.md)
for the adapter's general limits.

## Topologies

| Deployment | Configuration |
| --- | --- |
| Standalone Redis | Default. |
| Redis Cluster | Set `RespireOptions.UseCluster = true`, configure reachable seed endpoints, and use database **0**. Nonzero Cluster databases are not supported. The providers use one key per transaction or script, so no hash tags are needed. |
| Sentinel | Configure the adapter with Sentinel endpoints and `SentinelPrimaryName`, and keep `ConfigurationOptions.ServiceName` consistent with it. |

Provider configuration alone does not configure routing: set Cluster or Sentinel on the Respire options.
Combining Cluster and Sentinel in one adapter is not supported.

## Limitations

- Tested with Orleans 10.2.2. Check other versions before upgrading.
- Grain directory, streaming, and journaling providers are not covered.
- Membership transactions are never replayed after Redis may have accepted them. If a topology change
  races a transaction, that membership operation fails rather than risk a duplicate write.
- Redis replication is asynchronous, so writes that race an unplanned failover can be lost, as with any Redis client.
- How quickly a dead silo is evicted depends on your Orleans membership settings, not on Respire.
