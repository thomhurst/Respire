---
title: Observability
description: Collect OpenTelemetry traces, metrics, logs, and connection state.
---

# Observability

Respire emits OpenTelemetry-compatible activities and metrics. The core client uses one
`ActivitySource` and one `Meter`, both named `Respire`. Companion packages use their own
names: `Respire.Streaming` for [stream workers](../guides/stream-workers.md) and
`Respire.SignalR` for the [SignalR backplane](./signalr.md).

## Traces and metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire"))
    .WithMetrics(metrics => metrics.AddMeter("Respire"));
```

Respire does not create or own an OpenTelemetry provider, listener, or exporter. Register
the source and meter with your own provider, as above. The Aspire integration does this for
you.

Instrumentation follows the OpenTelemetry database semantic conventions. Each operation
produces a `Client` span named after the command (for example `GET`), or after the command
and script digest or function name for scripts and functions. Spans carry:

- `db.system.name` = `redis`
- `db.namespace`: the configured Redis database index
- `db.operation.name`: the command name
- `server.address`, and `server.port` when it is not 6379
- `db.operation.batch.size` for pipelines and transactions
- `db.stored_procedure.name` for scripts and functions
- `error.type` and `db.response.status_code` on failure

Raw command values are never attached to spans, because arbitrary Redis payloads cannot be
reliably sanitized. A pipeline or transaction is recorded as one operation.

## Latency

Operation latency uses the stable `db.client.operation.duration` histogram, in seconds, as
the semantic convention requires. It is off by default: enable the `Command` metric group
to collect it.

## Metric selection

Redis metrics use one process-wide selection, shared by every client and prefix view. The
default is `Resiliency | ConnectionBasic`, following the Redis client observability
specification. Command latency, client-side cache, pub/sub and stream metrics require
explicit opt-in, even when an exporter subscribes to the `Respire` meter.

To collect every standard measurement:

```csharp
using Respire;

RespireMetrics.Configure(new RespireMetricsOptions
{
    Groups = RespireMetricGroups.All,
});
```

For narrower collection, select flags and exact command names:

```csharp
using Respire;

