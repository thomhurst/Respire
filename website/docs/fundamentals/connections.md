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

## Protocol negotiation

The default `Protocol = RespProtocol.Auto` starts each data connection with `HELLO 3`.
Redis 6+ and compatible servers normally select RESP3. If the server explicitly reports
an unknown `HELLO` command or `NOPROTO`, the same connection remains in RESP2 and completes
AUTH, client naming, and database selection before any application command runs.
Authentication/ACL errors, malformed replies, disconnects, and timeouts do not trigger fallback.
No application command is replayed during negotiation.

Automatic negotiation adds one serialized HELLO round trip before the remaining setup
commands on each new physical connection. It adds no round trip to ordinary commands.
Information logs identify unsupported-HELLO fallback to RESP2 on each physical connection;
Debug logs identify successful RESP3 negotiation. Neither includes credentials. Different unknown-command wording is not treated as proof
that HELLO is unsupported; configure RESP2 explicitly for such a proxy.

Choose `Protocol = RespProtocol.Resp2` or `protocol=2` to skip HELLO and keep the earlier
RESP2 behavior. Choose `RespProtocol.Resp3` or `protocol=3` to require RESP3 and reject
unsupported servers. `protocol=auto` explicitly selects the default policy.
Client-side caching always requires RESP3 and never falls back. Sentinel discovery retains
RESP2 for compatibility; the discovered data connections use the configured policy.

This is a breaking default change for raw callers: RESP3 can return maps, sets, doubles,
booleans, and native nulls where RESP2 used arrays, bulk strings, integers, or null arrays.
Typed commands normalize both reply shapes. Raw `RespireResult` consumers must handle both
or select RESP2 explicitly. Automatic negotiation can also make authentication errors surface
at connection time, before the first application command.

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

## Cluster primary changes

In Cluster mode, a keyed command that receives `READONLY` invalidates its cached slot owner.
Respire refreshes `CLUSTER SLOTS` through other discovered primaries or configured seeds and
retries against a different owner. The replacement is cached for later commands. One refresh
round is bounded by `ConnectTimeout`, and commands share the existing five-retry redirect
budget. If discovery fails or still identifies the same node, the original `READONLY` error is
returned, including for keyed fire-and-forget commands. Cached-owner probes and discovered
primaries share at most half of the round deadline, leaving time for configured seeds.
Earlier configured seeds then share half of the remaining time; the final configured seed
can use the entire remainder. Exhausted phases skip remaining candidates in that phase.
This preserves a usable final-seed allowance even with many stalled earlier endpoints.
Caller cancellation still cancels recovery.

This applies to immediate, raw/catalog, fire-and-forget, batch, blocking/dedicated, and tracked
script commands. Transactions retry only when a queue error aborted the whole transaction;
errors inside an executed result array are never replayed because other commands may have
succeeded. Watched Cluster transactions instead throw `RespireTransactionRetryException` for
MOVED, ASK, or READONLY rejections: restart WATCH and re-read inputs before retrying. They never
replay on another connection. `NoRedirect`, commands without a
known slot, standalone clients, and other server error codes retain their existing behavior.

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

Supported query parameters are `clientName`, `connections`, `connectTimeoutMs`, `commandTimeoutMs`, `responseTimeoutMs`, `protocol` (`auto`, `2`/`resp2` or `3`/`resp3`, case-insensitive), `db`, `cluster`, and `allowAdmin`.

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

