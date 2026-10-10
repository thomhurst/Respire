---
title: Hangfire compatibility acceptance
description: Pinned upstream tests, worker lifecycle, supported contracts, and exclusions for the StackExchange.Redis shim.
---

# Hangfire compatibility acceptance

The [StackExchange.Redis migration boundary](./stackexchange-interop) is verified with
`Hangfire.Redis.StackExchange` **1.12.0**. Its NuGet repository commit is
[`da8e39a33df204900afc30aeb65110f76f081c55`](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55).
The acceptance project executes that published package against real Redis through
`RespireConnectionMultiplexer`, on .NET 8 and .NET 10 with RESP2 and RESP3.

Inject the adapter into `RedisStorage`; construct and configure it using native Respire options:

```csharp
using Respire.StackExchangeCompat;

await using var multiplexer = RespireConnectionMultiplexer.Create(options);
var storage = new Hangfire.Redis.StackExchange.RedisStorage(multiplexer,
    new Hangfire.Redis.StackExchange.RedisStorageOptions
    {
        Db = options.Database,
        UseTransactions = true,
    });
```

Keep the multiplexer alive until Hangfire workers and storage operations have finished.
The adapter owns its native clients. Hangfire borrows the injected multiplexer.
Passing a connection string directly to `RedisStorage` creates StackExchange.Redis instead.

## Upstream tests exercised

`tests/Respire.Hangfire.Acceptance.Tests` ports **93 of 96 active upstream facts**,
preserving their method names and behavioral assertions. The subscription timeout keeps
its minimum wait assertion without a scheduling ceiling. Lock-thread failures are reported
on the test thread after joining, with waiters released even when a callback fails.
An independent StackExchange.Redis connection
arranges and checks Redis state. Production storage, connections, fetched jobs, write
transactions, locks, and subscriptions receive the shim. Test-only control calls do not
extend the supported adapter interface. The package's existing friend assembly grant
allows the port to exercise its internal types without modifying production code.

| Upstream class | Facts exercised |
| --- | ---: |
| `RedisConnectionFacts` | 18 |
| `RedisWriteOnlyTransactionFacts` | 25 |
| `RedisFetchedJobFacts` | 16 |
| `RedisLockFacts` | 4 |
| `FetchedJobsWatcherFacts` | 7 |
| `ExpiredJobsWatcherFacts` | 5 |
| `RedisSubscriptionFacts` | 3 |
| `RedisStorageFacts` | 1 |
| `RedisStorageOptionsFacts` | 1 |
| `RedisTest` | 1 |
| `DeletedStateHandlerFacts` | 3 |
| `FailedStateHandlerFacts` | 3 |
| `ProcessingStateHandlerFacts` | 3 |
| `SucceededStateHandlerFacts` | 3 |

The [source notice and exact method inventory](https://github.com/thomhurst/Respire/blob/main/tests/Respire.Hangfire.Acceptance.Tests/Upstream/NOTICE.md)
record adaptations, provenance, and the upstream LGPLv3 notices. Argument validation
and state-handler callback tests retain upstream mocks; their results do not independently
prove Redis behavior. Transaction, fetched-job, watcher, and lock scenarios operate on Redis.

The three excluded `RedisStorageFacts` tests are `DbFromConnectionStringIsUsed`,
`PasswordFromToStringIsNotShown`, and `PasswordFromWriteOptionsToLogIsNotShown`.
They exercise string constructors that create StackExchange.Redis internally, so they
cannot validate the injected adapter. The upstream `AcquireFromNestedTask` lock test is
already disabled and remains disabled because the package uses thread-local lock ownership.
It is not included in the 96 active facts.
The upstream expired-job deletion test writes the misspelled key `succeded`;
its unchanged assertion does not prove cleanup of the `succeeded` list.

## Additional production acceptance

Two additional cases start a real `BackgroundJobServer`, enqueue and execute a stored job,
then shut down the worker. They verify the succeeded state through the package and an
independent Redis read, removal from the dequeued list, server removal, the succeeded
monitoring entry, and continued use of the borrowed multiplexer. Both
`UseTransactions = true` and `false` are exercised. Each protocol/framework run therefore
executes **95 cases**, with no skipped cases. CI runs all four combinations and saves TRX reports.

`Respire.StackExchangeCompat.Tests.TransactionTests` additionally verifies real MULTI/EXEC
atomic visibility, mismatched conditions and WATCH races that cancel every queued task,
runtime WRONGTYPE errors that fault only their command while surrounding writes commit,
and ACL errors during queueing that discard every write and leave repeat execution usable.
Its cancellation and shutdown controls verify that queued and executing tasks settle.
`ServerLockTests` verifies RedisStorage discovery, INFO/TIME/ROLE, competing token locks,
extension/release ownership, server script errors, and borrowed-client disposal.

## Supported scope and limitations

The supported command, transaction, condition, server, lock, and subscription contracts
are listed in the [migration guide](./stackexchange-interop). This acceptance verifies
the pinned package's upstream scenarios and basic worker lifecycle on standalone Redis;
it does not establish arbitrary StackExchange.Redis interface parity, other Hangfire package
versions, Redis Cluster/Sentinel deployments, every dashboard query, or every Hangfire feature.
Unsupported members and flags still throw `NotSupportedException`; they never return a
successful default to bypass an integration call. `ListRightPop`, `HashExists`, `SortedSetRank`,
`SetContains`, and newer `StringSet` overloads used only by upstream test setup/assertions
remain outside this contract unless separately documented in the migration guide.

This Hangfire-specific evidence does not close
[#889](https://github.com/thomhurst/Respire/issues/889), which requires independent combined
Microsoft/SignalR/Hangfire acceptance. The parent
[#1269](https://github.com/thomhurst/Respire/issues/1269) retains its complete acceptance gate.
