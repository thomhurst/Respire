---
title: Connections and options
description: Configure endpoints, timeouts, reconnects, and connection lifecycle.
---

# Connections and options

Use a URI for the common case or `RespireOptions` when the connection needs explicit control.

For opt-in standalone or Cluster data-node admission, configure [circuit breakers](../guides/circuit-breakers.md).

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
Large pools and reconnect storms pay this setup cost for every socket, including endpoints
that repeatedly fall back to RESP2. Protocol negotiation is not cached across connections.
Information logs identify unsupported-HELLO fallback to RESP2 on each physical connection;
Debug logs identify successful RESP3 negotiation. Neither includes credentials. Different unknown-command wording is not treated as proof
that HELLO is unsupported; configure RESP2 explicitly for such a proxy. An unclassified
`ERR` during automatic HELLO includes that compatibility hint unless its wording indicates
an authentication or ACL failure. The connection still fails and preserves the original error.

Choose `Protocol = RespProtocol.Resp2` or `protocol=2` to skip HELLO and keep the earlier
RESP2 behavior. Choose `RespProtocol.Resp3` or `protocol=3` to require RESP3 and reject
unsupported servers. `protocol=auto` explicitly selects the default policy.
Client-side caching always requires RESP3 and never falls back. Sentinel discovery retains
RESP2 for compatibility; the discovered data connections use the configured policy.

This is a breaking default change for raw callers: RESP3 can return maps, sets, doubles,
booleans, and native nulls where RESP2 used arrays, bulk strings, integers, or null arrays.
Typed commands normalize both reply shapes. Raw `RespireResult` consumers must handle both
or select RESP2 explicitly. Negotiation is per physical connection, including reconnects.
A pool can therefore contain RESP2 and RESP3 connections when endpoints or intervening
proxies support different protocols. Consecutive raw reads can have different reply shapes;
select an explicit protocol when a consumer requires one fixed shape.
Automatic negotiation can also make authentication errors surface at connection time,
before the first application command.

## Maintenance notifications

