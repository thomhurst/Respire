---
title: Use Respire with Hangfire
description: Run Hangfire.Redis.StackExchange on Respire through the StackExchange.Redis adapter.
---

# Use Respire with Hangfire

`Hangfire.Redis.StackExchange` runs on Respire unchanged. Pass the
[StackExchange.Redis adapter](stackexchange-interop.md) to `RedisStorage` and Hangfire uses Respire
for every storage operation. Tested with version **1.12.0** on .NET 10 and .NET 11, over RESP2 and RESP3.

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

- Pass the multiplexer, not a connection string. A connection string makes `RedisStorage` create a StackExchange.Redis connection instead.
- Keep the multiplexer alive until Hangfire workers and storage operations finish. Hangfire borrows it; the adapter owns its native clients.

## What works

- Enqueueing, fetching, and executing jobs with a real `BackgroundJobServer`, with `UseTransactions` set to `true` or `false`.
- Write-only transactions, fetched-job acknowledgement and requeue, distributed locks, and pub/sub job notifications.
- The fetched-jobs and expired-jobs watchers.
- Job state transitions (succeeded, failed, processing, deleted).

## Limitations

- Tested with standalone Redis only. Redis Cluster and Sentinel are not covered.
- Tested with Hangfire.Redis.StackExchange 1.12.0. Check other versions before upgrading.
- Not every dashboard query has been exercised.
- The adapter implements the members listed in the [adapter guide](stackexchange-interop.md). Anything else throws `NotSupportedException`.