RespireMetrics.Configure(new RespireMetricsOptions
{
    Groups = RespireMetricGroups.Default | RespireMetricGroups.Command,
    CommandAllowList = ["GET", "SET", "CLIENT LIST"],
    CommandBlockList = ["SET"],
});
```

| Flag | Redis group | Measurements |
| --- | --- | --- |
| `Resiliency` | `resiliency` | Errors, maintenance notifications, geographic failovers, and endpoint circuit counters |
| `ConnectionBasic` | `connection-basic` | Ready connection counts, creation time, active maintenance timeout allowances and published handoffs |
| `ConnectionAdvanced` | `connection-advanced` | Pending replies, dedicated-pool acquisition waits and physical socket closes |
| `Command` | `command` | Logical operation duration, including Redis commands used for pub/sub and streams |
| `ClientSideCaching` | `client-side-caching` | Cache requests and evictions |
| `PubSub` | `pubsub` | Confirmed publications and received messages |
| `Streaming` | `streaming` | Stream lag, reported explicitly when application processing starts |

`Default` is `Resiliency | ConnectionBasic`, `All` selects every group, and `None`
suppresses all of these measurements. Respire-specific `respire.*` instruments are not
controlled by these groups (except the endpoint circuit counters); filter them in your
exporter if needed. Selecting a group does not invent a measurement that Respire does not
implement.

Configure metrics once at application startup, before creating clients or attaching
exporters. `Configure` validates and copies its input, then atomically replaces the
process-wide policy; the last call wins. Later changes to the supplied collections, or to the
copy returned by `RespireMetrics.Configuration`, do not change the active policy. A
replacement affects telemetry started afterwards: an operation keeps the selection it had
when it started, including while it waits for a connection. There is no per-client
override, because the meter is shared by the process.

`CommandAllowList` and `CommandBlockList` are `IReadOnlyCollection<string>` values. Record
`with` expressions share those collections, and record equality does not compare their
contents.

### Command filters

Command filters affect only command duration metrics, never command execution, traces, or
other groups:

- Names match ordinally, ignoring case. Use the complete reported command name, such as
  `CLIENT LIST`; there are no wildcards or prefix matches.
- An empty allow list allows every command. The block list always wins.
- Names must contain 1 to 64 ASCII characters: letters, digits, `.`, `_`, `-`, and single
  spaces between words. Null, empty, malformed, or longer names and unknown group flags
  throw `ArgumentException` before the configuration is replaced.

A pipeline or transaction emits one duration only when every command in it passes the
filters. Homogeneous operations are labelled `PIPELINE GET` or `MULTI SET`, but filters
match the constituent `GET` or `SET`, not the compound label. Mixed operations are labelled
`PIPELINE` or `MULTI`; excluding any member suppresses the whole duration. Empty operations
match `PIPELINE` or `MULTI` directly. Durability batches also require their `WAIT` or
`WAITAOF` acknowledgement to pass the filters. A script that falls back from `EVALSHA` to
`EVAL` remains one logical `EVALSHA` (or `EVALSHA_RO`) operation, and filters use that name.
Immediate, raw, blocking, streamed, and fire-and-forget operations follow the same rules.

### Labels and cardinality

Metric labels never include keys, raw channel or stream names, command arguments, payloads,
or credentials. Script digests and function names appear on spans but not on metrics, to
avoid one series per script.

Command labels are limited to 1,024 distinct names per process, ignoring case. Later names
share the label `OTHER`, as do empty or non-ASCII names and names longer than 80 characters.
The limit includes compound labels and does not reset when configuration changes. Filters
always match the original operation name, before this normalization: an allowed command
first seen after the limit still emits a measurement labelled `OTHER`, and allowing or
blocking `OTHER` does not select or suppress other commands. Allow-list entries do not
reserve label capacity. Other standard attributes identify configured endpoints, database
numbers, batch sizes, and errors.

When the command group is disabled, the command is excluded, or no listener is attached,
Respire skips timestamps and tag formatting for command metrics. Tracing may still need its
own timestamps. Duration ends when the reply arrives, before your code consumes or converts
the result. On some fast paths the measurement is recorded when the result is consumed, so
an unconsumed result does not emit a duration. Metric listener exceptions never replace a
command's result or error.

## Redis metric mapping

Standard signals use names, units, and attributes from the
[Redis client observability specification](https://redis.io/docs/latest/develop/clients/observability/).
The meter name remains `Respire`.

| Replaces | Instrument | Unit | Attributes and counting |
| --- | --- | --- | --- |
| `respire.client_cache.hits`, `respire.client_cache.misses` | `redis.client.csc.requests` | `{request}` | `redis.client.csc.result=hit` or `miss`; one measurement per cache lookup |
| `respire.client_cache.evictions` | `redis.client.csc.evictions` | `{eviction}` | Removed cached responses; optional `redis.client.csc.reason=full`, `ttl`, or `invalidation` |
| `respire.maintenance.notifications` | `redis.client.maintenance.notifications` | `{notification}` | `redis.client.connection.notification` identifies the notification; `server.address` and `server.port` identify its source |
| | `redis.client.geofailover.failovers` | `{failover}` | Switches between known failover deployments; `db.client.geofailover.reason=automatic`, `db.client.geofailover.fail_from`, and `db.client.geofailover.fail_to` |
| | `redis.client.errors` | `{error}` | Handled internal errors and final operation failures; see [error measurements](#error-measurements) |
| | `db.client.connection.count` | `{connection}` | Ready sockets by pool and idle/used state; exported as a non-monotonic sum |
| | `db.client.connection.create_time` | `s` | Physical connection setup, including handshake, by pool |
| | `db.client.connection.pending_requests` | `{request}` | Replies still owed on ready sockets, by pool; advanced group |
| | `db.client.connection.wait_time` | `s` | Dedicated socket acquisition; advanced group |
| | `redis.client.connection.closed` | `{connection}` | Physical closes by reason and pool; advanced group |
| | `redis.client.connection.relaxed_timeout`, `redis.client.connection.handoff` | `{relaxation}`, `1` | Current maintenance allowances and published replacement handoffs; basic group |
| | `db.client.operation.duration` | `s` | Logical operation latency and database attributes; command group |
| | `redis.client.pubsub.messages` | `{message}` | One per confirmed publication or accepted incoming frame; `redis.client.pubsub.message.direction` (`out`/`in`) and `redis.client.pubsub.sharded` |
| | `redis.client.stream.lag` | `s` | Entry timestamp to explicit application processing start |

The standard counters and the stream-lag histogram carry
`redis.client.library=Respire:<version>` and `db.system.name=redis`.

Cache evictions count actual removals caused by capacity limits, local expiration, or server
invalidation. One invalidated key can remove several cached responses or none. Local
mutation, explicit clearing, and continuity flushes do not increment this counter; see
[client-side caching](../fundamentals/client-side-caching.md) for the Respire-specific
invalidation and continuity-flush instruments. Cache statistics returned by the client are
independent of exported metric totals.

The old cache and maintenance instrument names have been removed, with no aliases or dual
emission. Update exporter filters and dashboards when upgrading. The broader
`respire.failover.endpoint.switches` counter also records initial selection and transitions
to or from having no healthy endpoint; the geographic failover counter excludes those events.

### Endpoint circuit metrics

Opt-in [endpoint circuit breakers](../guides/circuit-breakers.md) expose two counters on the
`Respire` meter:

| Instrument | Unit | Attributes and counting |
| --- | --- | --- |
| `respire.circuit.transitions` | `{transition}` | One per state change; `respire.circuit.state.from` and `respire.circuit.state.to` use `closed`, `open`, or `half_open` |
| `respire.circuit.rejections` | `{operation}` | One per rejected admission, including open circuits and exhausted half-open probe capacity |

Both require `RespireMetricGroups.Resiliency`, so `None` suppresses them. They carry
`redis.client.library`, `db.system.name=redis`, `server.address`, and `server.port` when it
is not 6379. Keys, payloads, exception messages, and credentials are excluded.

Batch entries are admitted independently, so each rejected entry adds one. A transaction
commit acquires one permit for `MULTI`/`EXEC`, so it adds one even if several pending results
receive the rejection. `WATCH` setup is a separate admission. A successful admission never
adds a rejection, but can report a transition, such as `open` to `half_open` when recovery
starts. Creating a circuit in the closed state and evicting idle circuit history add nothing.
These counters are independent of `redis.client.errors`, which can also count the circuit
exceptions that reach callers. Failover-group health probes report only their
`respire.failover.*` metrics.

### Other Respire instruments

Respire-specific instruments on the `Respire` meter are documented with their features:

| Feature | Instruments |
| --- | --- |
| [Pub/sub](../guides/pub-sub.md) | `respire.pubsub.delivery.gaps`, `respire.pubsub.messages.dropped` |
| [Client-side caching](../fundamentals/client-side-caching.md) | `respire.client_cache.invalidations`, `respire.client_cache.continuity_flushes`, `respire.client_cache.shared_read.retirements` |
| [Reconnect policy](../guides/reconnect-policy.md) | `respire.connection.reconnect.attempt`, `respire.connection.reconnect.delay`, `respire.connection.reconnect.exhausted` |
| [Command retries](../guides/command-retries.md) | `respire.command.retries` |
| [Batches and transactions](../guides/batches-and-transactions.md) | `respire.transaction.watch.conflicts`, `respire.transaction.watch.retries` |
| [Failover groups](../guides/failover-groups.md) | `respire.failover.*` |
| [Renewable credentials](../guides/renewable-credentials.md) | `respire.authentication.refresh` |
| [Coordination](../guides/coordination.md) | `respire.coordination.cleanup.abandoned` |
| This page | Connection measurement drops, availability zones, hedged reads, thread pool, Sentinel, maintenance and Cluster slot migrations |

Mapping these signals does not mean that every instrument or configuration group in the
Redis specification is implemented.

## Prometheus and the published Redis dashboard

The [observability sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Observability)
exports Redis measurements through OpenTelemetry's Prometheus ASP.NET Core exporter
`1.19.1-beta.1` (SDK `1.19.1`). It checks names, units, forms, and labels against every query in
[the published Redis dashboard at revision `033fe86e47440da7f365ed2d8dd7f5d6217a575a`](https://github.com/redis-developer/redis-client-observability/blob/033fe86e47440da7f365ed2d8dd7f5d6217a575a/grafana/dashboards/redis-client-observability.json),
and evaluates the adapted queries with promtool `3.5.0`. CI runs it on .NET 8 and .NET 10.
To reproduce it from the repository root:

```powershell
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -OutputDirectory ./observability-evidence
```

Prometheus replaces dots with underscores. Second-based histograms get `_seconds_bucket`,
`_seconds_sum`, and `_seconds_count`; monotonic counters get `_total`. Braced counting units
add no other suffix with this exporter. For example, errors become `redis_client_errors_total`
and stream lag becomes `redis_client_stream_lag_seconds_bucket`. Up/down instruments become
gauges. The sample sets latency histogram boundaries to
`0, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10` seconds instead of the generic defaults.

| Dashboard panels | Required group or feature | Compatibility and scope |
| --- | --- | --- |
| Connections, pool state/summary, creation time | Default `ConnectionBasic` | Ready physical sockets. Multiplexed socket state is not command concurrency. |
| Error rates/type/category | Default `Resiliency` | Includes handled internal failures and final operation failures. The command success formula is approximate: errors are not one failed logical command each. |
| Latency, throughput, success rate, heatmap | Opt-in `Command` | One sample per logical operation, including a pipeline or transaction, not per wire command. Cache hits need not execute Redis commands. |
| Pending requests, closes, wait time | Opt-in `ConnectionAdvanced` | Replies owed, physical closes, dedicated-pool acquisition. Wait time excludes multiplexed admission and general command latency. |
| Cache ratio, requests, evictions | Opt-in `ClientSideCaching` plus an enabled cache | Actual lookup and removal counters. An eviction is not an invalidation notification. |
| Network bytes saved, cached items | Unavailable | Respire does not emit `redis.client.csc.network_saved` or `redis.client.csc.items`. |
| Message rates | Opt-in `PubSub` plus publications or subscriptions | Replace `redis_client_pubsub_direction` with `redis_client_pubsub_message_direction`. |
| Message rate by channel | Opt-in `PubSub` | Channel names are absent. The adapted query groups by direction; it does not rank channels. |
| Stream/consumer lag | Opt-in `Streaming` plus explicit `RecordProcessingStart()` | Remove `redis_client_stream_name` and `redis_client_stream_consumer_name` grouping to show aggregate lag; these names are absent. |
| Geo-failovers | Default `Resiliency` plus a geographic deployment | Published switches between known endpoints only. |
| Timeout relaxations/handoffs | Default `ConnectionBasic` plus a server that sends maintenance events | Current increased allowances and published `MOVING` handoffs. Standalone Redis generates no such events. |

The original dashboard selects `exported_job` from its Collector and Prometheus setup. A
direct ASP.NET Core scrape supplies resource metadata through `target_info` instead. The
sample's adapted dashboard selects `db_system_name="redis"` and changes its service variable
to that label. For several services, use the scrape `job` or Collector resource labels to
keep services apart.

Panels that group by channel, stream, or consumer name cannot keep that grouping, and there is
no option to expose those names. The sample's repeated scrapes validate query compatibility,
not performance; production rates need successive live scrapes over a rate interval. The
`respire.*` diagnostics for reconnects, generic failovers, invalidation notifications, and
pub/sub gaps are not substitutes for the standard panels.

## Pub/sub messages and stream lag

Enable `PubSub` and/or `Streaming` in `RespireMetricsOptions.Groups`. Command filters do not
apply to these observations. Channel, pattern, stream, group and consumer names, entry IDs,
and payloads are never labels.

`redis.client.pubsub.messages` counts an `out` message when Respire receives a successful
integer reply to `PUBLISH` or `SPUBLISH`. A publication counts once, whether zero or many
subscribers received it. Typed, raw, and fire-and-forget publications all count. A caller that
cancelled can still contribute if its successful reply arrives later. Server errors, lost
replies, and publications from inside server-side scripts do not count. Deferred raw queues do
not accept `PUBLISH` or `SPUBLISH`.

An `in` message is one accepted channel, pattern, or sharded frame on a current subscription
connection. Local fan-out to several subscriptions counts once, even if their buffers drop
messages. Two distinct server frames, such as channel and pattern deliveries of the same
publication, count twice. Reconnect markers and rejected stale routes do not count. The
existing drop and gap instruments remain separate. Metric callbacks run outside subscription
locks; keep listeners short and non-blocking.

Both directions carry `redis.client.pubsub.sharded`. The group is checked when the message is
observed. A fire-and-forget publication counts only if `PubSub` collection was enabled when it
was queued; enabling collection later cannot count an earlier discarded reply.

The stream-lag histogram needs an explicit signal from your code:

```csharp
RespireMetrics.Configure(new RespireMetricsOptions
{
    Groups = RespireMetricGroups.Default | RespireMetricGroups.Streaming,
});