Maintenance handling is opt-in through structured options:

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("cache.internal", 6379) },
    MaintenanceNotifications = RespireMaintenanceNotificationMode.Auto,
    MaintenanceRelaxedTimeout = TimeSpan.FromSeconds(30),
    MaintenanceWindowTimeout = TimeSpan.FromSeconds(60),
};
```

`Disabled` is the default and sends no extra handshake command. `Auto` sends
`CLIENT MAINT_NOTIFICATIONS ON` after RESP3 setup; an `ERR` reply reporting an unknown command
or unknown subcommand disables maintenance handling for that connection. Other error wording
is treated as a setup failure rather than as missing support. RESP2 connections also leave it disabled.
`Enabled` requires RESP3 and a successful acknowledgement. Authentication/ACL errors,
malformed replies, timeouts, and transport failures always fail setup. Ordinary servers
that accept the request but never send a maintenance push retain their normal timeouts.

Valid `MIGRATING`, `FAILING_OVER`, and `SMIGRATING` pushes relax the receiving command
connection's timeouts until the matching completion or `MaintenanceWindowTimeout`.
`MOVING` relaxes that connection until its grace period or the configured maximum window,
whichever is shorter. Pending and new commands, including producers waiting for ring
capacity, use the greater of the configured timeout and `MaintenanceRelaxedTimeout`.
Timeouts remain measured from the command's original start; a push does not restart them.
An already-expired caller cannot be revived. Completion/expiry restores normal deadlines,
so an old pending command can time out immediately afterward. Caller cancellation remains
active. Null command or receive timeouts remain unlimited. Watchdog restoration is observed
within its polling interval (at most one second); command restoration uses the normal sweep.
A notification does not revive a command whose original deadline already elapsed before the uninterrupted maintenance window began, even when the deadline sweep has not yet observed it. Overlapping operations retain the same cutoff; a later window after completion or expiry gets a new cutoff.

Both maintenance duration options must be between one millisecond and one day.

Overlapping operations have independent sequence/family windows. Duplicate starts do not
extend retained windows; unmatched completions cannot close another operation. Up to 256
recent identities are retained per physical connection, including completed/expired ones.
Under pressure, the oldest finished identity is evicted first. More than 256 concurrent operations
share a conservative overflow window that expires automatically. Completion replays received
before the negotiation acknowledgement are suppressed; pushes after it are handled normally. Reconnect starts with fresh per-connection state.

For multiplexed client connections, `MOVING` also triggers a background handoff. Respire
connects and completes the normal TLS, authentication, database, and notification handshake
with the announced endpoint before publishing replacement sockets. A null target reconnects
to the configured logical host; that host remains the reconnect name so DNS can change later.
Accepted commands drain on the old sockets, while commands selected just before retirement
move to a current socket only when the old socket had not accepted their frame, keeping the
command timeout that started on the old socket. Connection-scoped commands (`CLIENT ID`,
correction barriers, and credential-renewal `AUTH`) never move to another socket.

The advertised grace period starts when the notification is parsed, and it is a drain budget,
not a promise that accepted commands finish:

- A target that cannot be reached is retried until the grace period ends. Nothing has been
  published at that point, so the current connections stay in place and normal reconnect
  applies when the server closes them.
- A target handshake that completes after the grace period is still published, because the
  source is about to close and the target is the only endpoint left. The old sockets are then
  closed at once, so commands they had accepted fail with a connection error. Respire logs a
  warning when this happens.
- Old sockets still draining when the grace period ends are closed the same way.

Sequence IDs are tracked per announcing server, so the replacement server can announce a later
`MOVING` with its own numbering. A sequence ID suppresses repeats only until the grace period it
announced ends, so a server that restarts at the same address and numbers from 1 again is
followed after that.

This release implements notifications, diagnostics, timeout relaxation, and proactive Cluster
slot updates from `SMIGRATED`. The receive loop queues parsed notifications for a bounded topology
worker. The worker moves only slots still owned by the advertised source and not reassigned by a
`MOVED` redirect or discovery since the notification arrived. It ignores sequence IDs already seen
on the same connection and publishes changed ownership through the normal topology event. When
notifications from different connections arrive out of order (for example `B→C` before `A→B`),
the later move waits in a small bounded list and applies once the earlier one has, whichever
notification arrived first. A waiting move is dropped after 30 seconds, or when a `MOVED` redirect
or discovery reassigns its slots after it arrived. An older move is also rejected when its source
moved the slot away and got it back after that move arrived (`A→B` then `B→A` overtaking an
older `A→C`). A sequence ID is recorded when the worker first sees it, before its slots are
checked, so a server resend of the same ID on the same connection is ignored even when its first
copy was rejected, fenced or later dropped from the waiting list. If a
notification is dropped under queue pressure, or a server sends none, ordinary `MOVED` handling
and topology discovery remain the fallback. Until one of them runs, commands for the affected
slots go to the previous owner and are redirected. Queue drops request a topology refresh
using the same debounce and failure backoff as `MOVED`. Periodic topology refresh also
repairs missed notifications when its timer is enabled.
See [Cluster topology refresh](#redis-cluster-endpoint-identity).
Drops and other skipped notifications are counted
in `respire.cluster.slot_migrations.skipped` (see [Observability](../integrations/observability.md)).
Client disposal waits for a topology callback that is already running, such as a
`ConnectionStateChanged` handler raised by a migration, so keep those handlers short.

Server support and deployment restrictions are described in the
[Redis smart client handoff documentation](https://redis.io/docs/latest/develop/clients/sch/).
This mechanism targets supporting Redis Cloud/Software deployments; enabling the option does
not add server support. See [maintenance diagnostics](../integrations/observability.md#maintenance-notifications)
for activity, metric, and logging delivery.

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

This is connection-time fallback only. After a client is returned, commands run against that selected deployment and use Respire's normal reconnect behavior. `ConnectAnyAsync` is not a health-checked circuit breaker and does not continuously route commands between independent deployments. Use a [failover group](../guides/failover-groups.md) for health-checked switching and failback.

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

An independent TCP/TLS `ConnectTimeout` throws `RespireTimeoutException` with
`CommandName = "CONNECT"` and diagnostics at the `Connecting` stage, including during
dedicated connection acquisition. Caller cancellation remains `OperationCanceledException`
with the caller's token. Pool retirement keeps its separate cancellation identity so routing
can select a replacement. Redis handshake replies retain their command timeout behavior.

This changes the exception type for independent connect deadlines that previously surfaced
as `OperationCanceledException`. Catch `RespireTimeoutException` for connect timeouts;
a catch for `OperationCanceledException` alone no longer handles those deadlines.
If caller cancellation and the deadline have both fired when classified, caller cancellation wins.
Routing samples pool retirement when handling the failure. A CONNECT timeout can therefore
trigger reselection even if the timeout preceded retirement; this never replays an
admitted application command. Cluster routes enforce their retirement retry limit. Standalone
and Sentinel dedicated rentals have no retirement attempt limit: repeated pool replacement can
continue until acquisition succeeds, cancellation or disposal occurs, or a terminal failure is
encountered. `ConnectTimeout` bounds each connection attempt, not the entire rental across
replacements. Active-pool and Redis handshake timeouts remain failures.

`AllowAdmin = false` is the default safety setting. Set it to `true` only for callers that are allowed to run high-risk server administration commands such as `FLUSHDB`, `FLUSHALL`, and `CONFIG SET`.

For expiring passwords or access tokens, use a caller-owned
[`IRespireCredentialProvider`](../guides/renewable-credentials.md). It supplies current credentials
for new connections and renews expiring credentials on live connections through AUTH.

## URI query options

Connection URI query parameters cover common options:

```text
redis://localhost:6379/0?clientName=checkout-api&connections=4&allowAdmin=false
```

Supported query parameters are `clientName`, `connections`, `connectTimeoutMs`, `commandTimeoutMs`, `responseTimeoutMs`, `protocol` (`auto`, `2`/`resp2` or `3`/`resp3`, case-insensitive), `db`, `cluster`, `allowAdmin`, `keyPrefix`, and `pubSubPrefix`.

`keyPrefix` configures a [default root key namespace](../commands/strings-and-keys.md#default-root-namespace).
It accepts percent-escaped UTF-8 text in URI and comma-delimited strings, decodes once,
and leaves pub/sub channels unchanged. Empty disables prefixing; repeated entries use
the last value. Use `Uri.EscapeDataString` to escape delimiters and literal percent signs.
Configure arbitrary binary prefixes through `RespireOptions.KeyPrefix`.

`pubSubPrefix` independently configures the [root pub/sub namespace](../guides/pub-sub.md#explicit-pubsub-prefixes).
It follows the same decode-once, last-entry, and empty-value rules. Use
`RespireOptions.PubSubPrefix` for binary bytes. Typed publication and subscription methods
apply it; notification descriptors and Server PUBSUB queries retain explicit physical names.

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
| `sentinel-a,sentinel-b,serviceName=mymaster` | Sentinel discovery endpoints used to select and validate the current primary. |
| `cache-a,cache-b` | Rejected: the endpoints could belong to unrelated standalone deployments. |

Use `ConnectAnyAsync` with separate `RespireOptions` candidates for connection-time fallback
between independent deployments. It does not perform continuous failover; use a
[failover group](../guides/failover-groups.md) for that. Sentinel
discovers the primary on connection or first use, then discovers a replacement after a
disconnect or READONLY rejection. Ordinary standalone reconnection targets the deployment already selected. Cluster routing
is distinct from either standalone fallback or Sentinel discovery.

An optional [`ReconnectPolicy`](../guides/reconnect-policy.md#sentinel-discovery-fallback)
bounds and delays Sentinel fallback candidates after the first. Configured seeds, learned
peers, and failed primary ROLE validation share that resolution budget. Each new Sentinel
resolution uses this policy, and so do the Sentinel event monitors described below.
Cluster uses the same option for
[node, topology, and seed fallback](../guides/reconnect-policy.md#cluster-discovery-fallback),
with one shared budget per discovery round. Periodic Cluster refresh remains separate.

Programmatic `RespireOptions.Endpoints` follows the same rule: standalone mode requires one
endpoint. Lists that previously left extra standalone endpoints unused now fail validation.
Cluster and Sentinel cannot both be selected in one comma-delimited string.

Supported options are `user` (or `username`), `password`, `ssl`, `sslHost`, `sslProtocols`,
`checkCertificateRevocation`, `clientName` (or `name`), `defaultDatabase` (or `db`),
`connectTimeout`, `asyncTimeout` (or `syncTimeout`), `protocol` (`auto`, `resp2` or `resp3`), and
`allowAdmin`, `keyPrefix`, and `pubSubPrefix`. `sslHost` sets the TLS certificate/SNI target and enables TLS unless
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
wanted. An endpoint list never silently enables StackExchange.Redis-style connection groups or
continuous failover; configure a [failover group](../guides/failover-groups.md) explicitly. See the [StackExchange.Redis option reference](https://seredis.dev/Configuration.html)
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

Cluster topology refresh runs every 60 seconds by default, which is a behavior change for
existing Cluster clients. The background worker starts after the first connection, so `Create`
stays lazy. Each periodic refresh sends one `CLUSTER SLOTS` to one node, and the interval is
shortened by up to 10% of random jitter so that many clients started together do not refresh in
step. On large fleets, a longer interval reduces `CLUSTER SLOTS` load.

Set `ClusterTopologyRefreshInterval` to `null`, `TimeSpan.Zero`, or `Timeout.InfiniteTimeSpan` to
disable the periodic timer. This disables only the timer. The router still refreshes when:

- a primary connection is lost (at most once per second while a primary keeps failing to reconnect,
  and no sooner than the failure retry below while one is pending);
- a `MOVED` redirect arrives (debounced for 5 seconds from the first redirect, so a stream of
  redirects cannot postpone the refresh);
- a refresh failed (retried with backoff from 5 to 60 seconds until one succeeds). While a retry
  is pending, redirect-driven, periodic and primary-disconnect refreshes wait for it, even when the
  periodic interval is shorter than the backoff.

These timings are fixed. A redirect-driven refresh reuses a refresh that succeeded within the last
5 seconds. Refresh work triggered by redirects, disconnects, or concurrent `READONLY` recoveries is
coalesced into one flight. One refresh pass is bounded to 60 seconds whatever the interval, tries
connected nodes and configured seeds before other known nodes (later fallbacks are tried in a
different order on each pass), and keeps the last published slot map when discovery fails. A
partial `CLUSTER SLOTS` reply, for example from a cluster with `cluster-require-full-coverage no`
that has lost a shard, updates the slots it covers and keeps the previous owners of the rest. Replica endpoints, node IDs, and aliases from `CLUSTER SLOTS` stay current
in router metadata and are used as refresh fallbacks when every primary and seed fails; command
routing uses primaries by default. Read policies can select replicas for eligible commands,
as described in [Read from replicas](#read-from-replicas).

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
before the generation is published. Every new data connection is validated, including
dedicated and subscription connections. A reachable replica, malformed response, or denied `ROLE`
is rejected and the candidate is disposed. Grant `ROLE` to the data-node credentials.

Respire also requests `SENTINEL SENTINELS` and can try up to 64 learned peers after the
configured endpoints. Duplicate hosts/ports and invalid peer addresses are ignored.
Configured seeds and learned peers remain known for the client lifetime; the learned-peer
cap also bounds their event monitors. Newly learned peers do not recursively expand discovery
within the same attempt. ACL errors, timeouts, protocol errors, or disconnects during this
optional peer-list command do not discard an already completed primary reply. Discovery (including the peer-list request) uses
its existing bounded deadline; primary hostname resolution, setup, and role validation receive a fresh connection
deadline. Caller cancellation applies throughout. These checks follow the
[Redis Sentinel client specification](https://redis.io/docs/latest/develop/reference/sentinel-clients/).

Sentinel discovery always uses RESP2, so older Sentinel nodes can discover a RESP3 primary.
Transport settings inherit from the primary by default. Set `SentinelUseTls` independently when
Sentinel and the primary use different TLS modes, and set `SentinelTlsOptions` when Sentinel needs
different certificate validation or a different `TargetHost`. In a URI, `sentinelTls=false`
selects plaintext Sentinel discovery even when the `rediss://` primary uses TLS.

