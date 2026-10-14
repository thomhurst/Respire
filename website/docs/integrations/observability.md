---
title: Observability
description: Collect OpenTelemetry traces, metrics, logs, and connection state.
---

# Observability

Respire emits OpenTelemetry traces and metrics, and logs through `ILoggerFactory`. Use this
page to turn them on, choose which metrics to collect, and read what each instrument means.

## Enable traces and metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire"))
    .WithMetrics(metrics => metrics.AddMeter("Respire"));
```

Respire does not create a provider, listener, or exporter: register the source and meter with
your own, as above. The [Aspire integration](./aspire.md) does this for you.

| Package | `ActivitySource` and `Meter` name |
| --- | --- |
| Core client | `Respire` |
| [Stream workers](../guides/stream-workers.md) | `Respire.Streaming` |
| [SignalR backplane](./signalr.md) | `Respire.SignalR` |

By default only the `Resiliency` and `ConnectionBasic` metric groups are collected. Command
latency, client-side cache, pub/sub, and stream metrics need opt-in, even when an exporter
subscribes to the meter. To collect everything:

```csharp
using Respire;

RespireMetrics.Configure(new RespireMetricsOptions
{
    Groups = RespireMetricGroups.All,
});
```

## Traces

Spans follow the OpenTelemetry database semantic conventions. Each operation produces one
`Client` span named after the command (for example `GET`), or after the command plus script
digest or function name. A pipeline or transaction is one span.

| Attribute | Value |
| --- | --- |
| `db.system.name` | `redis` |
| `db.namespace` | Configured database index |
| `db.operation.name` | Command name |
| `server.address`, `server.port` | Endpoint; port omitted when 6379 |
| `db.operation.batch.size` | Pipelines and transactions |
| `db.stored_procedure.name` | Scripts and functions |
| `error.type`, `db.response.status_code` | On failure |

Spans never carry command values, because arbitrary Redis payloads cannot be reliably
sanitized.

## Metric selection

One process-wide selection applies to every client and prefix view. For narrower collection,
select groups and exact command names:

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
| `Resiliency` | `resiliency` | Errors, maintenance notifications, geographic failovers, endpoint circuit counters |
| `ConnectionBasic` | `connection-basic` | Ready connection counts, creation time, maintenance timeout allowances, handoffs |
| `ConnectionAdvanced` | `connection-advanced` | Pending replies, dedicated-pool waits, socket closes |
| `Command` | `command` | Logical operation duration (`db.client.operation.duration`), including pub/sub and stream commands |
| `ClientSideCaching` | `client-side-caching` | Cache requests and evictions |
| `PubSub` | `pubsub` | Confirmed publications and received messages |
| `Streaming` | `streaming` | Stream lag, when your code reports processing start |
| `Default` | | `Resiliency \| ConnectionBasic` |
| `All` / `None` | | Every group / no group |

- Configure once at startup, before creating clients or attaching exporters. The last call
  wins. There is no per-client override.
- `Configure` validates and copies its input. Later changes to the supplied collections, or to
  the copy from `RespireMetrics.Configuration`, have no effect.
- A new configuration affects telemetry started afterwards. An operation keeps the selection
  it started with, including while it waits for a connection.
- Groups do not control `respire.*` instruments, except the endpoint circuit counters. Filter
  those in your exporter.
- `CommandAllowList` and `CommandBlockList` are `IReadOnlyCollection<string>`. Record `with`
  expressions share them, and record equality does not compare their contents.

### Command filters

Filters affect only command duration metrics, never execution, traces, or other groups.

- Names match the complete reported command name (such as `CLIENT LIST`), ordinally, ignoring
  case. No wildcards or prefixes.
- An empty allow list allows every command. The block list always wins.
- Names must be 1 to 64 ASCII characters: letters, digits, `.`, `_`, `-`, and single spaces
  between words. Null, empty, malformed, or longer names, and unknown group flags, throw
  `ArgumentException` and leave the configuration unchanged.
- A pipeline or transaction emits a duration only if every command in it passes. Labels are
  `PIPELINE GET` or `MULTI SET` for homogeneous operations, `PIPELINE` or `MULTI` for mixed
  ones, but filters match the member commands (`GET`, `SET`). Empty operations match
  `PIPELINE` or `MULTI`.
- Durability batches also require their `WAIT` or `WAITAOF` to pass.
- A script that falls back from `EVALSHA` to `EVAL` stays one `EVALSHA` (or `EVALSHA_RO`)
  operation for filtering.
- Immediate, raw, blocking, streamed, and fire-and-forget operations follow the same rules.

### Labels and cardinality

- Metric labels never include keys, channel or stream names, arguments, payloads, or
  credentials. Script digests and function names appear on spans only.
- At most 1,024 distinct command labels per process, ignoring case, including compound labels.
  Later names, empty or non-ASCII names, and names over 80 characters become `OTHER`. The
  limit never resets until the process restarts.
- Filters match the original name: an allowed command first seen after the limit still emits,
  labelled `OTHER`. Allowing or blocking `OTHER` selects nothing else. Allow-list entries do not
  reserve label capacity.

### Duration timing

- Duration ends when the reply arrives, before your code converts the result. On some fast
  paths it is recorded when the result is consumed, so an unconsumed result emits nothing.
- With the group disabled, the command filtered out, or no listener, Respire skips command
  metric work.
- Metric listener exceptions never replace a command's result or error.

## Standard instruments

Standard signals use the names, units, and attributes of the
[Redis client observability specification](https://redis.io/docs/latest/develop/clients/observability/),
on the `Respire` meter. They carry `redis.client.library=Respire:<version>` and
`db.system.name=redis`. Not every instrument in the specification is implemented.

| Instrument | Group | Unit | Meaning |
| --- | --- | --- | --- |
| `redis.client.errors` | `Resiliency` | `{error}` | Handled and final errors; see [error measurements](#error-measurements) |
| `redis.client.maintenance.notifications` | `Resiliency` | `{notification}` | Notifications by `redis.client.connection.notification`, `server.address`, `server.port` |
| `redis.client.geofailover.failovers` | `Resiliency` | `{failover}` | Switches between known failover deployments; `db.client.geofailover.reason=automatic`, `.fail_from`, `.fail_to` |
| `db.client.connection.count` | `ConnectionBasic` | `{connection}` | Ready sockets, `idle` or `used` |
| `db.client.connection.create_time` | `ConnectionBasic` | `s` | Socket connect, TLS, and Redis handshake |
| `redis.client.connection.relaxed_timeout` | `ConnectionBasic` | `{relaxation}` | Connections with a timeout currently raised by a maintenance window |
| `redis.client.connection.handoff` | `ConnectionBasic` | `1` | Old connections replaced by a published `MOVING` handoff |
| `db.client.connection.pending_requests` | `ConnectionAdvanced` | `{request}` | Replies still owed, including cancelled ones still draining |
| `db.client.connection.wait_time` | `ConnectionAdvanced` | `s` | Waiting for a new dedicated connection; reusing an idle one emits nothing |
| `redis.client.connection.closed` | `ConnectionAdvanced` | `{connection}` | Connected sockets that close, including failed handshakes |
| `db.client.operation.duration` | `Command` | `s` | Logical operation latency |
| `redis.client.csc.requests` | `ClientSideCaching` | `{request}` | One per cache lookup; `redis.client.csc.result=hit` or `miss` |
| `redis.client.csc.evictions` | `ClientSideCaching` | `{eviction}` | Removed cached responses; optional `redis.client.csc.reason=full`, `ttl`, or `invalidation` |
| `redis.client.pubsub.messages` | `PubSub` | `{message}` | Publications and received frames; `redis.client.pubsub.message.direction` (`out`/`in`), `redis.client.pubsub.sharded` |
| `redis.client.stream.lag` | `Streaming` | `s` | Entry timestamp to application processing start |

These replace `respire.client_cache.hits`, `respire.client_cache.misses`,
`respire.client_cache.evictions`, and `respire.maintenance.notifications`, with no aliases.
Update exporter filters and dashboards when upgrading.

Cache evictions count removals caused by capacity, local expiration, or server invalidation.
One invalidated key can remove several responses or none. Local mutation, explicit clearing,
and continuity flushes do not count; see
[client-side caching](../fundamentals/client-side-caching.md) for those instruments. Client
cache statistics are independent of exported totals.

`redis.client.geofailover.failovers` excludes initial selection and transitions to or from
having no healthy endpoint. `respire.failover.endpoint.switches` counts those too.

### Endpoint circuit metrics

Opt-in [endpoint circuit breakers](../guides/circuit-breakers.md) add two counters. Both
require `Resiliency`, so `None` suppresses them.

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `respire.circuit.transitions` | `{transition}` | One per state change; `respire.circuit.state.from` and `.to` are `closed`, `open`, or `half_open` |
| `respire.circuit.rejections` | `{operation}` | One per rejected admission, including exhausted half-open probe capacity |

- Tags: `redis.client.library`, `db.system.name=redis`, `server.address`, and `server.port`
  (omitted when 6379). No keys, payloads, exception messages, or credentials.
- Each rejected batch entry adds one. A transaction commit adds one for `MULTI`/`EXEC`, however
  many pending results fail. `WATCH` setup is a separate admission.
- A successful admission can report a transition (such as `open` to `half_open`), never a
  rejection. Creating a closed circuit or evicting idle history adds nothing.
- `redis.client.errors` can also count the circuit exceptions that reach callers.
  Failover-group health probes report only `respire.failover.*` metrics.

### Other Respire instruments

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
| This page | [Connection drops](#lifecycle-event-delivery), [availability zones](#reads-by-availability-zone), [hedged reads](#hedged-reads), [thread pool](#thread-pool-scheduling), [Sentinel](#sentinel-primary-changes), [maintenance and slot migrations](#maintenance-notifications) |

## Prometheus and the published Redis dashboard

The [observability sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Observability)
exports through the OpenTelemetry Prometheus ASP.NET Core exporter `1.19.1-beta.1` (SDK
`1.19.1`). It checks names, units, and labels against every query in
[the published Redis dashboard at revision `033fe86e47440da7f365ed2d8dd7f5d6217a575a`](https://github.com/redis-developer/redis-client-observability/blob/033fe86e47440da7f365ed2d8dd7f5d6217a575a/grafana/dashboards/redis-client-observability.json),
and evaluates the adapted queries with promtool `3.5.0`, on .NET 8 and .NET 10. To reproduce
from the repository root:

```powershell
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -OutputDirectory ./observability-evidence
```

Prometheus naming:

- Dots become underscores.
- Second-based histograms get `_seconds_bucket`, `_seconds_sum`, `_seconds_count`; monotonic
  counters get `_total`; braced units add nothing else. For example `redis_client_errors_total`
  and `redis_client_stream_lag_seconds_bucket`.
- Up/down instruments become gauges.
- The sample sets latency buckets to `0, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10` seconds.

| Dashboard panels | Required group or feature | Notes |
| --- | --- | --- |
| Connections, pool state/summary, creation time | Default `ConnectionBasic` | Ready physical sockets. Multiplexed socket state is not command concurrency. |
| Error rates/type/category | Default `Resiliency` | Includes handled and final failures, so the command success formula is approximate. |
| Latency, throughput, success rate, heatmap | Opt-in `Command` | One sample per logical operation (a pipeline or transaction is one). Cache hits may run no Redis command. |
| Pending requests, closes, wait time | Opt-in `ConnectionAdvanced` | Wait time is dedicated-pool acquisition only, not multiplexed admission or command latency. |
| Cache ratio, requests, evictions | Opt-in `ClientSideCaching` plus an enabled cache | An eviction is not an invalidation notification. |
| Network bytes saved, cached items | Unavailable | Respire does not emit `redis.client.csc.network_saved` or `redis.client.csc.items`. |
| Message rates | Opt-in `PubSub` | Replace `redis_client_pubsub_direction` with `redis_client_pubsub_message_direction`. |
| Message rate by channel | Opt-in `PubSub` | Channel names are absent; the adapted query groups by direction. |
| Stream/consumer lag | Opt-in `Streaming` plus `RecordProcessingStart()` | Remove `redis_client_stream_name` and `redis_client_stream_consumer_name` grouping; those names are absent. |
| Geo-failovers | Default `Resiliency` plus a geographic deployment | Switches between known endpoints only. |
| Timeout relaxations/handoffs | Default `ConnectionBasic` plus maintenance events | Standalone Redis sends no such events. |

- The original dashboard selects `exported_job` from a Collector setup. A direct scrape puts
  resource metadata in `target_info`, so the adapted dashboard selects `db_system_name="redis"`.
  For several services, separate them by scrape `job` or Collector resource labels.
- No option exposes channel, stream, or consumer names.
- The sample validates query compatibility, not performance.
- `respire.*` diagnostics do not substitute for the standard panels.

## Pub/sub messages and stream lag

Enable `PubSub` and/or `Streaming` in `RespireMetricsOptions.Groups`. Command filters do not
apply. Channel, pattern, stream, group, and consumer names, entry IDs, and payloads are never
labels.

`redis.client.pubsub.messages` with direction `out`:

- Counts once per successful integer reply to `PUBLISH` or `SPUBLISH`, however many
  subscribers received it. Typed, raw, and fire-and-forget publications all count.
- A cancelled caller still counts if its successful reply arrives later.
- Server errors, lost replies, and publications from server-side scripts do not count.
- A fire-and-forget publication counts only if `PubSub` was enabled when it was queued.

Direction `in`:

- Counts once per accepted channel, pattern, or sharded frame on a current subscription, even
  if local fan-out reaches several subscriptions or their buffers drop it.
- Channel and pattern deliveries of the same publication are two frames, so they count twice.
- Reconnect markers and stale routes do not count. Drop and gap instruments stay separate.
- Metric callbacks run outside subscription locks; keep listeners short and non-blocking.

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

- Call `RecordProcessingStart()` once when each processing attempt begins, after any
  application queueing. Reads, claims, and acknowledgements emit no lag. Each call records
  one observation; it does not acknowledge or change the entry.
- Lag is the client's UTC clock minus the millisecond part of a `milliseconds-sequence` ID.
  Use it only when IDs reflect creation time (normal for Redis-generated IDs). Clock skew
  affects it.
- Future timestamps and malformed or special IDs are skipped.
- With the group or listener disabled, the method does nothing. Listener failures never change
  publication, subscription, or processing outcomes.

## Connection lifecycle

The connection instruments are listed under [standard instruments](#standard-instruments).

**Tags.** All carry `redis.client.library`, `db.system.name`, and
`db.client.connection.pool.name`. The pool name combines host, port, database, and purpose
(`shared`, `dedicated`, or `pubsub`), and never contains credentials, keys, or payloads.
Connections with the same identity aggregate, even across clients.

**Pool name limits.** The process keeps at most 64 pool identities, plus `overflow/shared` and
`overflow/pubsub`. Names over 256 characters also overflow. The first 64 are kept, with
zero-valued series, until the process restarts; configuration changes do not reset them.

**Count.** `db.client.connection.count` adds `db.client.connection.state` and
`redis.client.connection.pubsub`.

- A multiplexed connection is `used` while a reply is owed. A dedicated one is `used` while
  rented. Pub/sub connections are always `used`.
- Cancelled commands and early-disposed streamed replies stay pending until their reply
  drains.
- During replacement, old and new sockets can both count.
- Handshaking connections, in-memory test streams, and Unix sockets are excluded.

**Observable values.** Counts, pending replies, and timeout allowances are
`ObservableUpDownCounter<long>` instruments, exported as non-monotonic sums on the exporter's
interval. Late listeners see current values, with no replay. Group changes apply at the next
observation; event instruments emit only events whose group was selected when they occurred.

**Timeout allowances and handoffs.**

- Allowances count connections, not requests. They return to zero on the matching completion,
  window expiry, or socket close.
- Duplicate notifications add nothing. Failed or superseded replacements emit no handoff.
  Ordinary reconnects count as creation and close, not handoffs.
- Handoffs include idle and borrowed dedicated connections, across standalone, Sentinel, and
  Cluster. Connections already closing or still negotiating are excluded.

**Close reasons.** `redis.client.connection.close.reason` is one of:

| Reason | Meaning |
| --- | --- |
| `application_close` | Disposal, retirement, or caller cancellation during a handshake |
| `pool_eviction_idle` | Dedicated idle-capacity overflow |
| `server_close` | Server closed or reset the connection, including a reset wrapped by TLS |
| `error` | Transport or handshake failure, including handshake timeout; adds `error.type` and `redis.client.errors.category` (`network`, `tls`, `auth`, `server`, or `other`) |

Respire never emits `healthcheck_failed`. Failed TCP connection attempts are not closes.

### Lifecycle event delivery

Creation and wait durations, handoffs, and closes reach listeners on the thread pool. Durations
are measured at the event, so scheduling delay does not inflate them.

- At most 64 lifecycle measurements can be queued or running per process. Beyond that, new
  measurements are dropped without blocking.
- `respire.connection.measurements.dropped` (observable counter, `{measurement}`, no tags)
  counts drops since process start, including before any listener subscribed. It excludes
  listener exceptions and events lost at shutdown.
- The budget is shared by all clients, so a blocking listener can starve delivery for others.
  Observable counts are unaffected.
- Events can arrive after the matching state change, and out of order. They are best effort:
  disposal does not flush them, and shutdown can lose them.
- A `MOVING` handoff counts the sockets live when it is published.

## Error measurements

`redis.client.errors` is in the `Resiliency` group, independent of command filters. Each
measurement is one error at an observed boundary: connection setup, typed and raw commands,
scripts and functions, blocking commands, streamed uploads and downloads, cached reads,
batches, transaction commit, and fire-and-forget submission. Throwing or inspecting an
exception elsewhere emits nothing.

### Attributes

| Attribute | Value |
| --- | --- |
| `redis.client.errors.category` | `network`, `tls`, `auth`, `server`, `cancelled`, or `other` |
| `error.type` | Full exception type name; first 128 distinct names per process, then `_OTHER` |
| `db.response.status_code` | A recognized Redis error prefix, such as `WRONGTYPE` or `NOSCRIPT`; unknown prefixes are omitted |
| `redis.client.errors.internal` | `true` for a handled error, `false` for a failure delivered to a caller |
| `redis.client.operation.retry_attempts` | Retries so far, from zero; exact integer per the specification |
| `server.address`, `server.port` | Failed endpoint, when known; port omitted when 6379 |

Categories:

| Category | Covers |
| --- | --- |
| `tls` | TLS certificate failures |
| `auth` | `NOAUTH`, `NOPERM`, `WRONGPASS`, `RespireAuthenticationException` |
| `server` | Other Redis error replies |
| `network` | Socket, I/O, connection, and timeout failures, including `RespireTimeoutException` (a client deadline, not necessarily a network fault) |
| `cancelled` | Surfaced cancellation, whoever requested it |

- There is no `timeout` category. Filter by `error.type` to separate cancellations and
  deadlines from outage alerts.
- Wrapper exceptions are unwrapped up to 16 levels deep for classification; the caller still
  gets the original exception. In deeper chains the server code may be absent.
- Exception messages, credentials, keys, and payloads are never labels.
- Reuse exception types: `error.type` includes application codec and generic types, and one
  type per operation exhausts the 128-name limit.
- Handled retries report the failed attempt's endpoint; a final error reports the last
  attempted endpoint. Errors before node selection (validation, early cancellation) have no
  endpoint.
- To cap series count, drop `redis.client.operation.retry_attempts` with an exporter view.

### Handled and final errors

`redis.client.errors.internal=true` marks an error Respire handled: a Cluster redirect or
retry, a `NOSCRIPT` fallback, a failed connection candidate (including a replica rejected by
`ROLE`), a background reconnect, a connection abort, or an error reply discarded after
fire-and-forget or caller cancellation. If a `NOSCRIPT` fallback also fails, that failure is a
separate caller-visible measurement whose retry count includes the earlier attempts.

| Operation | Counting |
| --- | --- |
| Connection loss | The socket abort counts once, internal. Each in-flight command that fails counts at its own boundary. |
| Connecting | `ConnectAsync(string)` parse failures are final connection failures. `ConnectAnyAsync` reports an empty candidate list, enumeration failure, or exhaustion once; retry count is the number of failed candidates. |
| Batches and transactions | One caller-visible measurement per faulted deferred result. Re-reading `Result` or `ThrowIfAnyFailed` adds nothing. An execution failure counts only when no deferred result represents it. A null `EXEC` faults every result in an unwatched transaction; in a watched one it returns `false`, not an error. `WATCH` aborts and disposal are not errors. |
| Fire-and-forget | Submission failures are caller-visible; discarded replies are internal. |
| Cached reads | A final failure counts once per waiting caller, with the shared producer's retry count. Hits are not errors. |
| Hedged reads | Discarded legs are internal. The final error carries the retry count of the leg whose result the caller got. |
| Streaming | A streaming `GET` counts header failures at the command, then the first payload failure once. Cancelling one read counts that cancellation. Invalid arguments and reads after disposal do not count. `ReadAllAsync` counts recovered failures as internal and a terminal failure once. Ending enumeration early is not an error. |
| Pub/sub | Background resubscription failures and redirects are internal. Shutdown cancellation does not count. |
| Local validation | Cross-slot rejections (multi-key commands, `WATCH`, typed `XREAD`/`XREADGROUP`) count once with zero retries; nothing is sent. |
| Node fan-outs | Per-node commands count each failed node; the parent does not recount. All-primary commands (`SCRIPT`, `FUNCTION`, `DBSIZE`, `FLUSHDB`, `FLUSHALL`) report one final failure after every target finishes. |
| Locks and semaphores | Contention counts only when `AcquireOrThrowAsync` throws `RespireLockNotAcquiredException`. A capacity mismatch counts once as `RespireSemaphoreCapacityMismatchException`. Swallowed disposal cleanup failures are internal. |
| Distributed cache | Reported after any TTL correction or cleanup, describing the exception the caller receives. |

- The group is checked when the error is reported. Pending raw, cached, and fire-and-forget
  operations can report a final failure if collection is enabled before they complete.
  Earlier errors are not replayed.
- Listener exceptions never replace the caller's exception, cancellation, or recovery.

## Reads by availability zone

With `ClientAvailabilityZone` set, the observable counter `respire.read.availability_zone`
(`{read}`) counts read commands accepted by connections, tagged `server.availability_zone` and
`respire.availability_zone.status`.

- Counts attempts, not successes: redirects and retries can count twice. Local cache hits and
  transactions do not count; individually submitted batch reads do. All read policies count,
  including `Primary` and `Replica`.
- Status is `known` with a zone tag, `unknown` (no zone metadata), or `overflow`. The last two
  have no zone tag.
- At most 64 zone names per process. Names beyond those, or over 128 UTF-16 code units, use
  `overflow`. The first 64 are never evicted, so totals stay monotonic.
- The limit affects labels only; routing compares full zone names. Zone names come from server
  metadata, never keys or values.

## Hedged reads

Opt-in [hedged reads](../fundamentals/connections.md#hedged-reads) expose three instruments:

| Instrument | Kind | Unit | Meaning |
| --- | --- | --- | --- |
| `respire.read.hedge.sent` | Counter | `{request}` | Additional requests started |
| `respire.read.hedge.won` | Counter | `{request}` | Additional requests whose reply was returned |
| `respire.read.hedge.extra_load` | Histogram | `1` | One 0 or 1 sample per completed eligible read |

- The mean of `extra_load` is the extra-request ratio; multiply by 100 for a percentage. A
  window can exceed the configured percentage when saved budget is spent in it.
- `sent` and `won` tag the hedge endpoint; `extra_load` tags the original endpoint, using
  `server.address` and `server.port`.
- The sample is recorded when the read completes, even if a losing reply is still draining.
  Redirects and topology probes are not hedges.

## Logging

Pass an `ILoggerFactory` through `RespireOptions.LoggerFactory`, or use the
[dependency-injection integration](./dependency-injection.md), which uses the container's
factory. Logs cover connection lifecycle and recovery and never include payloads.
All shipping packages use source-generated logging with [stable event IDs](./log-events.md).

## Health state

Combine `IsConnected` with `ConnectionStateChanged`:

```csharp
redis.ConnectionStateChanged += change =>
    healthState.Update(change.Endpoint, change.State, change.Error);