await foreach (var entry in redis.Streams.ReadGroupAsync(
    "events", group: "processors", consumer: "worker", cancellationToken: stoppingToken))
{
    entry.RecordProcessingStart();
    await HandleAsync(entry.GetString("type"));
    await entry.AckAsync();
}
```

Call `RecordProcessingStart()` once when each processing attempt actually begins, after any
application queueing. Reads, iterator delivery, claims, and acknowledgements do not emit lag.
Repeated calls produce repeated observations; the method does not acknowledge, change, or
deduplicate an entry.

Lag uses the millisecond part of a complete `milliseconds-sequence` ID and the client's UTC
clock. Use it only when IDs represent creation time, which is normally true for IDs generated
by Redis. Clock skew affects the result. Future timestamps and malformed or special IDs are
skipped rather than reported as negative or zero lag. With the group or listener disabled,
the method does no work. Listener failures cannot change publication, subscription, or
processing outcomes.

## Connection lifecycle

| Instrument | Group | Unit | Measurement |
| --- | --- | --- | --- |
| `db.client.connection.count` | `ConnectionBasic` | `{connection}` | Current ready TCP connections, split into `idle` and `used` |
| `db.client.connection.create_time` | `ConnectionBasic` | `s` | Successful socket connection, TLS and Redis handshake |
| `redis.client.connection.relaxed_timeout` | `ConnectionBasic` | `{relaxation}` | Connections whose command or response timeout is currently increased by a maintenance window |
| `redis.client.connection.handoff` | `ConnectionBasic` | `1` | One per old physical connection replaced by a successfully published `MOVING` handoff |
| `db.client.connection.pending_requests` | `ConnectionAdvanced` | `{request}` | Replies still owed on ready connections, including cancelled replies that still need draining |
| `db.client.connection.wait_time` | `ConnectionAdvanced` | `s` | Waiting for a newly created dedicated connection, including connection recovery; reusing a healthy idle connection emits nothing |
| `redis.client.connection.closed` | `ConnectionAdvanced` | `{connection}` | One per connected TCP socket that closes, including failed TLS or Redis handshakes |

These instruments carry `redis.client.library`, `db.system.name` and
`db.client.connection.pool.name`. A pool name combines the configured host, port, database
and purpose (`shared`, `dedicated`, or `pubsub`). Connections with the same identity
aggregate, even across separate clients. Pool names never contain credentials, keys or
payloads.

The process keeps at most 64 distinct pool identities, plus two overflow identities,
`overflow/shared` and `overflow/pubsub`. Names longer than 256 characters also overflow. The
first 64 identities are kept for the life of the process, and their zero-valued series remain
visible after their connections close. After endpoint churn fills the budget, new endpoints
use overflow even if the original endpoints have no live connections. Configuration changes
do not reset the budget; restarting the process does. Overflow aggregates endpoints and
databases but keeps the pub/sub distinction.

The count adds `db.client.connection.state` and `redis.client.connection.pubsub`. A
multiplexed connection is `used` while a reply is owed; a dedicated connection is `used`
while rented. Pub/sub connections are always `used`. A cancelled command stays pending until
its reply drains. A streamed reply stays pending until its payload drains, even if the caller
disposes the stream early. These are transport observations, not application tasks or pool
capacity. During replacement, old and new sockets can both count until the old one closes.
Connections still in their handshake, in-memory test streams and Unix sockets are excluded.

Counts, pending replies and timeout allowances are .NET `ObservableUpDownCounter<long>`
instruments, exported as OpenTelemetry non-monotonic sums and collected on the exporter's
normal interval. Late or re-enabled listeners therefore see current values without a replay
of past events. Group changes take effect at the next observation; event instruments emit
only events whose group was selected when they occurred.

Timeout allowances return to zero when the matching completion arrives, the maintenance
window expires, or the socket closes. They count connections, not requests that received
an extension. Duplicate notifications do not add allowances or handoffs. A failed or
superseded replacement emits no handoff, and ordinary reconnects count as creation and close
events, not handoffs. Handoffs include idle and borrowed dedicated connections from the
retired pool, including streaming leases, across standalone, Sentinel and Cluster routing.
Connections already closing or still negotiating when the handoff is published are excluded.

Close events add `redis.client.connection.close.reason`:

- `application_close`: intentional disposal, retirement, or caller cancellation during a
  handshake.
- `pool_eviction_idle`: dedicated idle-capacity overflow.
- `server_close`: the server closed or reset the connection (including a reset wrapped by TLS).
- `error`: transport or handshake failure, including an internal handshake timeout and
  unknown I/O failures. Error closes also include `error.type` and
  `redis.client.errors.category` (`network`, `tls`, `auth`, `server`, or `other`).

Respire has no health-check-driven pool eviction, so it never emits `healthcheck_failed`.
Close events do not count unsuccessful TCP connection attempts.

### Lifecycle event delivery

Connection instrumentation adds no metric callbacks to command submission. Creation and wait
durations, handoffs and closes are delivered to listeners on the thread pool. Durations are
measured at the event, so scheduling delay does not inflate them. At most 64 lifecycle
measurements can be queued or running across the process. Beyond that, or if queueing fails,
new measurements are dropped without blocking. Listener callbacks never hold up connection
acquisition, pending replies, pool cleanup, or disposal.

`respire.connection.measurements.dropped` is an observable counter (unit `{measurement}`, no
tags) with the cumulative number of dropped lifecycle measurements in the process, including
drops before any listener subscribed. It does not count listener exceptions or events lost at
shutdown. Reading it runs no callback on the transport path, so it cannot itself be dropped.

Because the delivery budget is shared by all clients and pools, a listener that blocks can
starve delivery for others; live observable counts are unaffected. An exporter can see live
state change before the matching events arrive, and queued events can arrive out of order.
Queued events are best effort: client disposal does not flush them and they can be lost at
process shutdown. A `MOVING` handoff counts the shared and dedicated sockets live when it is
published, so a lease closed afterwards still counts and a handshake completed afterwards
does not.

## Error measurements

`redis.client.errors` is a counter in the `Resiliency` group, independent of command
filters and latency collection. Each measurement is one error at an observed boundary. It is
not one failed command inferred from a disconnected socket, and it is not an exception
constructor hook: throwing or inspecting an exception elsewhere does not emit a measurement.
The group is checked when the error is reported.

Observed boundaries include connection establishment, typed and raw commands, scripts and
functions, blocking commands, streamed uploads and downloads, cached reads, batch execution,
transaction commit, and fire-and-forget submission.

### Attributes

| Attribute | Value |
| --- | --- |
| `redis.client.errors.category` | `network`, `tls`, `auth`, `server`, `cancelled`, or `other` |
| `error.type` | Full name of the exception type |
| `db.response.status_code` | A recognized Redis error prefix, such as `WRONGTYPE` or `NOSCRIPT` |
| `redis.client.errors.internal` | `true` for a handled error, `false` for a failure delivered to a caller |
| `redis.client.operation.retry_attempts` | Retries observed so far for the operation |
| `server.address`, `server.port` | The failed endpoint, when known; port omitted when 6379 |

Categories: TLS certificate failures use `tls`. Redis authentication and authorization
rejections (`NOAUTH`, `NOPERM`, `WRONGPASS`) and `RespireAuthenticationException` use `auth`.
Other Redis error replies use `server`. Socket, I/O, connection and timeout failures use
`network`; `RespireTimeoutException` means a client deadline expired, not necessarily a
network fault. Surfaced cancellation uses `cancelled`, because the client cannot tell who
requested it. There is no `timeout` category: filter by `error.type` to separate
cancellations and deadlines from outage alerts. Known wrapper exceptions are unwrapped for
classification, up to sixteen links deep, without changing the exception delivered to the
caller. In a deeper chain, the innermost server code may be absent.

Unknown error prefixes are omitted from `db.response.status_code` because scripts and
modules can put application data in them. Exception messages, credentials, keys, and payloads
are never labels.

`error.type` keeps the first 128 distinct exception type names in the process; later names
use `_OTHER`, and accepted names stay stable. This includes exceptions from application codecs
and generated generic types, so reuse exception types instead of creating one per operation.

Endpoint attributes use the same rules as `db.client.operation.duration`, so errors can be
grouped by node in standalone, Cluster, and Sentinel deployments. Handled retries identify
the failed attempt's endpoint; a final error identifies the last attempted endpoint. Errors
before a node is selected, such as argument validation or cancellation before dispatch, have
no endpoint attributes.

`redis.client.operation.retry_attempts` starts at zero and grows with each observed retry,
including redirects followed by script fallback. It is an exact non-negative integer, as the
specification requires. To cap series count, drop this attribute with an exporter metric view.

### Handled and final errors

`redis.client.errors.internal=true` marks an error that Respire handled: a Cluster redirect
or retry, a `NOSCRIPT` fallback, an unsuccessful connection candidate (including a replica
rejected by `ROLE`), a background reconnect attempt, a physical connection abort, or an error
reply discarded after fire-and-forget submission or caller cancellation. `false` marks a
failure delivered to a caller. A recovered `NOSCRIPT` produces only an internal measurement;
if the fallback also fails, that failure is a separate caller-visible measurement whose
retry count includes the earlier attempts.

How common operations are counted:

- **Connection loss.** A socket abort is counted once, as internal. Each in-flight command
  that then fails is counted separately at its own boundary; a disconnect does not create
  errors for idle sockets or completed commands.
- **Connections.** Connection-string parsing failures in `RespireClient.ConnectAsync(string)`
  count as final connection failures. `ConnectAnyAsync` reports a null or empty candidate
  list, an enumeration failure, or exhaustion once; its retry count is the number of
  candidates that failed.
- **Batches and transactions.** One caller-visible measurement per faulted deferred result,
  after cleanup. Reading `Result` again or calling `ThrowIfAnyFailed` adds nothing. An
  execution failure, such as a durability acknowledgement failure, is counted only when no
  deferred result represents it. An unexpected null `EXEC` in an unwatched transaction faults
  every queued result; in a watched transaction it returns `false` and is not an error.
  `WATCH` aborts and intentional disposal are not errors.
- **Fire-and-forget.** Submission failures are caller-visible. Replies the contract discards
  are internal.
- **Cached reads.** A final failure counts once per waiting caller, and each waiter inherits
  the shared producer's retry count. Cache hits are not errors.
- **Hedged reads.** Failures of a discarded hedge leg are internal, even when the leg finishes
  after the caller. The caller's final error carries the retry count of the leg whose result
  it received.
- **Streaming.** A streaming `GET` counts header failures at the command. After the stream is
  returned, its first payload failure counts once; repeated reads of the failed stream add
  nothing. Cancelling an individual read counts that cancellation without failing the
  payload. Invalid arguments and reads after disposal are not counted. Continuous reads such
  as `ReadAllAsync` count recovered failures as internal and a terminal failure once, with the
  total retry count. Ending enumeration early is not an error.
- **Pub/sub.** Background resubscription failures and redirects are internal. Cancellation
  caused by subscription shutdown is not counted.
- **Local validation.** Cross-slot rejections from multi-key commands, `WATCH`, and typed
  `XREAD`/`XREADGROUP` count once with zero retries, and no command is sent.
- **Node fan-outs.** Commands that return a result per node count each failed node before
  returning its error result; the parent does not count them again. Commands that run on all
  primaries (`SCRIPT`, `FUNCTION`, `DBSIZE`, `FLUSHDB`, `FLUSHALL`) report one final failure
  after every target finishes.
- **Locks and semaphores.** Lock contention is an error only when `AcquireOrThrowAsync` throws
  `RespireLockNotAcquiredException`. A semaphore capacity mismatch is reported once as
  `RespireSemaphoreCapacityMismatchException`. Cleanup failures swallowed by disposal are
  internal.
- **Distributed cache.** Final errors are reported after any required TTL correction or
  owner-only cleanup finishes, and describe the exception the caller actually receives.

Pending raw, cached, and fire-and-forget operations keep their final boundary even when
collection was disabled at dispatch, so enabling the group or attaching a listener before
completion can observe their final failure. Earlier errors are not replayed. Re-reading an
exception never creates a measurement. Listener exceptions cannot replace the caller's
exception, its cancellation token, or recovery behavior.

## Reads by availability zone

When `ClientAvailabilityZone` is configured, the `Respire` meter exposes the observable
counter `respire.read.availability_zone` (unit `{read}`). It counts read commands accepted by
physical connections, tagged with `server.availability_zone` and
`respire.availability_zone.status=known`. These are attempts, not successful operations: a
redirected or retried command can count more than once. Local cache hits do not count.
Individually submitted batch reads count; transactions do not. All read policies count,
including `Primary` and `Replica`.

Missing zone metadata uses `respire.availability_zone.status=unknown`, without a zone tag.
The process keeps counters for at most 64 distinct zone names, shared by all clients. Names
longer than 128 UTF-16 code units, and names beyond the first 64, use
`respire.availability_zone.status=overflow`, also without a zone tag. The status tag
distinguishes a real zone named `unknown` from missing metadata. The first 64 names are never
evicted, so totals stay monotonic across connection disposal. This limit applies only to
metric labels; routing still compares full zone names ordinally. Zone names come from server
metadata, never from keys or values. Overflow bounds exported series without dropping read
counts; it is not a trust check.

## Hedged reads

Opt-in [hedged reads](../fundamentals/connections.md#hedged-reads) expose three instruments:

| Instrument | Kind | Unit | Meaning |
| --- | --- | --- | --- |
| `respire.read.hedge.sent` | Counter | `{request}` | Additional requests started |
| `respire.read.hedge.won` | Counter | `{request}` | Additional requests whose successful reply was returned to the caller |
| `respire.read.hedge.extra_load` | Histogram | `1` | One 0 or 1 sample per completed eligible logical read |

The mean of `extra_load` is the ratio of additional requests to eligible reads; multiply by
100 for a percentage. Sent and won tags identify the hedge endpoint; extra-load tags identify
the original endpoint. All use `server.address` and `server.port`, never keys or payloads.
The sample is recorded when the logical read completes, even if a losing reply is still
draining. Transport redirects and topology probes are not hedge starts. A histogram window
can exceed the configured percentage when saved budget is spent during that window.

## Logging

Pass an `ILoggerFactory` through `RespireOptions.LoggerFactory`, or use the
[dependency-injection integration](./dependency-injection.md), which uses the container's
logger factory. Logs cover connection lifecycle and recovery and never include command
payloads.

## Health state

For health checks or dashboards, combine `IsConnected` with `ConnectionStateChanged`:

```csharp
redis.ConnectionStateChanged += change =>
    healthState.Update(change.Endpoint, change.State, change.Error);