`RespireClient.Create(options)` performs no network I/O. Its first operation discovers and
validates the primary; `ConnectAsync` performs the same work eagerly. Prefixed views keep
sharing the same client core across primary changes.

A disconnect, READONLY reply, or a ROLE response identifying a replica retires the affected
generation. The next operation resolves Sentinel again and publishes a validated replacement,
even when Sentinel returns the same endpoint after a brief disconnect. This deliberately
revalidates the primary role before accepting new work; there is no reconnect grace period.
EXEC and script array replies are scanned for nested READONLY errors in time proportional to
their elements. Ordinary collection reads do not perform this additional scan.
Fire-and-forget replies retain their operation identity for the same checks, while the call
still completes after writing rather than waiting for a reply. This metadata uses a bounded
array allocated on the connection's first fire-and-forget command; ordinary connections do
not allocate that array.
New commands cannot enter a retired generation. Already accepted commands and blocking
operations drain on their original sockets; ambiguous writes and existing WATCH state are
never replayed. Start a new watched transaction after a failover. Client disposal aborts
outstanding work and joins owned connection cleanup.

On RESP3 connections with maintenance notifications enabled, retirement freezes command
admission before sending a PING barrier. If that barrier fails, accepted commands get their
`CommandTimeout` allowance, extended through an active maintenance relaxation. When
`CommandTimeout` is disabled, retirement uses a 30-second minimum grace, extended if the
configured maintenance relaxation is longer. A streamed reply keeps the connection while its
reader makes progress; retirement closes it after the same idle grace if the reader stalls.