```

A transient reconnect passes through several states, so apply thresholds before paging anyone.
For ASP.NET Core readiness endpoints, see [health checks](./health-checks.md).

## Thread-pool scheduling

While any client has monitoring on (the default), a shared background thread measures how
long the thread pool takes to run a probe, once per second. It reports a pending probe even
when every worker is blocked.

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `respire.thread_pool.scheduling.delay` | `s` | Latest probe's scheduling delay |
| `respire.thread_pool.workers.busy` | `{thread}` | Maximum minus available worker threads |
| `respire.thread_pool.workers.min` | `{thread}` | Configured minimum worker threads |
| `respire.thread_pool.work.pending` | `{work_item}` | Process-wide queued work |

When the delay reaches `ThreadPoolWarningThreshold` (default 500 ms), the `Respire.ThreadPool`
logger warns, at most once per client every 30 seconds. Look for synchronous blocking and
long-running work; prefer async I/O. Respire never changes thread-pool limits.

- Warnings are per client, so clients sharing a sink can report the same stall.
- Logging runs on the sampler thread: a blocking logger delays sampling for every client.
- `RespireTimeoutDiagnostics.ThreadPoolProbe` holds the latest `RespireThreadPoolSnapshot`,
  with `CapturedAt` and `IsPending`. A pending probe's delay is a lower bound.
- GC pauses or process suspension also raise the delay, so a warning alone does not prove
  starvation. Recovery shows within a second.

Configure per client:

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ThreadPoolMonitoring = false,
    ThreadPoolWarningThreshold = TimeSpan.FromSeconds(1)
};
```

