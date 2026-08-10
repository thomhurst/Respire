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

## Connection-time failover

Use `ConnectAnyAsync` when an application can connect to one of several independent Redis deployments and should try them in priority order at startup:

```csharp
await using var redis = await RespireClient.ConnectAnyAsync([
    new RespireOptions
    {
        Endpoints = { new RespireEndpoint("redis-primary.internal", 6379) },
        Username = configuration["Redis:Username"],
        Password = configuration["Redis:Password"],
        ConnectTimeout = TimeSpan.FromSeconds(3),
    },
    new RespireOptions
    {
        Endpoints = { new RespireEndpoint("redis-secondary.internal", 6379) },
        Username = configuration["Redis:Username"],
        Password = configuration["Redis:Password"],
        ConnectTimeout = TimeSpan.FromSeconds(3),
    },
]);
```

Each candidate keeps its full `RespireOptions`, including TLS, authentication, timeout, cluster, and serializer settings. Respire tries candidates in order, disposes failed partial clients, and returns the first connected client. If every candidate fails, `ConnectAnyAsync` throws `RespireConnectionException` with each attempt in the message and an aggregate inner exception.

This is connection-time fallback only. After a client is returned, commands run against that selected deployment and use Respire's normal reconnect behavior. `ConnectAnyAsync` is not a health-checked circuit breaker and does not continuously route commands between independent deployments.

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
    AllowAdmin = false,
    LoggerFactory = loggerFactory,
};

await using var redis = await RespireClient.ConnectAsync(options);
```

`Connections = 0` uses one multiplexed connection, the default. Raise the fixed pool size only when profiling shows one socket is saturated.

`AllowAdmin = false` is the default safety setting. Set it to `true` only for callers that are allowed to run high-risk server administration commands such as `FLUSHDB`, `FLUSHALL`, and `CONFIG SET`.

## URI query options

Connection URI query parameters cover common options:

```text
redis://localhost:6379/0?clientName=checkout-api&connections=4&allowAdmin=false
```

Supported query parameters are `clientName`, `connections`, `connectTimeoutMs`, `commandTimeoutMs`, `responseTimeoutMs`, `protocol` (`2` or `3`), `db`, `cluster`, and `allowAdmin`.

## Lazy creation

Applications that must start before Redis can use `Create`:

```csharp
await using var redis = RespireClient.Create(options);
```

The first command triggers connection. Dependency-injection registration uses this lazy behavior so Redis availability does not block host startup.

## Cancellation and timeouts

Commands with a `CancellationToken` abandon the wait when cancelled; cancellation cannot guarantee the server did not execute a command already written to the socket. Some variadic `params ReadOnlySpan<T>` overloads do not accept cancellation.

Likewise, a `RespireTimeoutException` means the response did not arrive within `CommandTimeout`. Treat writes as potentially executed and design retries around operation idempotency.

## Connection state

`IsConnected` reports current availability. Subscribe to `ConnectionStateChanged` when a health surface needs transition events:

```csharp
redis.ConnectionStateChanged += state =>
    logger.LogInformation("Redis connection is {State}", state);
```

Respire reconnects failed connections in the background. Pub/sub subscriptions reconnect and resubscribe automatically.

:::note TLS

Use `rediss://` to enable TLS. Portless `redis://` and `rediss://` URIs both use Redis's standard port, `6379`; specify an explicit port when your provider uses another one.

:::