Client-side cached reads lose continuity on retirement. Cached MGET and opted-in partial HMGET
reads discard all cached elements if the generation retires during lookup, then refetch the
complete request from the validated primary. Subscriptions reconnect to the new
primary and report their normal delivery gap; Redis cannot replay missed publications.
Explicit server connections remain pinned to the endpoint selected by the application.
Correction operations retain their original physical peer rather than following a new
primary. `Endpoint` reads host and port from one generation snapshot; a later handoff can make
that snapshot historical, but cannot combine fields from different primaries. Before a lazy
Sentinel client has resolved and validated its first primary, `Endpoint` throws
`InvalidOperationException`; it never returns a Sentinel discovery address as the data endpoint.
Reading the property performs no I/O. Use `ConnectAsync`, or await the first command on a client
created with `Create`, before reading it. Failed discovery leaves the endpoint unavailable.
`ConnectionStateChanged` reports endpoint changes, and the `Respire` meter records
`respire.sentinel.failover` for validated primary endpoint changes, tagged with `server.address`
and `server.port`. State observers may dispose the client synchronously. Disposal suppresses
queued notifications but does not wait for an observer already running; that callback may
finish after disposal returns. The process-wide `respire.sentinel.generations.retired` gauge
counts retired generations still owned while accepted commands, borrowed leases, or correction
fences drain. A nonzero value can be expected during handoff; a value that keeps growing
indicates retained work to investigate. There is no forced drain deadline that abandons
accepted commands or an unacknowledged correction fence. Retention preserves ownership of
that server-side ordering obligation; a time or attempt cap could discard it before the old
server acknowledges the fence. Repeated failovers during an outage can therefore grow retained
state until the fences succeed or client disposal aborts cleanup.

Respire also subscribes to `+switch-master`, `+sdown`, and `+odown` on configured and discovered
Sentinels after the first primary is validated. A client whose initial discovery has not yet
succeeded has no monitors; each command runs discovery itself until one does. A Sentinel learned
later is monitored as soon as discovery finds it.

Each monitored Sentinel costs one extra TCP connection: a single-connection RESP client that
holds only that subscription, with no client name, client-side cache or maintenance
notifications. Discovery remembers at most 64 learned Sentinels beyond the configured ones, which
bounds the number of monitor connections.

These events are hints: Respire resolves the service again and confirms the candidate with `ROLE`
before publishing a replacement. When Sentinel still names the current primary, `ROLE` is checked
on the existing connection and no new connection is opened. A `+switch-master` retires the current
generation before rediscovery only when its old address matches that generation, by announced
endpoint or by the connected peer address. Hints for other services and replica events never
start discovery.

Notification-driven discovery starts at most once per 100 ms, even when repeated fault hints
keep arriving and each discovery succeeds. Hints received during that interval remain coalesced.
Command-triggered discovery is not delayed by this notification rate limit.

Discovery also reads `SENTINEL MASTER` to associate the reported address with its `config-epoch`.
After observing an epoch, Respire rejects older configurations and conflicting addresses at the
same epoch. All retained reporters are queried, so a later promotion can supersede an intermediate
primary without letting a stale reporter roll the client back. Grant the Sentinel credentials
permission to run `SENTINEL MASTER` for this reconciliation. If metadata is unavailable, an existing
primary matching the observed epoch can still be confirmed. If no epoch has ever been available,
discovery continues using `ROLE` validation and switch evidence, including after delivery gaps.
Missing epoch metadata emits one warning per client. Once an epoch is observed, a failed connection
attempt does not discard it: older fallback replies cannot restore a stale primary.
Owner comparison recognizes resolved hostname/IP aliases and canonical IPv4/IPv6 spellings,
so an unavailable hostname transport does not fence out the same owner reported by its IP.
Reporter reconciliation retains evidence naming demoted primaries when configuration metadata
is unavailable; a stale reporter cannot restore one merely because it still answers `ROLE master`.

Epoch metadata is needed to order completed failover cycles reliably. A `+switch-master`
payload has no epoch or sequence number. After A-to-B and B-to-A, a delayed A-to-B report can
look identical to a genuine third transition when both servers still answer `ROLE master`.
The metadata-free fallback permits genuine recurrence, so it cannot reject every delayed cyclic
report. Grant `SENTINEL MASTER` permission when this ordering guarantee is required; ROLE alone
does not prove global primary ownership.

Discovery uses Sentinel-specific credentials and TLS settings. Monitor subscriptions reconnect
independently under the client's `ReconnectPolicy`, reported with
`respire.reconnect.scope = sentinel-monitor`. A monitor that exhausts the policy resumes with a
fresh budget after a validated primary publication, including one that lands during its final
retries. Pub/Sub delivery is at-most-once, so a
monitor that subscribes, reconnects, or reports a delivery gap triggers one rediscovery to catch a
missed switch. Sentinel sends each event only once, so a failed event-triggered rediscovery retries
with backoff. Without a `ReconnectPolicy` it retries until discovery succeeds or the client is
disposed, waiting at most 30 seconds between attempts. A policy's `MaxAttempts` bounds those
retries. The first failure in a run is logged as a warning and later ones at debug level, so a
long Sentinel outage does not repeat the warning on every attempt. Later commands also still use the reactive discovery path. Client disposal stops monitor
work and waits up to 10 seconds for it, logging any task that does not stop. Accepted commands are
never replayed during handoff.

