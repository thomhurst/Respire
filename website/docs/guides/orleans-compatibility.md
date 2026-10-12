# Orleans Redis provider compatibility

`Respire.StackExchangeCompat` **does not currently cover all three Orleans Redis
providers**. Orleans can inject the adapter without a fork, but grain persistence
and reminders require operations that the adapter explicitly rejects.

This assessment pins these packages to **10.2.2**, from Orleans tag `v10.2.2`,
commit [`a575888773a3b01b079c9c3720bb1553c3169997`](https://github.com/dotnet/orleans/tree/a575888773a3b01b079c9c3720bb1553c3169997):

- `Microsoft.Orleans.Clustering.Redis`
- `Microsoft.Orleans.Persistence.Redis`
- `Microsoft.Orleans.Reminders.Redis`

This is an assessed version, not a blanket supported-version declaration.

The acceptance project pins all three providers to **10.2.2** by updating its
central `PackageVersion` items. Repository defaults are **10.4.0**, but the
project pins preserve the provider and runtime versions used by this source
inventory and its recorded results. Renovate updates the repository defaults
together and excludes the assessment pins. Version **10.4.0** is not assessed
here; moving the pins requires a new source inventory and compatibility assessment.

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
| [RedisGrainStorage](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Persistence.Redis/Storage/RedisGrainStorage.cs) | `IDatabase.HashGetAllAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` for write, clear, and delete-on-clear | Hash reads implemented; all three script paths unsupported |
| [RedisReminderTable](https://github.com/dotnet/orleans/blob/a575888773a3b01b079c9c3720bb1553c3169997/src/Redis/Orleans.Reminders.Redis/Storage/RedisReminderTable.cs) | `IDatabase.KeyExpireAsync(RedisKey, TimeSpan?)`, `KeyDeleteAsync(RedisKey)`, `SortedSetRangeByValueAsync(RedisKey, RedisValue, RedisValue, Exclude, long, long, CommandFlags)`, `SortedSetRemoveRangeByValueAsync`, `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)` | Expiry/delete implemented; lexicographic reads/removals and script upserts unsupported |

The hosting extensions and provider builders set the factory/options; they do
not add database operations. A configured `ServiceKey` supplies a shared
DI-registered multiplexer through that same factory. None of these providers calls `IBatch` or
`ISubscriber`, `GetSubscriber`, server discovery, profiling, or
`ChannelMessageQueue`. The SignalR queue limitation is unrelated.

## Complete gap list for this inventory

These three members throw `NotSupportedException` in
`src/Respire.StackExchangeCompat/CompatDatabaseAsync.Unsupported.g.cs`:

1. `ScriptEvaluateAsync(string, RedisKey[], RedisValue[], CommandFlags)`.
   Persistence needs atomic ETag compare-and-write, compare-and-clear, and
   compare-and-delete, including serialized binary state, integer results, and
   optional expiry. Reminders need an atomic lexicographic removal followed by
   `ZADD` and an array reply. Lock-specific Lua support does not implement this
   general script entry point. Ordinary hash writes cannot replace its atomicity.
2. `SortedSetRangeByValueAsync` without an explicit `Order` parameter.
   Reminders use inclusive lexicographic bounds for a grain, a named reminder,
   and both ordinary and wrapped hash-ring ranges. Score/rank range support
   cannot substitute for lexicographic ordering.
3. `SortedSetRemoveRangeByValueAsync`.
   Reminder deletion uses a lexicographic interval containing the grain,
   reminder name, and ETag; a stale ETag must not remove a replacement reminder.

There are no additional unsupported members or option/flag branches in the
in-scope source inventory. That source assessment does not establish behavior
for untested runtime conditions.

## Real Redis evidence and decision

`tests/Respire.Orleans.Acceptance.Tests` runs named upstream scenarios with the
pinned production providers, first against StackExchange.Redis as a positive
control, then against the shim. The test project records its upstream sources,
adaptations, and exclusions in `NOTICE.md`.

Three clustering scenarios cover insert/read, stale versions and duplicate
inserts, update/heartbeat/cleanup, and active gateway filtering. Six diagnostic
cases confirm the exact unsupported members in persistence write and both clear
modes, reminder upsert, lexicographic read, and lexicographic removal. The control
client seeds state for clear/read/removal diagnostics so the earlier unsupported
write does not hide another gap. Successful diagnostic tests confirm failures;
they do not mean persistence or reminders pass conformance.

Local validation passed all nine cases in each .NET 8/.NET 10 and RESP2/RESP3
combination (36 case executions). The NuGet repository metadata for all three
packages matches the source commit above. These results apply to standalone
Redis 7.0.15, not Redis Cluster or failover.

Run the nine cases on .NET 8 and .NET 10 with RESP2 and RESP3:

```powershell
$env:RESPIRE_TEST_PROTOCOL = '2' # Repeat with '3'.
& scripts/Invoke-AgentDotNet.ps1 -SingleNode -DotNetArguments @(
    'run', '--project', 'tests/Respire.Orleans.Acceptance.Tests',
    '-c', 'Release', '-f', 'net10.0', '--', '--minimum-expected-tests', '9')
# Repeat with net8.0 as well.
```

The current shim is insufficient for combined Orleans clustering, persistence,
and reminders. [#890](https://github.com/thomhurst/Respire/issues/890) remains
justified for the missing atomic persistence and reminder behavior; no native
membership replacement is justified by this inventory. Extending the shim's
three missing methods is also a possible solution, but requires successful
upstream acceptance before removing that need. No native provider is delivered
by this assessment.

Grain directory, streaming, and journaling providers are outside this assessment.
No compatibility claim is made for those packages or other Orleans versions.
