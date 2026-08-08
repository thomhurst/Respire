---
title: Strings and keys
description: Read, write, expire, scan, and manage Redis keys.
---

# Strings and keys

Frequent operations are available directly on `IRespireClient`; complete string and key surfaces live under `Strings` and `Keys`.

## Read and write

```csharp
await redis.SetAsync("greeting", "hello");
string? greeting = await redis.GetStringAsync("greeting");

await redis.SetAsync("visits", 1);
long visits = await redis.IncrementAsync("visits");
```

Conditional writes use `SetWhen`:

```csharp
bool created = await redis.SetAsync(
    "lock:invoice:42",
    requestId,
    expiry: TimeSpan.FromSeconds(20),
    when: SetWhen.NotExists);
```

## Bulk operations

```csharp
await redis.Strings.SetManyAsync(
    ("feature:a", "on"),
    ("feature:b", "off"));

string?[] values = await redis.Strings.GetManyAsync("feature:a", "feature:b");
long removed = await redis.DeleteAsync("feature:a", "feature:b");
```

Variadic APIs use `params ReadOnlySpan<T>` where possible, avoiding a params-array allocation on supported C# toolchains.

## Key lifetime

```csharp
await redis.ExpireAsync("session:42", TimeSpan.FromMinutes(30));
RespireExpiry ttl = await redis.Keys.ExpiryAsync("session:42");

await redis.Keys.PersistAsync("session:42");
await redis.Keys.ExpireAtAsync("report", DateTimeOffset.UtcNow.AddDays(1));
```

## Scan safely

`ScanAsync` manages Redis cursors and returns an async stream:

```csharp
await foreach (string key in redis.Keys.ScanAsync(
    match: "session:*",
    pageSize: 500,
    cancellationToken: stoppingToken))
{
    await InspectAsync(key);
}
```

Prefer `SCAN` over `KEYS` in production; each page yields control and avoids a single server-blocking sweep.

## Key-prefixed views

Create a lightweight client view when one service or tenant needs a namespace:

```csharp
IRespireClient tenant = redis.WithKeyPrefix("tenant:42:");
await tenant.SetAsync("settings", json); // tenant:42:settings
```