Disabling monitoring removes that client's warnings; other clients can keep the shared probe
running. When the last subscribed client is disposed, sampling stops and the gauges report
nothing. A probe still queued when monitoring restarts includes the idle time in its delay.

## Cluster retirement

`RespireClient.GetClusterRetirementSnapshot()` returns a snapshot of retired Cluster
connection generations still draining, or `null` for standalone and Sentinel clients. Resolve
the concrete `RespireClient` from dependency injection to call it.

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

- Capture does no network I/O but takes internal locks briefly; do not call it per command.
  Values are best effort.
- After disposal begins, capture throws `ObjectDisposedException`. Existing snapshots stay
  valid and hold no connection references.
- `CapturedAt` is UTC wall-clock time; ages use a monotonic clock. Prefix views report the
  same router.

| Property | Meaning |
| --- | --- |
| `RetiringGenerationCount`, `OldestRetirementAge` | Retired generations still owned; age is zero when none remain |
| `UndrainedGenerationCount` | Transport drain not finished, including failures before that point |
| `AwaitingFenceGenerationCount` | Drained, waiting for a connection fence acknowledgement |
| `PendingCorrectionFenceCount` | Fences still owed; can grow while a drain is unfinished |
| `CleanupFailedGenerationCount` | Unexpected cleanup failures; retriable fence failures do not count |
| `BorrowedDedicatedConnectionCount`, `ConnectingDedicatedConnectionCount` | Active leases and unfinished acquisitions in retained dedicated pools |