## Read from replicas

Set `RespireOptions.ReplicaEndpoints` for a standalone primary/replica deployment. Sentinel
clients discover replica endpoints with `SENTINEL REPLICAS`, using the first Sentinel that gives
a well-formed answer, in the same order as primary discovery. Replies from different Sentinels
are not merged, because during a failover or partition a stale Sentinel can still list replicas
that the others have dropped. A reply with a malformed entry counts as a failed Sentinel; an empty
list is accepted and removes every replica. Existing multi-endpoint standalone
configuration keeps its current validation and connection-time fallback behavior.

`RespireOptions.ReadFrom` sets the default policy. `WithReadFrom` creates a per-view override;
it composes with `WithKeyPrefix`. Policies apply only to commands whose catalog metadata marks
them read-only, including eligible blocking reads such as `XREAD BLOCK`. For standalone and
Sentinel clients, caller-defined commands, writes, blocking commands that modify data,
subscriptions, batches, and transactions stay on the primary. `Replica` fails when no validated replica is available.
`PrimaryPreferred` uses a replica when primary connection acquisition fails; `ReplicaPreferred`
uses the primary when replica selection fails.

Eligible blocking reads rent a separate, reusable connection from the selected endpoint, so
waiting for a reply does not occupy the multiplexed connection. Replica leases validate `ROLE`
before admission. Removing a replica prevents new leases and drains accepted reads; disposing
the client aborts them. Caller cancellation discards the blocked lease and preserves the caller's
token. Blocking reads retain their response-timeout exemption.

For blocking reads, a preferred policy can switch server roles once after `LOADING`,
`MASTERDOWN`, or `CLUSTERDOWN`. Fallback stays in that role and retains the original rejection
if no fallback connection can be acquired. A private connection deadline can try remaining
eligible candidates; caller cancellation never triggers fallback. Nearest reselects by latency
after a dedicated connection fails, retaining the failed endpoint's cooldown.

### Nearest reads

`RespireReadFrom.Nearest` selects the lowest measured latency among eligible primary and replica
connections. It works with configured replica endpoints, Sentinel, and Redis Cluster slot routes.
Writes and unknown commands retain primary routing. Like other replica-capable policies,
Nearest can return stale data and does not guarantee read-your-writes consistency. Its views
bypass the primary tracking cache, and cursor reads keep their original endpoint.

Measurements use advisory `PING` commands on the physical connections that can serve the read.
Each router starts at most four probes concurrently, with no waiting probe queue. Each connection
starts at most one probe per second. All candidates and any topology retry share one second of
sampling wait time per selection. Connection establishment retains its configured timeout. A probe
that exceeds this budget still occupies its probe slot until its reply or connection failure:
Respire does not queue repeated PINGs behind a stalled one, even with `CommandTimeout = null`.
An unanswered probe also excludes that connection from Nearest selection until its FIFO reply
completes, so an unsampled healthy candidate can serve the read.
Sampling happens only
when Nearest reads request it; ordinary Primary reads create no sampler and send no sampling PINGs.
The first successful sample establishes the estimate. Later samples use one quarter of the new
measurement and three quarters of the previous estimate to reduce jitter.

A sample younger than ten seconds can serve selection immediately when no probe is outstanding.
The warm cached path takes no shared sampler lock and allocates no memory. Cold or pending
probe waits can allocate; the zero-allocation guarantee applies only to warm cached selection.
If a new probe is pending, selection waits within the shared one-second sampling budget even
when the previous estimate remains fresh; it cannot send a read behind an unanswered PING.
A cold or expired sample also waits for an available shared probe within that remaining budget. Caller cancellation
stops that caller's wait without canceling the shared probe. When all probe slots are busy,
unsampled candidates remain eligible with unknown latency. Measured candidates take precedence
over unknown candidates; equal estimates, or entirely unknown estimates, rotate selection order.
If every candidate has a pending probe, the bounded retry ends with a connection exception
reporting that no healthy eligible endpoint is available; no read is queued behind those probes.
PING failure or ACL denial removes the estimate without disqualifying an otherwise healthy,
role-validated connection. Connection failures use a cooldown before retrying; configured and
Sentinel replica cooldowns follow `ReplicaRefreshInterval`, while primary and Cluster candidate
cooldowns last one second. Replaced physical connections start with fresh estimates, and removed
candidates cannot win a new selection. Existing connection draining and command replay rules apply.

When a healthy primary is available, initial Sentinel or Cluster replica discovery runs in the
background. Reads can use that primary until replicas are known. Cluster replica refresh uses
temporary topology connections once Nearest sampling is active, so a stalled `CLUSTER SLOTS`
reply does not block the primary's data connection. Shared discovery still coalesces requests and
applies the existing refresh throttles and topology version checks.
Each uncovered Cluster slot keeps its own background waiter, so a partial reply for another
slot does not suppress its discovery. If cached Sentinel or Cluster candidates all fail, selection
joins a pending or due refresh and retries once before reporting failure. This retry includes
endpoints that recovered without changing address. A concurrent topology publication also gets
one retry when it replaces every captured candidate, under the original sampling budget.
Healthy cached candidates continue serving reads while that refresh runs.

Prepared Cluster replica selection does not allocate refresh callbacks until a refresh is
needed. Optimized Release allocation controls cover healthy `Replica`, `ReplicaPreferred`, pinned replica
cursor routes, and `Nearest` routes with cached latency samples, plus a primary control.
They do not claim that a public read, connection establishment, discovery, an expired
sample, or a topology retry is allocation-free. Replica revalidation and sampling intervals
are unchanged. Debug builds also validate routing correctness, but compiler-generated async
state-machine objects still allocate in that configuration.

