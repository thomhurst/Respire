---
title: Hangfire compatibility acceptance
description: Run Hangfire.Redis.StackExchange on Respire through the StackExchange.Redis adapter, and what is tested.
---

# Hangfire compatibility acceptance

Run `Hangfire.Redis.StackExchange` **1.12.0** on Respire by injecting the
[StackExchange.Redis adapter](stackexchange-interop.md) into `RedisStorage`. Tested against real Redis
on .NET 8 and .NET 10 with RESP2 and RESP3 (package commit
[`da8e39a`](https://github.com/marcoCasamento/Hangfire.Redis.StackExchange/tree/da8e39a33df204900afc30aeb65110f76f081c55)).

```bash
dotnet add package Respire.StackExchangeCompat
```

```csharp
using Respire.StackExchangeCompat;

// options is your RespireOptions, for example RespireOptions.Parse("localhost:6379").
await using var multiplexer = RespireConnectionMultiplexer.Create(options);
var storage = new Hangfire.Redis.StackExchange.RedisStorage(multiplexer,
    new Hangfire.Redis.StackExchange.RedisStorageOptions
    {
        Db = options.Database,
        UseTransactions = true,
    });
```

- Keep the multiplexer alive until Hangfire workers and storage operations finish. Hangfire borrows it; the adapter owns its native clients.
- Pass the multiplexer, not a connection string. A connection string makes `RedisStorage` create StackExchange.Redis instead.

## What is tested

`tests/Respire.Hangfire.Acceptance.Tests` ports 93 of the 96 active upstream tests, plus two worker lifecycle tests.

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

- Transaction, fetched-job, watcher, and lock tests run on Redis. Argument-validation and state-handler tests use upstream mocks, so they do not prove Redis behavior.
- The worker lifecycle tests run a real `BackgroundJobServer` with `UseTransactions` both `true` and `false`: enqueue, execute, check the succeeded state, and shut down.
- The [source notice](https://github.com/thomhurst/Respire/blob/main/tests/Respire.Hangfire.Acceptance.Tests/Upstream/NOTICE.md) lists each method, adaptation, and the upstream LGPLv3 notices.

Excluded:

- `DbFromConnectionStringIsUsed`, `PasswordFromToStringIsNotShown`, and `PasswordFromWriteOptionsToLogIsNotShown` use string constructors that create StackExchange.Redis, so they cannot test the adapter.
- `AcquireFromNestedTask` is disabled upstream (the package uses thread-local lock ownership).
- The upstream expired-job deletion test writes the misspelled key `succeded`, so it does not prove cleanup of the `succeeded` list.

## Limitations

- Supported members are listed in the [migration guide](stackexchange-interop.md). Unsupported members and flags throw `NotSupportedException`.
- Not covered: other Hangfire package versions, Redis Cluster or Sentinel, every dashboard query, every Hangfire feature, and general StackExchange.Redis parity.
- `ListRightPop`, `HashExists`, `RespireSortedSetRankResult`, `SetContains`, and newer `StringSet` overloads appear only in upstream test setup and are not part of the supported surface unless the migration guide lists them.