```

A transient reconnection moves through several connection states, so apply application-level
thresholds before paging an operator. For ASP.NET Core readiness endpoints, see
[health checks](./health-checks.md).

## Thread-pool scheduling

While at least one client has monitoring enabled (the default), a shared background thread
samples thread-pool scheduling once per second. It queues one probe at a time and measures
how long the pool takes to run it, so it can report a pending probe even when every worker
is blocked. Successful Redis commands do not update this monitor.

The `Respire` meter exposes four observable gauges:

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `respire.thread_pool.scheduling.delay` | `s` | Latest probe's scheduling delay |
| `respire.thread_pool.workers.busy` | `{thread}` | Maximum minus available worker threads |
| `respire.thread_pool.workers.min` | `{thread}` | Configured minimum worker threads |
| `respire.thread_pool.work.pending` | `{work_item}` | Process-wide queued thread-pool work |

When the delay reaches `ThreadPoolWarningThreshold` (500 ms by default), the
`Respire.ThreadPool` logger emits a warning with these counters and remediation guidance, at
most once per client every 30 seconds. Look for synchronous blocking and long-running work,
and prefer asynchronous I/O. Respire never changes thread-pool limits. Clients can have
different thresholds and loggers, so warnings are per client, and several clients sharing a
logging sink can report the same stall. Logging runs serially on the sampler thread, so a
logger provider that blocks delays sampling for every client.

`RespireTimeoutDiagnostics.ThreadPoolProbe` holds the latest immutable
`RespireThreadPoolSnapshot`, including `CapturedAt` and `IsPending`. While a probe is
pending, its delay is a lower bound. Check `CapturedAt` for freshness. GC pauses or process
suspension can also increase the delay, so a warning alone does not prove starvation.
Recovery shows on a later sample, up to one second after the pool recovers.

Configure the threshold or disable monitoring per client:

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ThreadPoolMonitoring = false,
    ThreadPoolWarningThreshold = TimeSpan.FromSeconds(1)
};
```