The `run-cluster-replica-benchmarks` pull request label compares the pinned merge and its
actual base on one net10.0 runner, using the same real primary/replica cluster and fixture.
It brackets the candidate with two baseline runs and measures prepared routing, public
replica GETs at one and fifty concurrent reads, and matching primary controls. Review
latency confidence intervals, allocations, dispersion, and CPU per operation before
accepting a performance change. CPU counters include the entire client benchmark process
after setup, including calibration, warmup, background refresh and response completion;
they do not isolate measured iterations or Redis server CPU. Dry runs establish fixture
correctness only.

The [selected cluster comparison](https://github.com/thomhurst/Respire/actions/runs/37707575496)
removes 80 bytes per prepared replica selection and reduces its measured latency by
17–19% and whole-process client CPU per operation by 15–16% against both controls.
Public replica GET allocation falls by about 80 bytes, but its latency confidence
intervals overlap and its CPU counters do not establish an improvement. The primary
prepared control costs an additional 0.40–0.66 ns; public primary GET latency intervals
overlap. These results describe prepared routing, not a general read-throughput or
server CPU improvement. Discovery, sampling and refresh intervals remain unchanged.

PING round-trip time includes local connection queues, server scheduling, and network delay.
It does not measure geographic distance, replication lag, or the execution time of a particular
read. Configured and Sentinel candidates still follow the replication-link preference described
below, before comparing latency. Zone affinity is separate from this policy.

Unless hedged reads are explicitly enabled, fallback happens only while a connection is being selected. Once a command has been written to a
replica or the primary, a failure is returned to the caller and the command is not sent again,
matching the rest of Respire: a command accepted by a failed connection is never replayed.

### Hedged reads

Set `HedgedReads` to duplicate a slow, audited idempotent read onto one other eligible server.
It is disabled by default. The first successful reply wins; a losing reply is drained and disposed
without delaying the caller. If both requests fail, the original request's error is returned.

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

`Delay` is a fixed threshold between 1 ms and one minute, starting after initial connection
selection. Choose it from your observed latency distribution; Respire does not automatically
track command p95/p99. `Nearest` continues to use its existing per-connection PING EWMA to select
the original endpoint. Optional connection discovery or handshake cannot delay an original reply.

`Primary` never hedges. `Replica` can hedge only onto another replica. `PrimaryPreferred`,
`ReplicaPreferred`, and `Nearest` can hedge onto either role, using a different physical peer.
`AzAffinity` and `AzAffinityReplicasAndPrimary` keep their zone and role ranking when selecting
that other peer. Cluster selection excludes the original attempt's current peer after redirects
or role fallback, and optional retries cannot re-enter that peer while the original remains there.
Configured and Sentinel replicas must pass the same role validation as ordinary replica reads;
Cluster candidates must belong to the key's current slot topology. All replica-capable policies
can return stale data, including a replica hedge that beats a primary read.

The client shares one budget across all its views. Each eligible logical read adds credit after
initial endpoint selection, including fast reads and reads with no second eligible peer;
starting a hedge consumes it. Primary-only reads and excluded commands do not add credit.
At 5%, at least twenty eligible reads fund one hedge. The budget
starts empty and stores at most one hedge, so fast reads cannot accumulate an unbounded burst.
Concurrent reads may use less than the configured maximum. This bounds additional hedge starts,
not bytes, server CPU, topology probes, or redirects. A saved credit can be spent in a later
reporting interval; the percentage is a bound over the client's lifetime, not every time window.

Only buffered catalog reads with audited idempotent semantics are eligible. Writes, scripts,
random selections, cursors, blocking commands, batches, transactions, streamed replies, and
unknown commands are excluded. Cluster reads also require a known slot. Eligible commands include
`GET`/`MGET`, deterministic hash/list/set/sorted-set reads, geo lookups, and stream range reads.
Argument bytes are copied when a request can hedge, because a losing request may outlive the caller.
This opt-in path is not zero-allocation: asynchronous originals can also allocate tasks, timers,
and linked cancellation sources. A synchronously successful original avoids conversion to a race
task and timer setup, but still needs its argument snapshot. Deferring that snapshot until the
timer fires would leave an original request waiting for admission with caller-owned bytes after
a hedge returns. Reads without credit or without a possible second replica under strict `Replica`
routing use the ordinary awaited send path.
Accepted losing requests retain their normal timeout and FIFO response slot; hedging does not
cancel them when another reply wins.

Monitor `respire.read.hedge.sent`, `respire.read.hedge.won`, and `respire.read.hedge.extra_load`
through the `Respire` meter. A reproducible real-Redis latency probe is documented in
[`tools/Respire.StressTests`](https://github.com/thomhurst/Respire/tree/main/tools/Respire.StressTests).

### Validation and staleness

For standalone and Sentinel clients, Respire validates each replica connection with `ROLE` before sending reads to it, and rejects a
node that does not report the `slave`/`replica` role. `ROLE` also reports the replica's link to
its primary. A replica whose link is `connected` is always chosen over one that is still
connecting or syncing, because an unlinked replica can serve arbitrarily stale data. When no
linked replica is available, as during a primary outage, Respire still uses an unlinked replica;
the server's `replica-serve-stale-data` setting decides whether it answers or rejects the read.

`RespireOptions.ReplicaRefreshInterval` (default one second) bounds how stale the replica
topology can be:

- A connection's `ROLE` check is reused for one interval, so a node promoted to primary can keep
  serving reads for up to one interval. Under heavy load one caller revalidates while others keep
  using the previously validated connection, which can extend the window to two intervals.
- Sentinel clients refresh the replica set in the background at most once per interval and keep
  the last known set when no Sentinel answers. During a Sentinel outage the retry delay doubles
  after each failed attempt, up to 30 seconds (or the interval, if that is longer), and returns to
  one interval once Sentinel answers. Respire logs a warning when Sentinel stops answering and an
  informational message when it recovers.
- A replica that fails a connection attempt or a `ROLE` check is skipped for one interval, so a dead
  replica does not add a connect timeout to every read.

`TimeSpan.Zero` revalidates on every read and disables the cooldown.

Uninstrumented typed reads can dispatch directly on a prepared standalone replica when the
view uses `Replica` or `ReplicaPreferred`, the current topology has exactly one replica,
one physical connection is configured, and that connection has a fresh successful `ROLE` check with a connected
replication link. A cached single-replica entry is tied to the exact endpoint publication.
Topology publication, entry removal and router disposal invalidate that identity under the
router lifecycle gate. Warm selection checks the publication identity instead of repeating
endpoint dictionary lookups; connection health is still checked on every read.
Selection rechecks the current endpoint publication, entry identity and
connection admission state. String, binary and scalar replies use their specialized pooled
sources. Cache coordination, telemetry listeners, hedging, cursor affinity, multiple replicas,
multiple sockets, zone policies, and `Nearest` retain their existing routing paths. Sentinel
discovery still runs when its refresh interval expires. Readiness does not lease a socket;
connection retirement and command admission continue to enforce their normal lifetime rules.

Prepared single-replica reads still advance the shared atomic rotation counter. A topology-growth
regression checks that selection across multiple replicas resumes from that cursor. The counter
is retained to preserve this behavior; no counter-only performance improvement is claimed.
The prepared-read comparison isolates fresh validation and dispatch. Its results do not isolate
the counter cost, measure periodic `ROLE` checks or establish performance for every routing policy.
It exercises string `GET` calls and prepared route selection, with matching primary controls.

The [selected pinned net10.0 comparison](https://github.com/thomhurst/Respire/actions/runs/37728047447)
brackets the candidate with two baseline runs on the same runner and Redis primary/replica pair.
Prepared routing and concurrent replica reads improve, and public allocations fall, but serial
replica `GET` costs 160,445.36 ns of client process CPU per operation against 160,052.63 and
161,553.49 ns in the bracketing baselines. The serial row fails the required improvement against
both baselines; overlapping launch ranges do not establish that improvement.
[Issue #1186](https://github.com/thomhurst/Respire/issues/1186) remains open for CPU acceptance.
The publication cache needs its own unchanged six-row comparison, including primary controls,
allocation, latency dispersion and CPU per operation. No improvement is claimed for that cache
before the comparison passes and its distributions are reviewed. The comparison covers prepared
string reads and route selection, not every routing policy or server CPU.

A replica removed from the topology stops receiving new reads at once. Its connections stay open
for up to one second, then drain the commands they already accepted before closing. The drain
waits for every accepted command, including a `GetStreamAsync` reply that is still being consumed;
each command remains bounded by its own `CommandTimeout`, so retirement does not cut a read short
while it makes progress. A streamed reply that receives no data for 30 seconds (or
`CommandTimeout`, if that is longer) is treated as abandoned, for example a stream that was never
read or disposed: the replica then closes and the stream fails. Disposing the client closes any
replica that is still draining.

Completed replica retirement failures retain up to 64 distinct exceptions until client disposal.
Disposal reports those exceptions plus a count of additional failure occurrences whose details
were not retained. Debug logging can capture individual retirement failures as they happen.
Cleanup still in progress remains owned and joined by disposal.

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

Affinity works with standalone replica endpoints, Sentinel discovery, and Redis Cluster.
Zone names use ordinal, case-sensitive comparison. The selection order is:

| Policy | Selection order |
| --- | --- |
| `AzAffinity` | Same-zone replicas, other replicas, primary |
| `AzAffinityReplicasAndPrimary` | Same-zone replicas, same-zone primary, other replicas, primary |

The existing replica health checks still apply: a linked replica takes precedence over
an unlinked replica. For `AzAffinityReplicasAndPrimary`, a same-zone primary also precedes
unlinked replicas, including same-zone replicas. If no linked replica or preferred primary
is available, unlinked same-zone replicas precede other unlinked replicas.
Writes and operations that already require the primary keep their
existing routing. Replica reads can return stale data regardless of zone.

Valkey servers configured with `availability-zone` advertise `availability_zone` in
`HELLO` or `INFO SERVER`. Respire captures that metadata for each physical connection.
RESP2 clients with `ClientAvailabilityZone` configured request `INFO SERVER` during
the handshake. Missing metadata or an ACL denial leaves the zone unknown; such replicas
remain eligible in the other-replica tier. Redis deployments without this metadata
therefore retain replica-first fallback behavior. Reconnecting refreshes the metadata;
changing the server setting does not change an already established connection's zone.

Both affinity policies require a nonempty `ClientAvailabilityZone`, including when
selected through `WithReadFrom`. Selection respects cancellation and existing timeout,
cursor-pinning, retirement, and accepted-command ownership rules.

Pinned cursors retain the zone preference on every page while staying on the server that
issued the cursor. When a dedicated read pool has mixed zone metadata, rental prefers a
healthy same-zone idle connection compatible with the operation. If none is idle, normal
rental or connection establishment preserves availability; it does not open extra
connections merely to search for a matching zone. Role fallback retains this
physical-connection preference after narrowing the read to its fallback role.

Blocking reads such as `XREAD BLOCK` select an endpoint using the read policy, then rent a
dedicated connection from that endpoint. This applies to configured replica groups,
Sentinel, and Cluster. Standalone and Sentinel replica leases validate their own socket's
`ROLE` before use. Blocking waits do not occupy the multiplexed read connection or inherit
the normal response timeout; caller cancellation still ends the wait and discards its
socket. Removing a replica drains accepted blocking reads, while client disposal aborts them.

### Cursor reads

`SCAN`, `HSCAN`, `SSCAN`, `ZSCAN` and `ARSCAN` cursors are only valid on the server that issued
them. `Keys.ScanAsync`, `Hashes.ScanAsync`, `Sets.ScanAsync` and `SortedSets.ScanAsync` pin each
enumeration to the server that served its first page, so concurrent scans spread across replicas.
If that server leaves the topology or fails mid-enumeration, the enumeration throws a
`RespireConnectionException` instead of continuing a cursor on another server; start a new scan.

Raw cursor commands sent through `ExecuteAsync` share one pinned server per read policy, because
Respire cannot tell which enumeration a raw cursor belongs to. That pin is dropped when its server
leaves the topology or fails. A raw `SCAN`, `HSCAN`, `SSCAN` or `ZSCAN` with cursor `0` then
selects a healthy server, but a command that continues a cursor (any other cursor value) throws a
`RespireConnectionException` rather than sending the cursor to a server that never issued it;
restart that scan with cursor `0`. Respire does not know where `ARSCAN` keeps its cursor, so a raw
`ARSCAN` always reselects once its pin is dropped.
Fresh Cluster cursor-`0` calls also revalidate replica routes when due; continuation pages keep
their existing node affinity.

Fire-and-forget commands follow the same policy: a catalogued read sent with
`ExecuteFireAndForgetAsync` goes to the server the policy selects.

### Consistency

Replica reads can be stale and do not provide read-your-writes consistency. Both
preferred policies can return stale data whenever they select a replica, including immediately
after a successful write. Choosing a primary on another call does not make the policy consistent.
Read views bypass client-side cache reads to avoid mixing primary-tracked cache entries with replica data, and a
replica disconnect does not flush the client-side cache. A read-only function that reports
`Function not found` on a replica causes the registered library to be checked or reloaded on the
primary, then retried under the original read policy until replication makes it available.
The missing-function retry loop has a five-second budget. A shorter `CommandTimeout` reduces
that budget; `null` does not disable it. The budget covers route acquisition and waiting
for connection capacity, with a final check before enqueueing. No new attempt is enqueued
after the budget expires. An attempt already admitted to its connection retains its normal `CommandTimeout` and caller
cancellation, so a function that exists can finish executing after the retry budget expires.
If that attempt returns `Function not found` after expiry, no further retry is sent.
Exhausting the retry budget throws
`RespireTimeoutException` for the function call. Caller cancellation stops the wait with
`OperationCanceledException`. Retries preserve the original read policy, so `Replica` never
falls back to the primary.
Replica connection health is reported through `ConnectionStateChanged` with the replica's
endpoint.

### Cluster routing

Cluster clients discover replicas per slot range from `CLUSTER SLOTS` and negotiate `READONLY`
on their replica connections. `ReadFrom` and `WithReadFrom` select among those routes. Strict
`Replica` reads fail if no replica route is usable. Preferred policies can fall back to the
other role on connection selection failures and server unavailability replies such as `LOADING`,
`MASTERDOWN`, and `CLUSTERDOWN`. Once a read switches roles, redirect and retirement recovery
keep that fallback role for the rest of the attempt.

Known read-only commands, including registered read-only functions and eligible blocking reads,
follow the selected policy. Writes, unknown commands, transactions, and cache-backed reads stay
on primary routes. `TOUCH` also stays on the primary because its purpose is to update that
server's access metadata. `MEMORY USAGE` reports the selected node's memory use; read-only scripts
(`EVAL_RO` and `EVALSHA_RO`) run on the selected node and need not return deterministic results.
A batch can use a replica only when all its operations are eligible reads.
Cursor enumerations remain pinned to the node that issued their cursor.

Replica refreshes are shared by concurrent callers for the same slot range. The refresh has a
total budget of `ConnectTimeout + CommandTimeout` (using `ConnectTimeout` again when the command
timeout is disabled). Known candidates are probed in parallel under that shared deadline, so
stalled nodes cannot consume the time available to healthy candidates. After a reply supplies
replicas for the requested slot, remaining probes are cancelled and observed. Empty replica
snapshots are published only if no candidate supplies replicas. A failed refresh keeps existing
routes, and partial replies preserve uncovered slot ranges.

## Cancellation and timeouts

Commands with a `CancellationToken` abandon the wait when cancelled; cancellation cannot guarantee the server did not execute a command already written to the socket. A `params` parameter must come last, so variadic `params ReadOnlySpan<T>` commands carry their token on a sibling overload that takes the items non-params followed by a required token — `DeleteAsync(keys)` for the convenient form, `DeleteAsync(keys, cancellationToken)` when you need cancellation.

A `RespireTimeoutException` can report an expired command-response budget or replica function
propagation budget. For response timeouts, treat writes as potentially executed and design
retries around operation idempotency. Function propagation expiry has a distinct message:
responses may have arrived, but the function remained unavailable on the selected replica.
Check library replication and replica health. Increasing or disabling `CommandTimeout` cannot
extend the five-second propagation ceiling. The exception's `Timeout` property reports the
budget that expired, which can be shorter than the configured response timeout.

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
`Buffered` is an observation, not a safe retry guarantee: a queued frame can still be sent
after caller cancellation or a timeout. A socket or TLS write can also transfer bytes before
the successful-write counter advances. Command retry metadata does not enable automatic
transport retries; an unknown or partial write remains ambiguous for a non-idempotent command.
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

Do not block a thread on a Respire result inside a continuation of another Respire command
(`.Result`, `.Wait()`, `GetAwaiter().GetResult()` on a pending command). Replies for one
connection are delivered in order, one after another, so the reply you are waiting for can sit
behind the continuation that is waiting for it. Respire detects this: when replies have waited
about 500 ms behind a continuation that is not making progress, it delivers them on another
thread and logs a warning ("Reply delivery ... was blocked by a continuation"). The caller
recovers, but it has paid that delay, and replies delivered this way run concurrently with
the blocked continuation instead of after it. If you see the warning, make the blocking code
path `await` instead.

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
