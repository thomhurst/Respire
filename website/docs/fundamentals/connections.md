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

Omitting `Connections` uses one multiplexed connection, the default. The value must be at least one; raise the fixed pool size only when profiling shows one socket is saturated.

`AllowAdmin = false` is the default safety setting. Set it to `true` only for callers that are allowed to run high-risk server administration commands such as `FLUSHDB`, `FLUSHALL`, and `CONFIG SET`.

## URI query options

Connection URI query parameters cover common options:

```text
redis://localhost:6379/0?clientName=checkout-api&connections=4&allowAdmin=false
```

Supported query parameters are `clientName`, `connections`, `connectTimeoutMs`, `commandTimeoutMs`, `responseTimeoutMs`, `protocol` (`2`/`resp2` or `3`/`resp3`, case-insensitive), `db`, `cluster`, and `allowAdmin`.

Unsupported protocol values and malformed or overflowing integer options throw `ArgumentException`
with the option name and `ParamName == "connectionString"`. This applies to URI and comma-delimited
connection strings. Existing option range checks still apply after parsing.

## StackExchange.Redis connection strings

Respire also accepts the common comma-delimited format, which eases migration from
StackExchange.Redis:

```text
cache-a:6380,password=secret,ssl=true,defaultDatabase=2
```

Multiple endpoints require an explicit deployment mode:

| Configuration | Meaning |
| --- | --- |
| `cache-a,cache-b,cluster=true` | Redis Cluster seeds, tried during connection setup; discovered slot owners and redirects route later commands. |
| `sentinel-a,sentinel-b,serviceName=mymaster` | Sentinel discovery endpoints, tried until a reachable primary is found at startup. |
| `cache-a,cache-b` | Rejected: the endpoints could belong to unrelated standalone deployments. |

Use `ConnectAnyAsync` with separate `RespireOptions` candidates for connection-time fallback
between independent deployments. It does not perform continuous geographic failover. Sentinel
currently discovers the primary at startup; it does not automatically discover a replacement
primary later. Ordinary reconnection targets the deployment already selected. Cluster routing
is distinct from either standalone fallback or Sentinel discovery.

Programmatic `RespireOptions.Endpoints` follows the same rule: standalone mode requires one
endpoint. Lists that previously left extra standalone endpoints unused now fail validation.
Cluster and Sentinel cannot both be selected in one comma-delimited string.

Supported options are `user` (or `username`), `password`, `ssl`, `sslHost`, `sslProtocols`,
`checkCertificateRevocation`, `clientName` (or `name`), `defaultDatabase` (or `db`),
`connectTimeout`, `asyncTimeout` (or `syncTimeout`), `protocol` (`resp2` or `resp3`), and
`allowAdmin`. `sslHost` sets the TLS certificate/SNI target and enables TLS unless
`ssl=false` explicitly disables it, regardless of option order. `sslProtocols`
accepts pipe-separated enum names, such as `Tls12|Tls13`, or numeric masks combining defined
protocol bits, such as `15360`. `sslProtocols` and `checkCertificateRevocation` configure TLS
settings but do not enable TLS by themselves; those settings have no effect on a plaintext
connection. Use `ssl=true` or `sslHost` without `ssl=false` to enable TLS. Boolean options accept `true` or `false`
(case-insensitive); other values throw `ArgumentException`. Existing password splitting and
async-timeout precedence stay unchanged.

In Cluster mode, an explicit `sslHost` applies the same certificate/SNI target to every seed
and discovered node. Use it only when every node certificate covers that shared name; omit it
to validate each connection's own hostname.

Mode options are `cluster` (or `useCluster`) and `serviceName` (or `sentinelPrimaryName`).
Sentinel also accepts `sentinelUser`, `sentinelPassword`, `sentinelTls`, and `sentinelSslHost`; an empty
`sentinelPassword=` disables inherited authentication. Omitted ports default to 26379 in
Sentinel mode and retain Respire's existing 6379 default otherwise, including TLS. Explicit
ports always take precedence.

Set `sentinelSslHost=sentinel.example` when Sentinel certificates use a different hostname from
the primary's `sslHost`. This overrides only the Sentinel TLS target; protocol and revocation
settings remain inherited. It enables Sentinel TLS unless `sentinelTls=false` explicitly
disables it. Without `sentinelSslHost`, Sentinel inherits the primary TLS settings.