Disabling monitoring removes that client's subscription and warnings. Other clients may keep
the shared probe running, so the process-wide sample can still appear in timeout diagnostics.
Disposing the last subscribed client stops sampling and clears the sample; the gauges then
report nothing until monitoring restarts. Timeout diagnostics captured earlier keep their
sample. If a probe is still queued when monitoring restarts, its delay includes the time with
no subscribers, because that work item has still not run.

## Cluster retirement

`RespireClient.GetClusterRetirementSnapshot()` returns an immutable snapshot of retired
Cluster connection generations that are still draining, or `null` for a standalone or
Sentinel client. Resolve the concrete `RespireClient` from dependency injection to call it.

```csharp
await using var client = RespireClient.Create(new RespireOptions
{
    UseCluster = true,
    Endpoints = [new RespireEndpoint("localhost", 7000)]
});
var snapshot = client.GetClusterRetirementSnapshot()!;
Console.WriteLine($"Retiring: {snapshot.RetiringGenerationCount}; " +
    $"oldest: {snapshot.OldestRetirementAge}; fences: {snapshot.PendingCorrectionFenceCount}");
```

Capture performs no network I/O, but it briefly takes internal locks, so avoid calling it on
every command. Counters can change during capture, so treat the result as a best-effort
observation. Prefix views report the same underlying router. Capture after client disposal
begins throws `ObjectDisposedException`; snapshots already captured stay valid and hold no
references to connections or pools. `CapturedAt` is UTC wall-clock time; ages use a monotonic
clock.

