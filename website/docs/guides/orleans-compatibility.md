# Orleans Redis provider compatibility

`Respire.StackExchangeCompat` supports the inventoried Redis operations used by
Orleans clustering, grain persistence, and reminders. Use the existing official
providers with an injected adapter multiplexer. Separate native providers are
unnecessary for these assessed paths, and the Redis storage format remains the
official provider's format.

This assessment pins these packages to **10.2.2**, from Orleans tag `v10.2.2`,
commit [`a575888773a3b01b079c9c3720bb1553c3169997`](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997):

- `Microsoft.Orleans.Clustering.Redis`
- `Microsoft.Orleans.Persistence.Redis`
- `Microsoft.Orleans.Reminders.Redis`

This is a tested version and selected provider acceptance, not a blanket
supported-version declaration or the complete upstream conformance suite.

The acceptance project pins all three providers to **10.2.2** by updating its
central `PackageVersion` items. Repository defaults are **10.4.0**, but the
project pins preserve the provider and runtime versions used by this source
inventory and its recorded results. Renovate updates the repository defaults
together and excludes the assessment pins. Version **10.4.0** is not assessed
here; moving the pins requires a new source inventory and compatibility assessment.

## Configure the official providers

Install `Respire.StackExchangeCompat` and the three official provider packages
at the assessed version. In your existing Orleans silo configuration, supply
the adapter through each provider's `CreateMultiplexer` option:

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
```

The tuple's `true` means `IsShared`: Orleans borrows the multiplexer and leaves
disposal to its owner. Keep the connection endpoint, database, and credentials
consistent in both option objects. `ConfigurationOptions` is still required by
Orleans' hosting validators even though the custom factory selects the actual
connection. Configure your service/cluster IDs, serializer, expiry, and clear mode
as usual; the adapter executes the provider's original Lua scripts atomically.
Client gateway discovery uses the same clustering factory option on the Orleans
client builder. Full silo activation is outside the recorded tests below.

## Source inventory

All inventoried calls use the default `CommandFlags.None`. Each provider calls
`IConnectionMultiplexer.GetDatabase()` without arguments, so the configured
default database applies. Their `CreateMultiplexer` option returns
`Task<(IConnectionMultiplexer Multiplexer, bool IsShared)>`. Providers dispose
an owned multiplexer with `Dispose()` or `DisposeAsync()` and leave a shared
multiplexer to its owner. The shim implements those lifetime members.

| Provider/source | Members actually called | Shim assessment |
| --- | --- | --- |
| [RedisMembershipTable](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Clustering.Redis/Storage/RedisMembershipTable.cs) | `IDatabase.KeyDeleteAsync(RedisKey)`, `HashSetAsync(RedisKey, RedisValue, RedisValue, When)`, `KeyExpireAsync(RedisKey, TimeSpan?)`, `HashGetAllAsync`, `HashDeleteAsync(RedisKey, RedisValue)`, `CreateTransaction()` | Implemented, including initialization's `When.NotExists` |
| Membership transactions | `ITransaction.HashSetAsync` with `When.Always`, `HashGetAsync(RedisKey, RedisValue)`, `AddCondition(Condition.HashEqual(...))`, `AddCondition(Condition.HashNotExists(...))`, `ExecuteAsync()`, `ConditionResult.WasSatisfied` after abort | Implemented by `CompatTransaction`, `CompatDatabaseAsync`, and `CompatCondition` |
| [RedisGatewayListProvider](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Clustering.Redis/Providers/RedisGatewayListProvider.cs) | No direct Redis interface calls; invokes membership initialization and `ReadAll()` | Uses the membership surface above |
| [RedisGrainStorage](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Persistence.Redis/Storage/RedisGrainStorage.cs) | `IDatabase.HashGetAllAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` for write, clear, and delete-on-clear | Implemented, including binary payloads, atomic ETag comparison, and optional expiry |
| [RedisReminderTable](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Reminders.Redis/Storage/RedisReminderTable.cs) | `IDatabase.KeyExpireAsync(RedisKey, TimeSpan?)`, `KeyDeleteAsync(RedisKey)`, `SortedSetRangeByValueAsync(RedisKey, RedisValue, RedisValue, Exclude, long, long, CommandFlags)`, `SortedSetRemoveRangeByValueAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` | Implemented, including atomic replacement, lexicographic ranges, and ETag-conditional deletion |

The hosting extensions and provider builders set the factory/options; they do
not add database operations. A configured `ServiceKey` supplies a shared
DI-registered multiplexer through that same factory. None of these providers calls `IBatch` or
`ISubscriber`, `GetSubscriber`, server discovery, profiling, or
`ChannelMessageQueue`. The SignalR queue limitation is unrelated.

## Implemented persistence and reminder behavior

The three gaps identified by the original assessment are now implemented in
`src/Respire.StackExchangeCompat/CompatDatabaseAsync.Orleans.cs`:

1. `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)`.
   Persistence needs atomic ETag compare-and-write, compare-and-clear, and
   compare-and-delete, including serialized binary state, integer results, and
   optional expiry. Reminders use atomic lexicographic removal followed by
   `ZADD` and an array reply. The adapter sends the original script, keys, and
   binary arguments to Redis in one `EVAL`; it does not split writes or emulate
   ETag comparison locally.
2. `SortedSetRangeByValueAsync` without an explicit `Order` parameter.
   Reminders use inclusive lexicographic bounds for a grain, a named reminder,
   and both ordinary and wrapped hash-ring ranges. The adapter uses
   `ZRANGEBYLEX`, preserving inclusive/exclusive and null/open bounds. The
   explicit `Order` overload also supports descending reads and pagination.
3. `SortedSetRemoveRangeByValueAsync`.
   Reminder deletion uses a lexicographic interval containing the grain,
   reminder name, and ETag; `ZREMRANGEBYLEX` ensures a stale ETag cannot remove
   a replacement reminder.

Synchronous counterparts and deferred batch/transaction dispatch also support
these commands. The adapter copies binary keys, values, and bounds before queueing.
See the [adapter guide](./stackexchange-interop) for script overload limitations,
SHA-string dispatch, reply conversion, and flags. There are no additional
unsupported members or option/flag branches in the in-scope source inventory.

## Real Redis evidence and decision

`tests/Respire.Orleans.Acceptance.Tests` runs named upstream scenarios with the
pinned production providers, first against StackExchange.Redis as a positive
control, then against the shim. The test project records its upstream sources,
adaptations, and exclusions in `NOTICE.md`.

All **18 cases** require successful provider behavior from both clients:

- Three clustering cases cover insert/read, stale versions and duplicate
  inserts, update/heartbeat/cleanup, and active gateway filtering.
- Nine persistence cases cover write/read, both clear modes, stale/null ETag
  rejection, concurrent writers with one winner, optional expiry, binary state,
  and bidirectional storage interoperability with StackExchange.Redis.
- Six reminder cases cover upsert/read, stale/current/repeated removal,
  bidirectional interoperability, multiple grains/names, exact ordinary/wrapped
  hash-ring intervals, and concurrent upserts leaving one replacement.

No provider operation falls back to StackExchange.Redis. Interoperability tests
deliberately use the other client to read/update the same provider namespace,
then verify ETags, payloads, and deletion from the first client. Separate adapter
tests compare lexicographic bounds/pagination/removal with StackExchange.Redis
and cover Lua reply types, script hashes/errors, and queued argument ownership.

Local validation passed all 18 cases in each .NET 8/.NET 10 and RESP2/RESP3
combination (72 case executions). The NuGet repository metadata for all three
packages matches the source commit above. These results apply to standalone
Redis 7.0.15, not Redis Cluster or failover.

Run the 18 cases on .NET 8 and .NET 10 with RESP2 and RESP3:

```powershell
$env:RESPIRE_TEST_PROTOCOL = '2' # Repeat with '3'.
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'run', '--project', 'tests/Respire.Orleans.Acceptance.Tests',
    '-c', 'Release', '-f', 'net10.0', '--', '--minimum-expected-tests', '18')
# Repeat with net8.0 as well.
```

The adapter delivers the missing atomic persistence and reminder behavior for
[#890](https://github.com/thomhurst/Respire/issues/890), preserving the official
providers and their storage format. No native provider is needed for the assessed
calls. Full silo/grain activation, all storage datasets/serializers, provider-owned
shutdown, sustained parallel stress, Cluster routing, reconnect, and failover
remain unvalidated. The expiry case checks the written key's positive TTL and
payload; it does not wait for wall-clock expiration. Binary tests use Orleans'
binary serializer with byte arrays and an activatable byte list for clear.

Grain directory, streaming, and journaling providers are outside this assessment.
No compatibility claim is made for those packages or other Orleans versions.
