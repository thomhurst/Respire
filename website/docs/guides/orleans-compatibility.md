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
the adapter, on .NET 8 and .NET 10 with RESP2 and RESP3, against standalone Redis 7.0.15. Its `NOTICE.md`
records sources and exclusions.

| Provider | Covered |
| --- | --- |
| Clustering | Insert/read, stale versions, duplicate inserts, update, heartbeat, cleanup, gateway filtering |
| Persistence | Write/read, both clear modes, stale/null ETag rejection, concurrent writers, expiry (TTL set, not waited out), binary state, interoperability with StackExchange.Redis |
| Reminders | Upsert/read, removal, interoperability, multiple grains, hash-ring ranges, concurrent upserts |

Interoperability tests write with one client and read with the other, so data written by the official
StackExchange.Redis setup works with the adapter and back.

Run the suite from a repository checkout (requires Docker):

```powershell
$env:RESPIRE_TEST_PROTOCOL = '2' # Repeat with '3'.
dotnet run --project tests/Respire.Orleans.Acceptance.Tests -c Release -f net10.0 -- --minimum-expected-tests 18
# Repeat with -f net8.0 as well.
```

Not validated:

- Full silo and grain activation, other serializers and datasets, provider-owned shutdown, sustained stress.
- Redis Cluster, reconnect, and failover.
- Grain directory, streaming, and journaling providers, and other Orleans versions.