| Property | Meaning |
| --- | --- |
| `RetiringGenerationCount`, `OldestRetirementAge` | Retired generations still owned; age is zero when none remain. |
| `UndrainedGenerationCount` | Generations whose transport drain has not completed, including failures before that point. |
| `AwaitingFenceGenerationCount` | Drained generations still waiting for a connection fence to be acknowledged. |
| `PendingCorrectionFenceCount` | Fences still owed across retained generations; more can appear while a drain is unfinished. |
| `CleanupFailedGenerationCount` | Unexpected cleanup failures; ordinary retriable fence failures do not count. |
| `BorrowedDedicatedConnectionCount`, `ConnectingDedicatedConnectionCount` | Active leases and unfinished acquisitions in dedicated pools of retained generations. |

Categories can overlap: a generation awaiting a fence can still own a borrowed blocking
operation. Zero fences alone does not prove that accepted work finished. Fully retired
generations disappear from the next snapshot. A permanently unavailable peer can keep a
generation indefinitely; Respire retries fences with a one-to-30-second backoff and never
abandons one because of its age.

## Sentinel primary changes

Sentinel batch, durability-batch, and transaction acquisition failures still emit an error
activity and `db.client.operation.duration`, including time spent discovering or connecting.
Those records omit `server.address` and `server.port` because no data connection was
acquired. Successful batch, durability, transaction, blocking, and script durations also
include discovery and acquisition time, and report the selected primary's endpoint, never
the Sentinel seed. Timeouts before a data node is selected also carry no endpoint.

