---
title: Connections and options
description: Configure endpoints, timeouts, reconnects, and connection lifecycle.
---

# Connections and options

Connect with a URI for the common case, or with `RespireOptions` for explicit control. This page covers connection strings, timeouts, protocol, Cluster, Sentinel, and replica reads.

To stop sending commands to an unhealthy endpoint, configure [circuit breakers](../guides/circuit-breakers.md).

## Connect immediately

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379/0");
```

`ConnectAsync` connects before returning, so an unreachable server fails here. Standalone clients throw the underlying error, such as `SocketException` (refused) or `RespireTimeoutException` (`ConnectTimeout` expired). Cluster clients wrap seed failures in `RespireConnectionException`.

When all Cluster seeds fail, the exception message lists the configured seed endpoints, and `InnerException` preserves the candidate failures (an `AggregateException` when several fail). Check those addresses from the application's network, then check TLS settings and credentials against the inner errors. IPv6 addresses are bracketed so the port is unambiguous. Caller cancellation still throws `OperationCanceledException` rather than a seed-exhaustion error.

## Lazy creation

Use `Create` when the application must start before Redis:

```csharp
await using var redis = RespireClient.Create(options);
```

`Create` performs no network I/O. The first command connects; a later command retries a failed attempt. Dependency-injection registration uses `Create`, so Redis availability does not block host startup.

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

| Option | Default | Meaning |
| --- | --- | --- |
| `ConnectTimeout` | 10 s | Bounds each TCP/TLS connection attempt. |
| `CommandTimeout` | 10 s | Reply budget; `null` disables it. Blocking commands use their own wait. |
| `Connections` | 1 | Multiplexed connections (at least 1). Raise only when profiling shows one socket is saturated. |
| `Database` | 0 | Logical database. |
| `Protocol` | `Auto` | See [Protocol negotiation](#protocol-negotiation). |
| `AllowAdmin` | `false` | Set `true` only for callers allowed to run high-risk commands such as `FLUSHDB`, `FLUSHALL`, and `CONFIG SET`. |
| `ConnectionIdleReadTimeout`, `GatheredWriteStallTimeout` | `null` | See [Independent liveness bounds](#independent-liveness-bounds). |
| `MaintenanceNotifications` | `Disabled` | See [Maintenance notifications](#maintenance-notifications). |
| `ClusterTopologyRefreshInterval` | 60 s | See [Cluster topology refresh](#cluster-topology-refresh). |
| `ReadFrom` | `Primary` | See [Read from replicas](#read-from-replicas). |
| `ReplicaRefreshInterval` | 1 s | See [Validation and staleness](#validation-and-staleness). |

For expiring passwords or tokens, use a caller-owned [`IRespireCredentialProvider`](../guides/renewable-credentials.md). It supplies credentials to new connections and renews them on live ones through AUTH.

**Connect timeouts:**

- An expired `ConnectTimeout` throws `RespireTimeoutException` with `CommandName = "CONNECT"` and stage `Connecting`, including for dedicated connections. Catch it, not `OperationCanceledException`.
- Caller cancellation throws `OperationCanceledException` with the caller's token, and wins if both fire.
- The Redis handshake after TCP/TLS setup uses `CommandTimeout`.
- `ConnectTimeout` bounds each attempt, not a whole dedicated-connection rental. A pool replacement can trigger reselection, which never replays an admitted command. Cluster limits these retries; standalone and Sentinel rentals retry until success, cancellation, disposal, or a terminal failure.

## URI query options

```text
redis://localhost:6379/0?clientName=checkout-api&connections=4&allowAdmin=false
```

| Parameter | Sets |
| --- | --- |
| `clientName` | `ClientName` |
| `connections` | `Connections` |
| `connectTimeoutMs` | `ConnectTimeout` |
| `commandTimeoutMs` | `CommandTimeout` |
| `connectionIdleReadTimeoutMs` (or `responseTimeoutMs`) | `ConnectionIdleReadTimeout` |
| `gatheredWriteStallTimeoutMs` | `GatheredWriteStallTimeout` (`-1` disables it) |
| `protocol` | `Protocol`: `auto`, `2`/`resp2`, or `3`/`resp3`, case-insensitive |
| `db` | `Database` (overrides the `/database` path segment) |
| `allowAdmin` | `AllowAdmin` |
| `cluster` (or `useCluster`) | `UseCluster` |
| `serviceName` (or `sentinelPrimaryName`), `sentinelUser`, `sentinelPassword`, `sentinelTls` | [Sentinel](#redis-sentinel) discovery |
| `keyPrefix`, `pubSubPrefix` | `KeyPrefix`, `PubSubPrefix` |

- `rediss://` enables TLS. Without a port, both schemes use 6379 (26379 with `serviceName`).
- Unix sockets: `unix:///path/to/redis.sock` (or `redis+unix://`), with `?db=N`.
- Unknown URI parameters, bad protocol values, and malformed or overflowing integers throw `RespireConfigurationException` naming the option. Parse errors use this exception for both URI and comma-delimited strings. Range checks still apply after parsing.
- `keyPrefix` sets the [default root key namespace](../commands/strings-and-keys.md#default-root-namespace); it does not prefix channels. `pubSubPrefix` sets the [root pub/sub namespace](../guides/pub-sub.md#explicit-pubsub-prefixes) for typed publish and subscribe; notification descriptors and Server PUBSUB queries use physical names.
- Both prefixes take percent-escaped UTF-8, decoded once (escape with `Uri.EscapeDataString`). Empty disables; the last repeat wins. For binary prefixes, set `RespireOptions.KeyPrefix` or `PubSubPrefix`.

## StackExchange.Redis connection strings

Respire accepts the StackExchange.Redis comma-delimited format:

```text
cache-a:6380,password=secret,ssl=true,defaultDatabase=2
```

Endpoints without an explicit port use 6379, or 6380 when TLS is enabled (`ssl=true`, or `sslHost` without `ssl=false`). Sentinel seeds use 26379 even when TLS is enabled. Explicit ports always take precedence.

| Endpoints | Meaning |
| --- | --- |
| `cache-a,cache-b,cluster=true` | Cluster seeds; discovered slot owners route later commands. |
| `sentinel-a,sentinel-b,serviceName=mymaster` | Sentinels used to find and validate the primary. |
| `cache-a,cache-b` | Rejected: standalone mode takes exactly one endpoint. |

The one-endpoint rule also applies to `RespireOptions.Endpoints` (throws `RespireConfigurationException`). One string cannot select both Cluster and Sentinel. An endpoint list never enables connection groups or continuous failover: use [`ConnectAnyAsync`](#connection-time-failover) or a [failover group](../guides/failover-groups.md).

| Option | Meaning |
| --- | --- |
| `user` (or `username`), `password` | Credentials. |
| `ssl` | Enables or disables TLS. |
| `sslHost` | TLS certificate/SNI target. Enables TLS unless `ssl=false`. |
| `sslProtocols` | Allowed TLS versions (format below). |
| `checkCertificateRevocation` | Revocation checking. |
| `clientName` (or `name`) | `ClientName`. |
| `defaultDatabase` (or `db`) | `Database`. |
| `connectTimeout` | `ConnectTimeout` (ms). |
| `asyncTimeout` (or `syncTimeout`) | `CommandTimeout` (ms); `asyncTimeout` wins. |
| `abortConnect=false` | Accepted: `Create` connects lazily. `ConnectAsync` still waits for a connection. `abortConnect=true` is ignored with a warning; choose `ConnectAsync` for eager failure. |
| `channelPrefix` | `PubSubPrefix`, as literal text (unlike the percent-decoded `pubSubPrefix` alias). Last prefix option wins. |
| `keepAlive` | `TcpKeepAliveTime` in seconds; zero or -1 leaves the OS default. This configures TCP probes, not StackExchange.Redis's application-level heartbeat. |
| `connectRetry` | Positive `ReconnectPolicy.MaxAttempts`. Limits recovery attempts; does not retry initial connection setup. Exhausted command slots stay unavailable until the client is recreated. Omit for unlimited recovery. |
| `responseTimeout` | Positive `ConnectionIdleReadTimeout` (ms). Unlike StackExchange.Redis's obsolete option, this actively controls Respire's receive watchdog. |
| `configCheckSeconds`, `topologyRefreshSeconds` | `ClusterTopologyRefreshInterval` in seconds; zero disables periodic refresh. Applies only to Cluster routing. Last entry wins. |
| `maintNotifications` | `MaintenanceNotifications`: `Disabled`, `Auto`, or `Enabled` (case-insensitive). Enabled requires RESP3. |
| `maintRelaxedTimeout`, `maintRelaxedWindowMax` | `MaintenanceRelaxedTimeout` and `MaintenanceWindowTimeout` in seconds, from 1 through 86400. |
| `protocol` | `auto`, `2`/`resp2`, or `3`/`resp3`. |
| `allowAdmin`, `keyPrefix`, `pubSubPrefix` | As for URIs. |
| `cluster` (or `useCluster`) | Cluster mode. |
| `serviceName` (or `sentinelPrimaryName`) | Sentinel mode. |
| `sentinelUser`, `sentinelPassword` | Sentinel credentials; empty `sentinelPassword=` disables inherited auth. |
| `sentinelTls` | Sentinel TLS mode. |
| `sentinelSslHost` | Sentinel TLS target only. Enables Sentinel TLS unless `sentinelTls=false`; protocol and revocation settings stay inherited. |

- `sslProtocols` takes pipe-separated enum names (`Tls12|Tls13`) or a numeric mask of defined bits (`15360`).
- `sslProtocols` and `checkCertificateRevocation` do not enable TLS and do nothing on plaintext.
- In Cluster mode, `sslHost` applies to every node. Use it only when every certificate covers that name.
- `sslHost`, `sentinelSslHost`, `sslProtocols`, and `checkCertificateRevocation` are not URI parameters; with `rediss://`, set `TlsOptions` and `SentinelTlsOptions` in code.
- Booleans accept `true`/`false`, case-insensitive; other values throw `RespireConfigurationException`.
- Omitted ports default to 26379 for Sentinel, otherwise 6380 with TLS or 6379 without TLS. Bracket IPv6 to add a port: `[::1]:6380`.
- Comma-delimited strings accept unknown and client-specific options. `configChannel`, `tiebreaker`, `resolveDns`, `version`, `setlib`, `highPriorityThreads`, `preserveAsyncOrder`, `writeBuffer`, `defaults`, `highIntegrity`, `sharedSubscriptionConnection`, `tcpKeepAlive`, `maintMovingEndpointType`, and `maintPostEventRelaxed` are ignored. Respire uses its own defaults and discovery behavior. URI query parameters remain strict.
- Each ignored key logs warning `IgnoredConnectionStringOption` (141) once per client, without its value. Attach `LoggerFactory` to the parsed options before creating the client; parsing itself has no logger. Duplicate keys are deduplicated case-insensitively, and derived clients do not log again.
- `proxy=None` and an empty `tunnel=` are accepted. Other proxy/tunnel values and `$COMMAND=...` remapping are rejected because silently ignoring them could change routing or commands. Linux abstract Unix sockets (`!@name`) are rejected with a filesystem-socket alternative (`!/path`).
- All `RespireOptions.Parse` errors, including malformed URIs and endpoints, use `RespireConfigurationException` with the reason. Mapped-option errors identify the setting.

For example, an Azure portal string can be used unchanged:

```csharp
var options = RespireOptions.Parse(
    "example.redis.cache.windows.net:6380,password=secret,ssl=True,abortConnect=False")
    with { LoggerFactory = loggerFactory };
await using var redis = RespireClient.Create(options);
```

See the [StackExchange.Redis option reference](https://seredis.dev/Configuration.html) for original semantics.

## Protocol negotiation

| `Protocol` | URI | Behavior |
| --- | --- | --- |
| `Auto` (default) | `protocol=auto` | Sends `HELLO 3`. Falls back to RESP2 on the same connection only if the server reports unknown `HELLO` or `NOPROTO`. |
| `Resp2` | `protocol=2` | Skips `HELLO`. |
| `Resp3` | `protocol=3` | Requires RESP3. |

- No application command runs or is replayed during negotiation.
- Auth/ACL errors, malformed replies, disconnects, and timeouts fail the connection instead of falling back, so auth errors surface at connect time.
- Proxies with other unknown-command wording fail with the original error plus a compatibility hint; set RESP2 explicitly for them.
- `Auto` adds one `HELLO` round trip per new physical connection (not cached), which adds up in large pools and reconnect storms.
- RESP2 fallback logs at Information, RESP3 success at Debug, without credentials.
- Client-side caching requires RESP3. Sentinel discovery always uses RESP2.

:::caution Raw reply shapes
RESP3 returns maps, sets, doubles, booleans, and native nulls where RESP2 returns arrays, bulk strings, integers, or null arrays. Typed commands normalize both. Negotiation is per connection, so a pool can mix protocols and consecutive raw reads can change shape. Raw `RespireResult` consumers must handle both or set an explicit protocol.
:::

## Connection-time failover

Use `ConnectAnyAsync` to try independent deployments in priority order at startup:

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

Each candidate keeps its full options. Respire returns the first connected client and disposes failed ones. If all fail, it throws `RespireConnectionException` listing each attempt, with an aggregate inner exception. The returned client never switches deployments; for health-checked failover and failback, use a [failover group](../guides/failover-groups.md).

## Connection state

`IsConnected` reports availability. `ConnectionStateChanged` raises transition events:

```csharp
redis.ConnectionStateChanged += change =>
    logger.LogInformation(
        change.Error,
        "Redis endpoint {Endpoint} is {State}",
        change.Endpoint,
        change.State);
```

Respire reconnects in the background to the same deployment, and resubscribes pub/sub subscriptions. An optional `ReconnectPolicy` bounds and delays [Sentinel fallback](../guides/reconnect-policy.md#sentinel-discovery-fallback) (one budget per resolution, also used by event monitors) and [Cluster node, topology, and seed fallback](../guides/reconnect-policy.md#cluster-discovery-fallback) (one budget per discovery round).

## Cancellation and timeouts

Cancelling a token abandons the wait; it cannot guarantee the server did not execute a command already written. Variadic `params ReadOnlySpan<T>` commands take a token on sibling overloads for a collection or a single item: `DeleteAsync(keys, cancellationToken)` or `DeleteAsync("key", cancellationToken)`.

`RespireTimeoutException.Timeout` reports the budget that expired:

- **Command response:** treat writes as possibly executed; design retries around idempotency.
- **Replica function propagation** (distinct message): the function stayed missing on the replica. Check replication and replica health. `CommandTimeout` cannot extend the five-second ceiling (see [Consistency](#consistency)).

`Diagnostics` captures the stage, endpoint, process-local `ConnectionId`, `ServerClientId` (if known), outstanding replies and bytes, unwritten bytes, time since last read and write, thread-pool counts, and a hint. It contains no keys, values, or credentials.

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

| Stage | Meaning |
| --- | --- |
| `Connecting` | TCP/TLS setup. |
| `WaitingForCapacity` | Not enqueued. |
| `Buffered` | Queued, not written. Not safe to retry: it can still be sent after cancellation or timeout. |
| `Writing` | Partially written. |
| `AwaitingReply` | Written; a successful write does not prove execution. |

- Retry metadata does not enable transport retries; a partial write stays ambiguous for non-idempotent commands.
- Counters are best-effort. Null fields mean no connection or measurement was available. Application-created exceptions have no snapshot unless they wrap one.
- Hints are not root causes. Check blocking code, SLOWLOG, payload sizes, and network before changing timeouts or thread-pool settings.

:::warning Do not block on a result inside a Respire continuation
`.Result`, `.Wait()`, or `GetAwaiter().GetResult()` on a pending command inside another command's continuation can stall, because replies on one connection arrive in order behind that continuation. After about 500 ms Respire delivers them on another thread and logs "Reply delivery ... was blocked by a continuation". Use `await` instead.
:::

Redis error replies throw `RespireServerException` with `Code`, `CommandName`, and `IsTransient` (true for `LOADING`, `BUSY`, `CLUSTERDOWN`, `TRYAGAIN`, `MASTERDOWN`). Use `RespireErrorCodes` in retry policies.

### Exception endpoint context

Every public Respire exception inherits `RespireException.Endpoint`, a nullable `RespireEndpoint` containing the host and port (or Unix socket address). Use this property instead of parsing an exception message. `null` means no single endpoint is known; failures covering several connection candidates do not select one candidate's endpoint. A populated endpoint does not prove that a command was submitted or executed.

Timeout exceptions take their endpoint from `Diagnostics.Endpoint`, or from a directly wrapped Respire exception when no snapshot endpoint is available. Transaction-retry exceptions retain the rejecting server's endpoint when the original server error has one. Circuit-open and scripting-engine-unavailable exceptions expose their known endpoint through both the base property and their existing non-nullable `Endpoint` property.

Connection owners populate context for unavailable connections, reconnect limits, and replica `ROLE` validation. A rejected `ROLE` reply retains its server exception and the same endpoint as its connection wrapper. Sentinel query failures identify the reporting Sentinel; primary connection and validation failures identify the resolved data candidate. A redirect destination is not the endpoint of the server that rejected a command.

Server and protocol failures from physical replies carry the originating endpoint, including typed, byte and streamed replies, deferred batch conversions, retained transaction queue errors, and individual EXEC results. Connection receive/send failures, authentication and handshake failures, and failures rejected before admission also carry context when the physical connection knows its endpoint. This does not require enabling telemetry.

Retries preserve the endpoint of the failed attempt. A MOVED or ASK error identifies the server that returned the redirect; its destination is not the failure origin. Exception types, underlying causes, and pre-admission classification are preserved. Native framework exceptions such as cancellation or socket errors retain their existing types and do not acquire a Respire property.

Cluster seed, replica-selection, and Sentinel discovery failures covering multiple candidates leave the outer endpoint null. Inspect their inner causes, including nested `AggregateException` instances, for individual candidate failures. Reconnect exhaustion retains its underlying failures; cluster discovery exhaustion has no single endpoint.

Failures without a known single endpoint, including locally constructed errors, may still have a null endpoint. General-purpose exception constructors do not copy endpoint context from an inner exception: that cause could represent only one failed candidate. Application code can attach known context when constructing an exception:

```csharp
var failure = new RespireConnectionException("Redis connection failed.")
{
    Endpoint = new RespireEndpoint("redis.example", 6380)
};
```

### Client-side cross-slot validation

By default in Cluster mode, keys in one multi-key command must share a slot after key prefixing.
A local mismatch throws `RespireCrossSlotException` before sending or enqueueing the
command, including raw commands and MGET cache hits. `CommandName`, `ExpectedSlot`,
and `ConflictingSlot` identify the rejected command and first detected slot conflict.
Give every key the same nonempty hash tag, such as `{order:42}:items` and
`{order:42}:total`; a hash tag in the client prefix also affects placement.
A rejected command does not occupy a batch/transaction entry or pin its slot.

The exception retains no keys by default. For a diagnostic session, explicitly set
`RespireOptions.IncludeCrossSlotKeyContext = true` to capture `ConflictingKeyHex`:
an owned hex representation of the conflicting physical key (including its prefix).
Hex is reversible and may reveal secrets or personal data. This property stays out
of the exception message and telemetry tags; keep it out of general application logs.
Respire's existing telemetry does not collect command text, and this opt-in does not
enable command logging.

Catch `RespireCrossSlotException` separately when handling local input validation.
It is not a `RespireServerException` and built-in retries do not retry it. Genuine
Redis `CROSSSLOT` replies still throw `RespireServerException` with
`Code == RespireErrorCodes.CrossSlot`. Error metrics classify local rejection as
`validation`, omit the server endpoint and `db.response.status_code`, and continue
to classify server replies as `server` with the `CROSSSLOT` status code.

### Splitting cross-slot commands

Set `RespireOptions.SplitCrossSlotCommands = true` to split immediate `MGET`, `MSET`,
`DEL`, `UNLINK`, `EXISTS`, and `TOUCH` requests by their physical hash slot:

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    UseCluster = true,
    Endpoints = [new("localhost", 7000)],
    SplitCrossSlotCommands = true,
});
await redis.Strings.SetManyAsync(("{customer:1}:name", "Ada"), ("{customer:2}:name", "Grace"));
var names = await redis.Strings.GetManyAsync("{customer:2}:name", "{customer:1}:name");
// names: ["Grace", "Ada"]
```

This opt-in applies to the typed facets and immediate catalog, raw, and interpolated
Execute APIs. Groups run concurrently, including separate slots owned by the same
node. `MGET` restores input order and preserves duplicates and nulls; `MSET` succeeds
only if every group returns OK; the other commands sum their integer replies.
Same-slot requests remain one command. Prefixes and hash tags determine the physical slots.

**Cross-slot requests are not atomic.** In particular, part of an `MSET` can succeed
before another group fails. Reads across slots are not a single consistent snapshot.
`MSETNX`, set algebra, scripts, transactions, deferred batches, and fire-and-forget
requests keep their existing single-slot restrictions. `JSON.MGET` is not split.

Each group uses the normal slot routing, MOVED/ASK recovery, configured retries,
reconnect backlog, read preference, and cache fences. A retry affects only that group;
successful groups are not replayed by the splitter. Typed `MGET` retains per-key caching.
Command hooks run once for the whole logical call, with completion after all groups finish.
The splitter starts one operation per distinct slot without a separate concurrency cap.
Each operation still obeys the connection's in-flight and backlog limits. For large key
sets, submit smaller batches to bound pending operations and share capacity with other callers.

If a group fails, the call joins all outstanding groups and throws
`RespireClusterPartitionException`, exposing `CommandName`, `Slot`, the known failed
`Endpoint`, and the underlying exception. If several groups fail, one failure is surfaced;
no partial result is returned. Route discovery failures may have no single known endpoint.
The exception does not enumerate successful slots, and a failed slot may still have applied
its write if the reply was lost. Reconcile application state before attempting compensation.
Caller cancellation remains `OperationCanceledException`; cancellation cannot roll back
commands already accepted by a node. Do not blindly retry an entire failed write request.

## Independent liveness bounds

`ConnectionIdleReadTimeout` aborts a connection when replies are pending and no bytes arrive (`null` disables it). `GatheredWriteStallTimeout` bounds how long a large binary SET that sends caller memory directly can stall.

| GatheredWriteStallTimeout | Borrowed-write behavior |
| --- | --- |
| `null` (default) | Inherits `ConnectionIdleReadTimeout`; both null disables both bounds. |
| Finite, at least 1 ms | Uses this write budget regardless of the receive budget. |
| `Timeout.InfiniteTimeSpan` | Disables only the write bound. |

```csharp
var options = new RespireOptions
{
    Endpoints = [new("localhost", 6379)],
    ConnectionIdleReadTimeout = TimeSpan.FromSeconds(30),
    GatheredWriteStallTimeout = TimeSpan.FromSeconds(5),
};
```

- DI and Aspire options expose both. Zero, sub-millisecond, and negative values (other than URI `-1`) are rejected.
- A paused bulk-stream consumer suppresses only receive checks. New commands do not postpone a stall.
- Maintenance windows relax enabled bounds to `MaintenanceRelaxedTimeout`.

:::warning Write stalls
A write stall closes the whole connection, failing other pending commands. Redis may have executed part or all of the SET. Cancellation does not abandon an accepted frame: keep binary input unchanged until the call completes, even if it throws. With the write bound disabled, a stalled peer can hold caller memory until the socket fails or the client is disposed. See [large binary SET ownership](../commands/strings-and-keys.md).
:::

## Maintenance notifications

Opt in to relax timeouts and reconnect proactively during server maintenance:

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.internal", 6379) },
    MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto,
    MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(30),
    MaintenanceWindowTimeout = TimeSpan.FromSeconds(60),
};
```

| Option | Default | Meaning |
| --- | --- | --- |
| `MaintenanceNotifications` | `Disabled` | `Disabled`: no handshake. `Auto`: sends `CLIENT MAINT_NOTIFICATIONS ON` on RESP3; an unknown-command `ERR` disables it for that connection, other errors fail setup. `Enabled`: requires RESP3 and an acknowledgement. |
| `MaintenanceRelaxedTimeout` | 30 s | Minimum timeout during a window (1 ms to 1 day). |
| `MaintenanceWindowTimeout` | 60 s | Maximum window length (1 ms to 1 day). |

Auth/ACL errors, malformed replies, timeouts, and transport failures always fail setup. Servers that accept but never send notifications keep normal timeouts.

**Timeout relaxation:**

- `MIGRATING`, `FAILING_OVER`, and `SMIGRATING` relax the connection until completion or `MaintenanceWindowTimeout`. `MOVING` relaxes it until its grace period or the window, whichever is shorter.
- Commands use the greater of their timeout and `MaintenanceRelaxedTimeout`, measured from their original start. Already-expired commands are not revived; when the window ends, old commands can time out at once.
- Cancellation still applies; null timeouts stay unlimited. Up to 256 overlapping operations are tracked per connection.

**MOVING handoff** (multiplexed connections):

1. Respire connects and handshakes with the announced endpoint (or re-resolves the configured host if none is given).
2. New commands, and unaccepted ones (keeping their timeout), move to the new sockets. `CLIENT ID` and credential-renewal `AUTH` never move.
3. Accepted commands drain on the old sockets.

The grace period is a drain budget, not a guarantee. An unreachable target is retried until it ends, then normal reconnect applies. A handshake finishing after it still switches, and old sockets close at once, failing their accepted commands (logged as a warning).

**Cluster slot migrations:** `SMIGRATED` updates slot ownership when the source still owns the slots. Dropped or missing notifications fall back to `MOVED` redirects and [topology refresh](#cluster-topology-refresh); skips count in `respire.cluster.slot_migrations.skipped`. Disposal waits for running topology callbacks, such as `ConnectionStateChanged` handlers, so keep them short.

Enabling the option does not add server support; see [Redis smart client handoff](https://redis.io/docs/latest/develop/clients/sch/) (Redis Cloud/Software) and [maintenance diagnostics](../integrations/observability.md#maintenance-notifications).

## Redis Cluster endpoint identity

With `UseCluster = true`, nodes connect through the preferred endpoint from `CLUSTER SLOTS`. Announced `ip` and `hostname` are aliases (hostnames case-insensitive), so redirects and seeds reuse existing connections. Node IDs distinguish a reassigned endpoint. Aliases do not change a connection's TLS/SNI name; set `TlsOptions.TargetHost` when certificate identity differs.

## Cluster topology refresh

`ClusterTopologyRefreshInterval` (default 60 s, minus up to 10% jitter) sends one `CLUSTER SLOTS` to one node, starting after the first connection. Lengthen it on large fleets. `null`, `TimeSpan.Zero`, or `Timeout.InfiniteTimeSpan` disables only the timer; these fixed triggers remain:

| Trigger | Timing |
| --- | --- |
| Primary connection lost | At most once per second. |
| `MOVED` redirect | Debounced 5 s; reuses a refresh from the last 5 s. |
| Failed refresh | Backoff 5–60 s until success; other refreshes wait for it. |

Concurrent triggers share one refresh, bounded to 60 s. Failed discovery keeps the last slot map; a partial reply (for example `cluster-require-full-coverage no`) updates only covered slots. Replicas serve as refresh fallbacks; commands use primaries unless a [read policy](#read-from-replicas) says otherwise.

## Cluster primary changes

A keyed command that gets `READONLY` refreshes `CLUSTER SLOTS` (within `ConnectTimeout`) and retries on the new owner, sharing the five-retry redirect budget. If discovery fails or finds the same node, the `READONLY` error is returned, also for keyed fire-and-forget commands. Caller cancellation cancels recovery. This covers immediate, raw, fire-and-forget, batch, blocking, and tracked script commands.

- Transactions retry only when a queue error aborted the whole transaction; executed results are never replayed.
- Watched transactions throw `RespireTransactionRetryException` on `MOVED`, `ASK`, or `READONLY` and never replay on another connection. Restart WATCH and re-read inputs.
- `NoRedirect`, slotless commands, and standalone clients are unaffected.

## Non-zero databases in Valkey Cluster

1. Use Valkey 9+ (Redis Cluster and older Valkey support only database `0`).
2. Raise `cluster-databases` on every node (default `1`; the standalone `databases` setting does not apply).
3. Grant `INFO` and `SELECT`.
4. Set `UseCluster = true` and `Database`.

Every new connection checks `INFO SERVER` before `SELECT`, adding sequential round trips. Database `0` and standalone skip the check.

- An incompatible server throws `RespireConfigurationException` at connection setup, naming server, version, and mode. It stops setup even if a later seed is compatible, and is never treated as transient.
- Auth errors, denied commands, and out-of-range databases fail setup; Respire never falls back to `0`.
- In background recovery, command sockets report failures through `ConnectionStateChanged`; pub/sub recovery only logs them, so configure logging.
- Slots depend on key bytes, not the database. Pub/sub is not isolated by database. Use one client per database; do not send raw `SELECT`.

See Valkey's [SELECT](https://valkey.io/commands/select/) and [Cluster specification](https://valkey.io/topics/cluster-spec/).

## Redis Sentinel

Set `SentinelPrimaryName` to discover the primary through Sentinel. To manage Sentinel itself, see [Sentinel administration](../guides/sentinel-administration.md).

```csharp
await using var redis = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = { new RespireEndpoint("sentinel-1", 26379) },
    SentinelPrimaryName = "mymaster",
    Password = configuration["Redis:Password"],
    SentinelPassword = configuration["Redis:SentinelPassword"],
});
```

```csharp
await using var redis = await RespireClient.ConnectAsync(
    "redis://:redis-password@sentinel-1?serviceName=mymaster&sentinelPassword=sentinel-password");
```

**Authentication and TLS:**

- Sentinel inherits primary credentials. `SentinelPassword = string.Empty` (or empty `sentinelPassword=`) disables them for Sentinel only.
- Sentinel inherits TLS. Override with `SentinelUseTls` and `SentinelTlsOptions`; `sentinelTls=false` gives plaintext Sentinel with a `rediss://` primary.
- Discovery uses RESP2, so older Sentinels work with RESP3 primaries.

**Discovery:**

- Configured Sentinels are tried in order, then up to 64 peers learned from `SENTINEL SENTINELS`. Respire moves on when discovery times out, returns invalid data, or names a primary it cannot reach.
- `Create` performs no I/O; the first operation discovers the primary.
- Every data connection must report primary `ROLE`; replicas and denied or malformed replies are rejected. Grant `ROLE` to the data credentials.
- Discovery has its own deadline; connection setup gets a fresh `ConnectTimeout`. See the [Sentinel client specification](https://redis.io/docs/latest/develop/reference/sentinel-clients/).

**Failover:**

- A disconnect, `READONLY` (also inside EXEC, script, and fire-and-forget replies), or replica `ROLE` retires the primary; the next operation rediscovers and revalidates.
- Accepted commands drain on old sockets. Ambiguous writes and WATCH state are never replayed; start a new watched transaction.
- With maintenance notifications, retiring connections get `CommandTimeout` (at least 30 s if disabled), extended by any relaxation.
- Interrupted cached MGET/HMGET reads refetch from the new primary. Subscriptions reconnect and report a delivery gap; missed messages are lost. Explicit server connections stay pinned.
- `Endpoint` throws `InvalidOperationException` until the first primary is validated (or after failed discovery); it never returns a Sentinel address.
- `ConnectionStateChanged` reports changes; disposal does not wait for a running observer. `respire.sentinel.failover` counts primary changes (`server.address`, `server.port`). `respire.sentinel.generations.retired` counts primaries still draining; steady growth means stuck work. There is no forced drain deadline.

**Event monitors:** after the first validated primary, Respire subscribes to `+switch-master`, `+sdown`, and `+odown` on each Sentinel, costing one extra TCP connection each. Events trigger rediscovery confirmed by `ROLE`, at most once per 100 ms. Monitors reconnect under `ReconnectPolicy` (`respire.reconnect.scope = sentinel-monitor`), and a monitor reconnect or delivery gap triggers one rediscovery. Failed event-driven rediscovery retries with backoff (up to 30 s apart without a policy, or `MaxAttempts`). Disposal waits up to 10 s for monitor work.

:::caution Grant SENTINEL MASTER
Discovery reads `SENTINEL MASTER` for `config-epoch` so a stale Sentinel cannot roll the client back. Without it, Respire falls back to `ROLE` checks, warns once, and cannot reliably order repeated failovers (A→B→A, then a delayed A→B). Grant `SENTINEL MASTER` to the Sentinel credentials.
:::

## Read from replicas

Replica sources: `ReplicaEndpoints` (standalone; `Endpoints` still holds one primary, not with `UseCluster`), `SENTINEL REPLICAS` from the first well-formed Sentinel reply, or Cluster slots. A `ReadFrom` other than `Primary` without one throws `RespireConfigurationException`.

| `RespireReadFrom` | Behavior |
| --- | --- |
| `Primary` (default) | Always the primary. |
| `Replica` | Fails when no validated replica is available. |
| `PrimaryPreferred` | Replica when primary connection acquisition fails. |
| `ReplicaPreferred` | Primary when replica selection fails. |
| `Nearest` | Lowest measured latency. |
| `AzAffinity`, `AzAffinityReplicasAndPrimary` | Zone-aware; see below. |

- `WithReadFrom` overrides per view and composes with `WithKeyPrefix`.
- Only catalog read-only commands (including blocking reads like `XREAD BLOCK` and fire-and-forget reads) follow the policy. Writes, custom commands, subscriptions, batches, and transactions use the primary.
- Without hedging, fallback happens only during selection; a written command is never resent.
- Blocking reads rent a dedicated connection with no response timeout; replica connections validate `ROLE` first. Cancellation discards the connection. Removing a replica drains accepted blocking reads; disposal aborts them.
- For blocking reads, preferred policies can switch roles once after `LOADING`, `MASTERDOWN`, or `CLUSTERDOWN`, keeping the original error if no fallback connection is available. Cancellation never triggers fallback.

### Nearest reads

`Nearest` picks the eligible connection with the lowest `PING` latency (at most four probes per router, one per connection per second, estimates fresh for 10 s). Writes and unknown commands use the primary. It can return stale data, bypasses the client-side cache, and keeps cursors on their endpoint.

- Selection waits up to one second for samples and never queues a read behind an unanswered `PING`. Cancellation stops only that caller's wait. Unsampled candidates stay eligible. If every candidate has a pending probe, it throws a connection exception.
- A `PING` failure or ACL denial drops the estimate, not the candidate. Failed connections cool down for `ReplicaRefreshInterval` (replicas) or one second (primary, Cluster).
- Latency reflects queues and network, not replication lag. Linked replicas are still preferred.

### Hedged reads

:::warning Experimental API: RSP0003
This feature requires an explicit compiler opt-in and is outside the 2.0 compatibility promise.
See [RSP0003](../diagnostics/RSP0003.md) for the affected APIs and the exact `NoWarn` or `#pragma` opt-in.
:::

`HedgedReads` (off by default) sends a slow idempotent read to a second server; the first success wins, and both failing returns the original error.

```csharp
var options = new RespireOptions
{
    Endpoints = [new("primary", 6379)],
    ReplicaEndpoints = [new("replica", 6379)],
    ReadFrom = RespireReadFrom.ReplicaPreferred,
    HedgedReads = new RespireHedgedReadOptions
    {
        Delay = TimeSpan.FromMilliseconds(10),
        MaximumExtraLoadPercent = 5,
    },
};
```

| Option | Default | Meaning |
| --- | --- | --- |
| `Delay` | 10 ms | Wait after connection selection before hedging (1 ms to 1 min). Choose from observed latency; Respire does not track p95/p99. |
| `MaximumExtraLoadPercent` | 5 | Hedge-start cap (1–100), shared across views. At 5%, twenty eligible reads fund one hedge; at most one is stored. Bounds starts, not bytes or CPU. |

- `Primary` never hedges; `Replica` hedges only to replicas; other policies hedge to any different peer, keeping zone ranking. Cluster hedges stay within the key's current slot topology.
- Eligible: buffered idempotent catalog reads (`GET`, `MGET`, deterministic collection, geo, and stream range reads). Excluded: writes, scripts, random, cursors, blocking, batches, transactions, streamed, unknown.
- Hedging copies arguments and allocates. Losing requests are not cancelled; they keep their timeout and response slot.
- A replica hedge can return stale data even when the original targeted the primary.
- Metrics: `respire.read.hedge.sent`, `.won`, `.extra_load`. See [`tools/Respire.StressTests`](https://github.com/thomhurst/Respire/tree/main/tools/Respire.StressTests).

### Validation and staleness

Replicas must report `ROLE` replica. Replicas with a `connected` link beat syncing ones; unlinked replicas are used only when no linked one exists, subject to `replica-serve-stale-data`.

`ReplicaRefreshInterval` (default 1 s) bounds staleness:

- A promoted node can serve reads for one interval (two under heavy load).
- Sentinel refreshes replicas once per interval, backing off up to 30 s during outages.
- A failing replica is skipped for one interval. `TimeSpan.Zero` revalidates every read.

A removed replica stops taking reads, then drains accepted commands. A stream with no data for 30 s (or `CommandTimeout`, if longer) fails. Disposal reports up to 64 retirement exceptions.

### Availability-zone affinity

Set `ClientAvailabilityZone` before connecting, then choose an affinity policy as the
default or through `WithReadFrom`:

```csharp
await using var client = await RespireClient.ConnectAsync(new RespireOptions
{
    Endpoints = [new RespireEndpoint("primary.example", 6379)],
    ReplicaEndpoints = [new RespireEndpoint("replica.example", 6379)],
    ClientAvailabilityZone = "eu-west-2a",
    ReadFrom = RespireReadFrom.AzAffinity
});
```

| Policy | Selection order |
| --- | --- |
| `AzAffinity` | Same-zone replicas, other replicas, primary |
| `AzAffinityReplicasAndPrimary` | Same-zone replicas, same-zone primary, other replicas, primary |

- Requires a nonempty `ClientAvailabilityZone` (ordinal, case-sensitive). Works with standalone, Sentinel, and Cluster.
- Linked replicas still beat unlinked ones.
- Zones come from Valkey's `availability_zone` (`HELLO` or `INFO SERVER`), captured per connection and refreshed on reconnect. RESP2 clients request `INFO SERVER` during the handshake. Missing metadata or ACL denial leaves the zone unknown; unknown zones fall into the other-replica tier, so Redis gets replica-first behavior.

### Cursor reads

`SCAN`, `HSCAN`, `SSCAN`, `ZSCAN`, and `ARSCAN` cursors are valid only on their issuing server. Typed `ScanAsync` methods pin each enumeration to one server; if it fails, they throw `RespireConnectionException`; start a new scan. Raw continuing cursors through `ExecuteAsync` throw the same after their pinned server leaves; restart at `0`. A raw `ARSCAN` always reselects.

### Consistency

Replica reads are stale and never read-your-writes. A replica disconnect does not flush the client-side cache.

A read-only function missing on a replica is reloaded on the primary and retried for up to five seconds (less with a shorter `CommandTimeout`; `null` does not disable it). Retries keep the read policy, so `Replica` never falls back to the primary. Exhaustion throws `RespireTimeoutException`; caller cancellation throws `OperationCanceledException`. An admitted attempt can still execute after the budget expires.

Replica health is reported through `ConnectionStateChanged` with the replica's endpoint.

### Cluster routing

Cluster replicas come from `CLUSTER SLOTS` and use `READONLY`. Strict `Replica` fails without a usable replica; preferred policies fall back on selection failures and `LOADING`, `MASTERDOWN`, or `CLUSTERDOWN`.

- Writes, unknown commands, transactions, cache-backed reads, and `TOUCH` stay on primaries.
- `MEMORY USAGE`, `EVAL_RO`, and `EVALSHA_RO` run on the selected node; read-only scripts need not be deterministic.
- Batches use replicas only when every operation is an eligible read. Cursors stay on their issuing node.
- Replica refresh budget: `ConnectTimeout + CommandTimeout` (`ConnectTimeout` twice when `CommandTimeout` is disabled). Failed refreshes keep existing routes.