Categories can overlap, and zero fences does not prove accepted work finished. A permanently
unavailable peer can keep a generation indefinitely: fences retry with 1 to 30 second backoff
and are never abandoned for age.

## Sentinel primary changes

- Batch, durability-batch, and transaction acquisition failures still emit an error activity
  and `db.client.operation.duration`, without `server.address` or `server.port`. Timeouts
  before a data node is selected also have no endpoint.
- Successful batch, durability, transaction, blocking, and script durations include discovery
  and connection time, and report the selected primary, never the Sentinel seed.
- `ConnectionStateChanged` reports the retired and replacement endpoints for handoffs
  triggered by events, disconnects, or `READONLY` replies. Prefix views share these events.
- Sentinel monitors log failover hints for the configured service at Information and replica
  events at Debug.

| Instrument | Meaning |
| --- | --- |
| `respire.sentinel.failover` | Primary endpoint changes, tagged `server.address` and `server.port`. Initial discovery and reconnecting to the same endpoint do not count. Recorded even after disposal suppresses lifecycle callbacks. |
| `respire.connection.reconnect.exhausted` with `respire.reconnect.scope=sentinel-monitor` | A monitor exhausted its `ReconnectPolicy`. Also logs a warning, then Information when the monitor resumes. |
| `respire.sentinel.guarded_logging.failures` | Process-wide count of logger exceptions caught while logging Sentinel notifications and optional discovery failures. No tags. If it grows, inspect the logging provider. |
| `respire.sentinel.generations.retired` | Process-wide gauge of retired generations still draining |