An optional [`ReconnectPolicy`](../guides/reconnect-policy.md#sentinel-discovery-fallback)
bounds and delays Sentinel fallback candidates after the first. Configured seeds, learned
peers, and failed primary ROLE validation share that resolution budget; it does not
enable ongoing Sentinel failover.

Programmatic `RespireOptions.Endpoints` follows the same rule: standalone mode requires one
endpoint. Lists that previously left extra standalone endpoints unused now fail validation.
Cluster and Sentinel cannot both be selected in one comma-delimited string.

Supported options are `user` (or `username`), `password`, `ssl`, `sslHost`, `sslProtocols`,
`checkCertificateRevocation`, `clientName` (or `name`), `defaultDatabase` (or `db`),
`connectTimeout`, `asyncTimeout` (or `syncTimeout`), `protocol` (`auto`, `resp2` or `resp3`), and
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

## Redis Cluster endpoint identity

With `UseCluster = true`, newly discovered nodes connect through the preferred endpoint
reported by `CLUSTER SLOTS`. Announced `ip` and `hostname` metadata are aliases, so an IP
redirect to a node discovered by hostname reuses its existing command connections and
dedicated pool. Hostnames compare case-insensitively. Node IDs, when supplied, distinguish
reassigned endpoints from the previous Redis node.

An alias does not change an existing connection's TLS certificate/SNI name. Configure
`TlsOptions.TargetHost` when certificate identity differs from the connection hostname;
that explicit value remains authoritative. Configured seed connections are reused when
topology identifies their aliases.

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
the initial connection. The candidate's data connection must return a valid primary `ROLE`
before the client is returned. A reachable replica, malformed response, or denied `ROLE`
is rejected and the candidate is disposed. Grant `ROLE` to the data-node credentials.

Respire also requests `SENTINEL SENTINELS` and can try up to 64 learned peers after the
configured endpoints. Duplicate hosts/ports and invalid peer addresses are ignored;
configured seeds are retained. Newly learned peers do not recursively expand discovery
within the same attempt. ACL errors, timeouts, protocol errors, or disconnects during this
optional peer-list command do not discard an already completed primary reply. Discovery (including the peer-list request) uses
its existing bounded deadline; primary setup and role validation receive a fresh connection
deadline. Caller cancellation applies throughout. These checks follow the
[Redis Sentinel client specification](https://redis.io/docs/latest/develop/reference/sentinel-clients/).

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

`RespireTimeoutException.Diagnostics` captures the command stage, physical endpoint and
process-local connection ID, outstanding reply count and serialized bytes, bytes waiting to
be written, and time since the last successful read and write. It also includes busy/minimum
worker and I/O thread counts, pending thread-pool work, and a diagnostic hint.

```csharp
try
{
    await redis.GetStringAsync("session:123");
}
catch (RespireTimeoutException error)
{
    var snapshot = error.Diagnostics;
    Console.WriteLine($"{snapshot.Stage}: {snapshot.Endpoint}, pending bytes={snapshot.PendingWriteBytes}");
    Console.WriteLine(snapshot.Hint);
}
```

`WaitingForCapacity` means this attempt was not enqueued. `Buffered`, `Writing`, and
`AwaitingReply` distinguish buffered data, a partial write, and a completed write whose reply
has not completed. A successful socket write does not prove server execution. Transactions
report their complete MULTI/EXEC frame: its full byte count remains outstanding until EXEC
replies, while intermediate replies reduce the outstanding slot count. Other multi-command
frames use the same accounting.
Snapshots also accompany batch failures and dedicated connection operations. Relabeled
internal timeout exceptions preserve the original snapshot. Exceptions constructed by application code
have an unavailable snapshot unless wrapping another timeout exception; they do not sample unrelated
thread-pool activity. Cluster discovery, redirection, and multi-step acquisition can report the stage
without a physical endpoint or connection when none can be reliably identified.

Counters are best-effort observations, not an atomic connection view. Null fields mean no
physical connection or measurement was available, such as during connection acquisition or
multi-step setup. `ConnectionId` is local to the process; `ServerClientId` is populated only
when Redis CLIENT ID was already obtained. Snapshot capture performs no network I/O and
includes no keys, values, or credentials. The snapshot and thread-pool inspection are created
only on failure; successful commands update numeric counters without diagnostic allocations.
Commands expired by one deadline sweep share its connection and thread-pool observations;
each command retains its own stage.

Hints suggest checks; they do not identify a root cause. In particular, queued work with busy
workers at or above the configured minimum is only a possible starvation signal. Inspect
blocking application work, Redis SLOWLOG, payload sizes, and network health before changing
timeouts or thread-pool settings. Intentional blocking commands retain their existing timeout
and cancellation semantics.

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

## Non-zero databases in Valkey Cluster

Set `UseCluster = true` and `Database` to the required database number when connecting to a
Valkey 9+ cluster. Configure `cluster-databases` on every cluster node first: it defaults to
`1`, so database `0` is the only valid selection until that setting is increased. The standalone
`databases` setting does not enable additional Cluster databases.

Respire verifies `INFO SERVER` reports Valkey 9+ in Cluster mode before sending `SELECT` on each
physical connection. This includes discovered nodes, MOVED/ASK destinations, replacement sockets,
dedicated blocking/control connections, and pub/sub connections. The selected database is never
borrowed from another node's capability result. `HELLO` alone is insufficient because Valkey's
version there is a Redis compatibility version.

This opt-in handshake uses sequential round trips for `INFO SERVER`, `SELECT`, and, when
enabled, `CLIENT TRACKING`. Validation completes before selection, and selection completes
before tracking or application commands. This cost repeats for each new physical connection.

Grant the client `INFO` and `SELECT` permissions when using a non-zero Cluster database.
An incompatible server produces `RespireConfigurationException` during connection setup;
`RespireClient.Create` remains lazy, so validation happens on first connection rather than at
construction. Authentication errors, denied discovery/selection, and out-of-range database
errors fail connection setup; Respire never falls back to database `0`. Reconnection repeats
validation and selection before the socket can execute application commands.

An incompatible seed stops setup immediately, even if a later seed is compatible. Respire does
not hide a mixed-version or incorrectly configured endpoint behind another seed. Transient
connection failures still allow the next seed to be tried. Capability errors include the
observed server name, Valkey version, and mode to help identify the incompatible endpoint.

The same configuration failure propagates from discovered-node selection, redirect targets,
dedicated connection acquisition, and initial subscription setup. It is not treated as a
transient discovery failure. Background recovery retains the existing reconnect policy:
command-socket replacement reports the error through `ConnectionStateChanged` and logging;
pub/sub recovery logs failed attempts and continues its backoff while subscriptions wait.
Configure logging to observe subscription recovery failures. No incompatible replacement
receives `SELECT` or application commands, and every later attempt validates again.

Database `0` and standalone connections retain their existing handshake and need no new discovery
permission. Redis Cluster and Valkey before version 9 still support only database `0`.
Hash slots depend on key bytes, not the selected database; multi-key and transaction slot rules
still apply. Pub/sub channels are not isolated by logical database. Each client has one configured
database; create separate clients for different databases instead of sending raw `SELECT`.

See Valkey's [SELECT reference](https://valkey.io/commands/select/) and
[Cluster specification](https://valkey.io/topics/cluster-spec/) for server configuration and scope.