`sslHost`, `sentinelSslHost`, `sslProtocols`, and `checkCertificateRevocation` are options for
the comma-delimited format, not URI query parameters. With `rediss://`, configure `TlsOptions`
and `SentinelTlsOptions` on the parsed options in code when you need these overrides.

Unknown or unsupported options still throw `ArgumentException`, catching spelling mistakes.
For example, StackExchange.Redis `keepAlive` sends protocol messages; it is not equivalent to
Respire's TCP keepalive settings. Configure `TcpKeepAliveTime` directly when kernel probes are
wanted. Connection groups and continuous failover require application-level policy; an endpoint
list never silently enables them. See the [StackExchange.Redis option reference](https://seredis.dev/Configuration.html)
for its original option semantics, and use `RespireOptions` directly for Respire-only settings.

Bare IPv6 endpoints use the default Redis port. Add brackets when specifying a port: `::1` or
`[::1]:6380`.

## Lazy creation

Applications that must start before Redis can use `Create`:

```csharp
await using var redis = RespireClient.Create(options);
```

The first command triggers connection. Dependency-injection registration uses this lazy behavior so Redis availability does not block host startup.

## Redis Sentinel

Set `SentinelPrimaryName` to resolve the current primary from one or more Sentinel endpoints before
connecting:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("sentinel-1", 26379) },
    SentinelPrimaryName = "mymaster",
    Password = configuration["Redis:Password"],
    SentinelPassword = configuration["Redis:SentinelPassword"],
});
```

URI connections use `serviceName`, `sentinelUser`, `sentinelPassword`, and `sentinelTls` query
parameters:

```csharp
await using var redis = await RespireClient.ConnectAsync(
    "redis://:redis-password@sentinel-1?serviceName=mymaster&sentinelPassword=sentinel-password");
```

By default, Sentinel authentication inherits the primary Redis credentials. Set
`SentinelPassword = string.Empty`, or include an empty `sentinelPassword=` URI parameter, to
explicitly disable Sentinel authentication while retaining authentication on the discovered
primary. When multiple Sentinel endpoints are configured, Respire also tries the next endpoint
if discovery times out, returns invalid data, or reports a primary that cannot be reached during
the initial connection.

Sentinel discovery always uses RESP2, so older Sentinel nodes can discover a RESP3 primary.
Transport settings inherit from the primary by default. Set `SentinelUseTls` independently when
Sentinel and the primary use different TLS modes, and set `SentinelTlsOptions` when Sentinel needs
different certificate validation or a different `TargetHost`. In a URI, `sentinelTls=false`
selects plaintext Sentinel discovery even when the `rediss://` primary uses TLS.

Sentinel currently requires `ConnectAsync` because discovery is a network operation that must run
before Redis connections exist. Lazy `Create` and automatic Sentinel re-discovery during failover
are planned follow-up work.

## Cancellation and timeouts

Commands with a `CancellationToken` abandon the wait when cancelled; cancellation cannot guarantee the server did not execute a command already written to the socket. A `params` parameter must come last, so variadic `params ReadOnlySpan<T>` commands carry their token on a sibling overload that takes the items non-params followed by a required token — `DeleteAsync(keys)` for the convenient form, `DeleteAsync(keys, cancellationToken)` when you need cancellation.

Likewise, a `RespireTimeoutException` means the response did not arrive within `CommandTimeout`. Treat writes as potentially executed and design retries around operation idempotency.

Redis error replies throw `RespireServerException`. Its `Code` identifies the Redis error,
`CommandName` identifies the originating command when available, and `IsTransient` classifies
`LOADING`, `BUSY`, `CLUSTERDOWN`, `TRYAGAIN`, and `MASTERDOWN`. Use `RespireErrorCodes` instead of
string literals when building retry policies.

## Connection state

`IsConnected` reports current availability. Subscribe to `ConnectionStateChanged` when a health surface needs transition events:

```csharp
redis.ConnectionStateChanged += change =>
    logger.LogInformation(
        change.Error,
        "Redis endpoint {Endpoint} is {State}",
        change.Endpoint,
        change.State);
```

Respire reconnects failed connections in the background. Pub/sub subscriptions reconnect and resubscribe automatically.

:::note TLS

Use `rediss://` to enable TLS. Portless `redis://` and `rediss://` URIs both use Redis's standard port, `6379`; specify an explicit port when your provider uses another one.

:::
