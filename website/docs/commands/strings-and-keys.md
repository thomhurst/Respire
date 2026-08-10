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

## Expiry

`RespireExpiry` is the single expiry argument: nothing, a relative TTL, an absolute instant, or "keep the TTL the key already has". A `TimeSpan` or `DateTimeOffset` converts implicitly.

```csharp
await redis.SetAsync("session:42", token);                                  // no TTL (clears any existing one)
await redis.SetAsync("session:42", token, TimeSpan.FromMinutes(30));        // PX
await redis.SetAsync("session:42", token, RespireExpiry.At(midnight));         // PXAT
await redis.SetAsync("session:42", token, RespireExpiry.Keep);                 // KEEPTTL
```

`RespireExpiry` is the expiry you *send*; `RespireTtl` (returned by `Keys.ExpiryAsync`) is the expiry Redis *reports*.

## Bulk operations

```csharp
await redis.Strings.SetManyAsync(
    ("feature:a", "on"),
    ("feature:b", "off"));

// One shared expiry (and optional NX/XX) for every pair — Redis MSETEX.
await redis.Strings.SetManyExpireAsync(
    TimeSpan.FromMinutes(5),
    SetWhen.NotExists,
    ("feature:a", "on"),
    ("feature:b", "off"));

string?[] values = await redis.Strings.GetManyAsync("feature:a", "feature:b");
long removed = await redis.DeleteAsync("feature:a", "feature:b");
```

Variadic APIs use `params ReadOnlySpan<T>` where possible, avoiding a params-array allocation on supported C# toolchains. Because a `params` parameter must come last, each of these has a sibling overload taking the items non-params plus a required `CancellationToken`:

```csharp
string?[] values = await redis.Strings.GetManyAsync(keys, cancellationToken);
long removed = await redis.DeleteAsync(keys, cancellationToken);
```

## Key lifetime

```csharp
await redis.ExpireAsync("session:42", TimeSpan.FromMinutes(30));
RespireTtl ttl = await redis.Keys.ExpiryAsync("session:42");

await redis.Keys.ExpireAsync("session:42", RespireExpiry.Persist);
await redis.Keys.ExpireAsync("report", RespireExpiry.At(DateTimeOffset.UtcNow.AddDays(1)));
await redis.Keys.ExpireAsync("lease", TimeSpan.FromMinutes(10), ExpireWhen.GreaterThan);

var value = await redis.Strings.GetExpireAsync("session:42", TimeSpan.FromMinutes(30));
```

`TypeAsync` returns `RespireKeyType` rather than a server string. Conditional rename and copy are
available without dropping to raw commands:

```csharp
RespireKeyType type = await redis.Keys.TypeAsync("session:42");
bool renamed = await redis.Keys.TryRenameAsync("draft", "published");
bool copied = await redis.Keys.CopyAsync("template", "working-copy", replace: true);
```

## Scan safely

`ScanAsync` manages Redis cursors and returns an async stream:

```csharp
await foreach (string key in redis.Keys.ScanAsync(
    match: "session:*",
    countHint: 500,
    type: RespireKeyType.Hash,
    cancellationToken: stoppingToken))
{
    await InspectAsync(key);
}
```

`countHint` maps to Redis `COUNT`; it guides work per iteration but does not guarantee page size.
Prefer `SCAN` over `KEYS` in production; each page yields control and avoids a single server-blocking sweep.

## Key-prefixed views

Create a lightweight client view when one service or tenant needs a namespace:

```csharp
IRespireClient tenant = redis.WithKeyPrefix("tenant:42:");
await tenant.SetAsync("settings", json); // tenant:42:settings
```