If `respire.sentinel.generations.retired` keeps growing, or stays above zero after commands
and leases finish, check warning logs for an unreachable peer or failed cleanup. Fences retry
with 1 to 30 second backoff and warn at most once per generation every five minutes. After a
terminal cleanup failure the generation stays counted until client disposal. See
[Sentinel connections](../fundamentals/connections.md#redis-sentinel) for drain and no-replay
behavior.

## Maintenance notifications

With maintenance handling enabled, each valid notification produces a `redis.maintenance`
consumer activity with an event named after its kind: `MOVING`, `MIGRATING`, `MIGRATED`,
`FAILING_OVER`, `FAILED_OVER`, `SMIGRATING`, or `SMIGRATED`.

- Activities carry the receiving endpoint and database, `respire.maintenance.kind`,
  `respire.maintenance.sequence_id`, and any announced seconds or target. They keep the
  receive timestamp and have no command parent. Information logs record kind, sequence, and
  endpoint.
- `redis.client.maintenance.notifications` has endpoint and kind tags, never sequence IDs.
- Diagnostics run serially per connection on a thread-pool worker, so slow listeners never
  block parsing or timeouts. Listener exceptions are isolated.
- Each queue holds 256 events; overflow drops the oldest and reports the count in
  `respire.maintenance.notifications.dropped` when delivery resumes.
- Diagnostics are best effort and can finish after disposal. Malformed notifications and
  replays during negotiation produce none.

Cluster clients count `SMIGRATED` notifications that did not update slot ownership in
`respire.cluster.slot_migrations.skipped`, by `reason`:

| Reason | Meaning |
| --- | --- |
| `queue_full` | The 128-item topology queue dropped its oldest entry; logs a warning at most every 30 seconds per client |
| `malformed` | Invalid slot list, or past the 16,384-slot budget |
| `duplicate` | Sequence ID already seen on the connection |
| `deferral_evicted` | Evicted while waiting for an earlier migration, to bound the list |
| `deferral_expired` | Waited 30 seconds without the earlier migration |

`MOVED` handling and topology discovery still correct the route in every case.

## For contributors

Every public method on a public core type declares its final error-observation owner in a
`<source-file>.cs.ownership.json` file next to the source; `CommandRouteOwnershipTests` fails
until a new method is classified. Rules are in
[`docs/ERROR_OWNERSHIP.md`](https://github.com/thomhurst/Respire/blob/main/docs/ERROR_OWNERSHIP.md).