`ConnectionStateChanged` reports the retired endpoint and the validated replacement for
Sentinel handoffs triggered by events, disconnects, or `READONLY` replies. Prefix views share
these events. Sentinel event monitors log failover hints for the configured service at
Information and replica events at Debug.

| Instrument | Meaning |
| --- | --- |
| `respire.sentinel.failover` | Primary endpoint changes, tagged with `server.address` and `server.port`. Initial discovery and reconnection to the same endpoint do not count. |
| `respire.connection.reconnect.exhausted` with `respire.reconnect.scope=sentinel-monitor` | A Sentinel monitor exhausted its `ReconnectPolicy`. Respire also logs a warning, then logs at Information when a later primary publication resumes the monitor. |
| `respire.sentinel.guarded_logging.failures` | Process-wide count of non-fatal logger exceptions caught while logging Sentinel notifications and optional discovery failures, without endpoint or exception tags. Other Sentinel logs are not covered; inspect the logging provider when this increases. |
| `respire.sentinel.generations.retired` | Process-wide gauge of retired generations still owned while accepted work or correction fences drain. |

Failover measurements are recorded even when disposal suppresses lifecycle callbacks.
Lifecycle observers run outside discovery and transport work and are suppressed after client
disposal.

Continued growth of `respire.sentinel.generations.retired` warrants investigation. A value
that stays above zero after commands and borrowed leases finish can indicate an unreachable
peer or a failed cleanup; check the warning logs. Fence retries use a one-to-30-second
backoff, with at most one warning per retired generation every five minutes. After an
unexpected terminal cleanup failure, the generation stays retained, and counted, until client
disposal, which aborts and joins the remaining connection work. See
[Sentinel connections](../fundamentals/connections.md#redis-sentinel) for drain and no-replay
behavior.

## Maintenance notifications

With maintenance handling enabled, each valid notification produces a `redis.maintenance`
consumer activity and an event named after its kind (`MOVING`, `MIGRATING`, `MIGRATED`,
`FAILING_OVER`, `FAILED_OVER`, `SMIGRATING`, or `SMIGRATED`). Activities include the
receiving endpoint and database, `respire.maintenance.kind`,
`respire.maintenance.sequence_id`, and any announced seconds or target endpoint. They keep
the receive timestamp and have no application command as parent. Information logs identify
the kind, sequence, and receiving endpoint.

The `redis.client.maintenance.notifications` counter counts notifications delivered to
diagnostics, with endpoint and kind tags; sequence IDs are never metric tags. Diagnostics run
serially on a thread-pool worker for each physical connection, so a slow listener cannot
block RESP parsing or timeout handling, and listener exceptions are isolated. Each queue holds
at most 256 pending events; on overflow the oldest is dropped, and the drops are reported
through `respire.maintenance.notifications.dropped` when delivery resumes. These diagnostics
are best effort, can finish after connection disposal, and are not an acknowledged event
stream. Malformed notifications and historical replays during negotiation produce no
diagnostics.

Cluster clients count `SMIGRATED` notifications that did not update slot ownership
proactively in `respire.cluster.slot_migrations.skipped`, with a `reason` tag:

- `queue_full`: the 128-item topology queue dropped its oldest notification. This also logs a
  warning at most once every 30 seconds per client.
- `malformed`: an entry with an invalid slot list, or entries past the 16,384-slot budget.
- `duplicate`: a sequence ID already seen on the same connection.
- `deferral_evicted`: an entry waiting for an earlier migration was evicted to keep the
  waiting list bounded.
- `deferral_expired`: an entry waited 30 seconds without the earlier migration arriving.

In every case, `MOVED` handling and topology discovery still correct the route.

## For contributors

Every public method on a public core type declares its final error-observation owner in a
source-adjacent `<source-file>.cs.ownership.json` file, and `CommandRouteOwnershipTests`
fails until a new method is classified. When you add a command path, start its owner before
argument validation, keep successful paths free of error-observation rentals, publish one
final failure per logical caller after cleanup, and test a handled retry, the caller-visible
failure, and collection enabled and disabled. The full rules are in
[`docs/ERROR_OWNERSHIP.md`](https://github.com/thomhurst/Respire/blob/main/docs/ERROR_OWNERSHIP.md).
