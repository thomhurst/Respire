---
title: Connections and options
description: Configure endpoints, timeouts, reconnects, and connection lifecycle.
---

# Connections and options

Use a URI for the common case or `RespireOptions` when the connection needs explicit control.

## Connect immediately

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379/0");
```

`ConnectAsync` establishes connections before returning. An unreachable server produces `RespireConnectionException` instead of deferring failure to an unrelated command.

## Full configuration

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.internal", 6379) },
    Username = configuration["Redis:Username"],
    Password = configuration["Redis:Password"],
    Database = 0,
    ClientName = "checkout-api",
    ConnectTimeout = TimeSpan.FromSeconds(5),
    CommandTimeout = TimeSpan.FromSeconds(2),
    Connections = 4,
    LoggerFactory = loggerFactory,
};

await using var redis = await RespireClient.ConnectAsync(options);
```

`Connections = 0` uses one multiplexed connection, the default. Raise the fixed pool size only when profiling shows one socket is saturated.

## Lazy creation

Applications that must start before Redis can use `Create`:

```csharp
await using var redis = RespireClient.Create(options);
```

The first command triggers connection. Dependency-injection registration uses this lazy behavior so Redis availability does not block host startup.

## Cancellation and timeouts

Each command accepts a `CancellationToken`. Cancellation abandons the wait; it cannot guarantee the server did not execute a command already written to the socket.

Likewise, a `RespireTimeoutException` means the response did not arrive within `CommandTimeout`. Treat writes as potentially executed and design retries around operation idempotency.

## Connection state

`IsConnected` reports current availability. Subscribe to `ConnectionStateChanged` when a health surface needs transition events:

```csharp
redis.ConnectionStateChanged += state =>
    logger.LogInformation("Redis connection is {State}", state);
```

Respire reconnects failed connections in the background. Pub/sub subscriptions reconnect and resubscribe automatically.

:::note Current transport limits

Only plain `redis://` endpoints are supported. `rediss://` throws `NotSupportedException`; TLS is on the [roadmap](../roadmap).

:::
