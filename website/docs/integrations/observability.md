---
title: Observability
description: Collect OpenTelemetry traces, metrics, logs, and connection state.
---

# Observability

Respire emits OpenTelemetry-compatible activities and metrics from sources named `Respire`.

## Traces and metrics

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire"))
    .WithMetrics(metrics => metrics.AddMeter("Respire"));
```

Instrumentation follows OpenTelemetry database semantic conventions. `db.namespace` reports the configured Redis database index.

Raw command values are not attached to spans because arbitrary Redis payloads cannot be reliably sanitized. Pipelines and transactions are recorded as single operations.

## Latency

Operation latency uses the stable `db.client.operation.duration` histogram and records seconds, as required by the semantic convention. Enable the `Command` metric group to collect it.

## Metric selection

Redis metrics use process-wide selection, shared by every client and prefix view. The
default is `Resiliency | ConnectionBasic`, following the Redis specification. Command
latency and client-side cache metrics now require explicit opt-in, even when an exporter
subscribes to the `Respire` meter. To retain all previously available standard measurements:

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

Configure before creating clients or attaching exporters. Respire validates and copies
both lists; mutating the supplied collections or `RespireMetrics.Configuration` cannot
alter the active selection. `Configure` is process-wide and last-writer-wins: the last atomic
replacement becomes the active policy for every client. Prefer one configuration call in
application startup rather than competing calls from individual components. A replacement
affects newly started telemetry.
An operation retains its selection when telemetry starts. Paths that capture selection before
connection acquisition retain it through the wait and through acquisition failure. An already
selected operation can finish after replacement. Configuration does not
create or own an OpenTelemetry provider, meter listener, exporter, or tracing listener.
There is no client-specific override because the meter is shared by the process.

The option lists are `IReadOnlyCollection<string>` inputs, not immutable value objects.
Record `with` expressions share those input collections, and record equality does not compare
their contents. `Configure` copies and validates the collections before publishing the policy.

| Flag | Redis group | Currently selected standard measurements |
| --- | --- | --- |
| `Resiliency` | `resiliency` | Errors, maintenance notifications, and geographic failovers |
| `ConnectionBasic` | `connection-basic` | Ready connection counts, creation time, active maintenance timeout allowances and published handoffs |
| `ConnectionAdvanced` | `connection-advanced` | Pending replies, dedicated-pool acquisition waits and physical socket closes |
| `Command` | `command` | Logical operation duration, including Redis commands used for pub/sub and streams |
| `ClientSideCaching` | `client-side-caching` | Cache requests and evictions |
| `PubSub` | `pubsub` | Confirmed publications and received messages |
| `Streaming` | `streaming` | Stream lag reported explicitly when application processing starts |

`None` suppresses these standard measurements. Existing `respire.*` diagnostics remain
available independently, including pub/sub gaps, cache invalidations, reconnects, and
thread-pool health. Filter those instruments in the application's exporter when needed.
Selecting a group does not invent a measurement that Respire has not implemented.

Command filters affect only command metrics, never command execution, traces, or other
groups. Names match ordinally without case sensitivity. Use the complete reported command
name, such as `CLIENT LIST`; there are no wildcards or prefix matches. Empty allow lists
allow every command, and block lists always win. Names must contain 1–64 ASCII characters:
letters, digits, `.`, `_`, `-`, and single spaces between words. Null, empty, malformed,
or longer names and unknown group flags are rejected before replacing configuration.

A pipeline or transaction emits one duration only when every constituent command passes
the filters. Homogeneous operations retain names such as `PIPELINE GET` or `MULTI SET`,
but filters match their constituent `GET` or `SET`, not the compound label. Mixed operations
retain `PIPELINE` or `MULTI`; excluding any member suppresses the whole duration. Empty
operations match `PIPELINE` or `MULTI` directly. Durability batches additionally require their `WAIT` or `WAITAOF`
acknowledgement to pass the filters. Script fallback remains one logical
`EVALSHA` (or `EVALSHA_RO`) operation; filters use that initial command. Immediate, raw,
blocking, streamed, and fire-and-forget operations use the same selection as other commands.

Metric labels never include keys, raw channel or stream names, command arguments, payloads,
or credentials. Script digests and function names remain trace attributes and are omitted
from metrics to avoid per-script series growth. Command metric labels retain at most 1,024
distinct names across the process lifetime, case insensitive; additional names share
`OTHER`. Filters always match the original operation name, before label normalization or
the cardinality limit. An allowed command first encountered after the limit therefore still
emits a measurement labeled `OTHER`; allowing or blocking `OTHER` does not select or suppress
other commands that receive that label. Allow-list entries do not reserve label capacity.
Empty, non-ASCII, or names longer than 80 characters also use `OTHER`. The limit
includes compound labels and does not reset when configuration changes. Other standard
attributes still identify configured endpoints, database numbers, batch sizes, and errors.
Metric selection and the label limit do not alter trace names or existing span attributes.

Without a listener, or with the command group disabled or the command excluded, metric
per-command instrumentation avoids timestamps, metric tag formatting, and compound name construction.
Tracing can independently require its own timestamps and span attributes. This does not
make streamed or blocking operations allocation-free; their transport contracts still apply.

A duration histogram listener by itself retains the direct string, byte-array and converted
reply sources on eligible ready primary and Cluster connections. Tracing retains its span
path, and cache, replica, streaming and scripting routing keep their existing exclusions.
Metric selection is captured before admission and remains fixed while a response is pending.
Duration ends when the source completes, before delayed result consumption or user conversion.
Listener callbacks run when the caller consumes the result, outside transport locks; listener
failures cannot replace the command result or its error on this direct path.
An unconsumed direct result does not emit its duration observation.

Each connection caches its database namespace and endpoint tags, including boxed port values.
This moves their formatting cost to connection creation. The three specialized source types
retain a connection reference and two timestamps while an observed request is pending; their
existing pools remain bounded to 4,096 sources per closed source type. Raw response sources
retain their existing layout. Retained storage and public-command latency still need measurement
alongside allocation counts when changing these paths.

## Redis metric mapping

Existing signals use names, units, and attributes from the
[Redis client observability specification](https://redis.io/docs/latest/develop/clients/observability/).
The meter name remains `Respire`.

| Previous instrument | Current instrument | Unit | Attributes and counting |
| --- | --- | --- | --- |
| `respire.client_cache.hits`, `respire.client_cache.misses` | `redis.client.csc.requests` | `{request}` | `redis.client.csc.result=hit` or `miss`; one measurement per cache lookup |
| `respire.client_cache.evictions` | `redis.client.csc.evictions` | `{eviction}` | Removed cached responses; optional `redis.client.csc.reason=full`, `ttl`, or `invalidation` |
| `respire.maintenance.notifications` | `redis.client.maintenance.notifications` | `{notification}` | `redis.client.connection.notification` identifies the notification; `server.address` and `server.port` identify its source |
| Deployment switches between known endpoints | `redis.client.geofailover.failovers` | `{failover}` | `db.client.geofailover.reason=automatic`, `db.client.geofailover.fail_from`, and `db.client.geofailover.fail_to` |
| New instrument | `redis.client.errors` | `{error}` | Internal handling or final operation failure; category, internal flag, exception type, retry count, and known Redis error code |
| Ready sockets | `db.client.connection.count` | `{connection}` | Pool and idle/used state; exported as a gauge |
| Physical connection setup | `db.client.connection.create_time` | `s` | Histogram including handshake, grouped by pool |
| Replies still owed | `db.client.connection.pending_requests` | `{request}` | Ready sockets grouped by pool; advanced group |
| Dedicated-pool acquisition | `db.client.connection.wait_time` | `s` | Dedicated socket acquisition histogram; advanced group |
| Physical closes | `redis.client.connection.closed` | `{connection}` | Close reason and pool; advanced group |
| Maintenance allowances/handoffs | `redis.client.connection.relaxed_timeout`, `redis.client.connection.handoff` | `{relaxation}`, `1` | Current allowance gauge and published replacement counter; basic group |
| `db.client.operation.duration` | Unchanged | `s` | Existing logical operation latency and database attributes |
| New measurement | `redis.client.pubsub.messages` | `{message}` | One message per confirmed publication or accepted incoming frame; direction `out`/`in` and sharded boolean |
| New measurement | `redis.client.stream.lag` | `s` | Entry timestamp to explicit application processing start |

The standardized counters and stream-lag histogram carry `redis.client.library=Respire:<version>` and
`db.system.name=redis`. Keys, command values, and credentials are never metric labels.
An invalidated key can remove several responses or none; eviction counts reflect actual
removals caused by capacity limits, local expiration, or server invalidation. Local
mutation, explicit clearing, and continuity flushes do not increment this standardized
counter. Respire-specific invalidation and continuity-flush instruments remain available.

This prerelease replaces the old cache and maintenance names without legacy aliases or
dual emission. Update exporter filters and dashboards when upgrading. The broader
`respire.failover.endpoint.switches` counter remains: it also records initial selection
and transitions to or from having no healthy endpoint. Those events are excluded from
the standardized geographic failover counter. Cache statistics keep their existing
meaning and are independent of exported metric totals.

Respire-specific instruments remain available for hedging, availability zones, thread-pool
health, coordination, Sentinel recovery, cache invalidation notifications, continuity
flushes, and pub/sub delivery gaps. Mapping existing signals does not imply that every
instrument or configuration group in the Redis specification is implemented. Additional
dashboard coverage is tracked
by [#866](https://github.com/thomhurst/Respire/issues/866).

## Prometheus and the published Redis dashboard

The [smoke sample](https://github.com/thomhurst/Respire/tree/main/samples/Respire.Samples.Observability)
exports actual Redis measurements through OpenTelemetry's Prometheus ASP.NET Core exporter
`1.19.1-beta.1` (SDK `1.19.1`). It checks names, units, forms, and labels against every query in
[the published Redis dashboard at revision `033fe86e47440da7f365ed2d8dd7f5d6217a575a`](https://github.com/redis-developer/redis-client-observability/blob/033fe86e47440da7f365ed2d8dd7f5d6217a575a/grafana/dashboards/redis-client-observability.json).
Pinned promtool `3.5.0` evaluates supported adapted queries against series from that export.
CI runs the smoke on .NET 8 and .NET 10. Reproduce from the repository root:

```powershell
pwsh samples/Respire.Samples.Observability/Smoke.ps1 -OutputDirectory ./observability-evidence
```

Prometheus replaces dots with underscores. Seconds histograms have `_seconds_bucket`,
`_seconds_sum`, and `_seconds_count`; monotonic counters have `_total`. Braced counting units
add no other suffix with this exporter. Thus errors become `redis_client_errors_total` and
stream lag becomes `redis_client_stream_lag_seconds_bucket`. Up/down instruments become gauges.
The sample explicitly sets histogram boundaries to `0, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10`
seconds, rather than using generic histogram buckets for latency.

| Dashboard panels | Required group or feature | Compatibility and scope |
| --- | --- | --- |
| Connections, pool state/summary, creation time | Default `ConnectionBasic` | Ready physical sockets. Multiplexed socket state is not command concurrency. |
| Error rates/type/category | Default `Resiliency` | Includes handled internal failures and final operation failures. The command success formula is approximate: errors are not one failed logical command each. |
| Latency, throughput, success rate, heatmap | Opt-in `Command` | One logical operation, including a pipeline/transaction, not one sample per wire command. Cache hits need not execute Redis commands. |
| Pending requests, closes, wait time | Opt-in `ConnectionAdvanced` | Replies owed, physical closes, dedicated-pool acquisition. Wait time excludes multiplexed admission and general command latency. |
| Cache ratio, requests, evictions | Opt-in `ClientSideCaching` plus enabled cache | Actual lookup/removal counters. An eviction is not an invalidation notification. |
| Network bytes saved, cached items | Unavailable | Respire does not emit `redis.client.csc.network_saved` or `redis.client.csc.items`. These two queries are explicitly unsupported. |
| Message rates | Opt-in `PubSub` plus publications/subscriptions | Replace `redis_client_pubsub_direction` with `redis_client_pubsub_message_direction`. |
| Message rate by channel | Opt-in `PubSub` | Channel names are absent. The adapted sample groups by direction; this is not channel ranking. |
| Stream/consumer lag | Opt-in `Streaming` plus explicit `RecordProcessingStart()` | Remove `redis_client_stream_name` and `redis_client_stream_consumer_name` grouping to show aggregate lag; these names are absent. |
| Geo-failovers | Default `Resiliency` plus geographic deployment | Published switches between known endpoints only. Initial selection and generic failovers differ. |
| Timeout relaxations/handoffs | Default `ConnectionBasic` plus maintenance-capable server/events | Current increased allowances and published `MOVING` handoffs. Standalone Redis generates no such events. |

The original dashboard selects `exported_job` from its Collector/Prometheus setup. A direct
ASP.NET Core scrape supplies resource metadata through `target_info`, not that label.
The sample's adapted dashboard selects `db_system_name="redis"` and changes its service variable
to the database-system label. For multiple services, use actual scrape `job` or Collector
resource labels to retain service isolation. This adaptation scopes one smoke export.

The report preserves all original/adapted queries: 42 verified targets, three requiring geographic
or maintenance features, and two unsupported cache measurements. Private channel/stream/consumer
panels cannot retain their original grouping. The check enables no name-disclosure option.
Constant repeated exported samples validate query compatibility, not performance; production
rates require successive live scrapes over a rate interval. Separate `respire.*` diagnostics for
reconnects, generic failovers, invalidation notifications, and pub/sub gaps are not substitutes
for the standardized panels.
Broader schema/configuration acceptance remains tracked in [#866](https://github.com/thomhurst/Respire/issues/866).
[#928](https://github.com/thomhurst/Respire/issues/928) remains open until its children and original acceptance are complete.

## Pub/sub messages and stream lag

Enable `PubSub` and/or `Streaming` in `RespireMetricsOptions.Groups`. Command filters
do not filter these observations. Channel, pattern, stream, group, consumer names,
entry IDs, and payloads are never labels; there is no name-disclosure option.

`redis.client.pubsub.messages` counts an `out` message when Respire processes a
successful integer reply to `PUBLISH` or `SPUBLISH`. A subscriber count of zero or
many still represents one publication. Typed, raw, and fire-and-forget publication
paths use this boundary. A canceled caller can still contribute if its successful
reply arrives later. Server errors, lost replies, and publications inside arbitrary
server-side scripts cannot establish another confirmed publication. Deferred raw
queues do not currently accept `PUBLISH`/`SPUBLISH`.

An `in` message is one accepted incoming channel, pattern, or sharded frame on a
current subscription connection. Local fan-out to several subscriptions counts
once, even if their buffers drop messages. Two distinct server frames, such as
channel and pattern deliveries of the same publication, count twice. Reconnect
markers and rejected stale routes do not count. Existing drop and gap instruments
remain separate. Metric callbacks run outside subscription routing/buffer locks;
listeners must remain short and non-blocking.

Both directions include `redis.client.pubsub.sharded`. Selection is checked when
the message observation occurs. Fire-and-forget commands retain their operation
metadata only when publication collection is enabled at enqueue (or existing
Sentinel bookkeeping already requires it); enabling collection cannot reconstruct
missing metadata for an earlier discarded reply.

The stream histogram requires an explicit application boundary:

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

Call `RecordProcessingStart()` once when each processing attempt actually begins,
including after any application queueing. Reads, iterator delivery, claims, and
acknowledgements do not automatically emit lag and do not prove handler completion.
Repeated calls intentionally produce repeated observations; the method does not
acknowledge, mutate, or deduplicate an entry.

Lag uses the millisecond portion of a complete `milliseconds-sequence` ID and the
client's UTC clock. Use it only when IDs represent creation time, normally IDs
generated by Redis. Numeric custom IDs cannot be distinguished from timestamps;
do not report them unless their timestamp meaning is known. Clock skew affects the
result; future timestamps and malformed/sentinel IDs are skipped rather than
reported as negative or fabricated zero lag. With the group or listener disabled,
the method performs no timestamp read, ID parsing, or allocation. Listener failures
cannot change publication, subscription, or application processing outcomes.

## Connection lifecycle

| Instrument | Group | Unit | Measurement boundary |
| --- | --- | --- | --- |
| `db.client.connection.count` | `ConnectionBasic` | `{connection}` | Current ready TCP connections, split into `idle` and `used` |
| `db.client.connection.create_time` | `ConnectionBasic` | `s` | Successful socket connection, TLS and Redis handshake, ending after generation validation |
| `redis.client.connection.relaxed_timeout` | `ConnectionBasic` | `{relaxation}` | Current connections with an active maintenance window that increases a configured command or response timeout |
| `redis.client.connection.handoff` | `ConnectionBasic` | `1` | One per old physical connection replaced by a successfully published `MOVING` handoff |
| `db.client.connection.pending_requests` | `ConnectionAdvanced` | `{request}` | Replies still owed on ready connections, including canceled replies that still need draining |
| `db.client.connection.wait_time` | `ConnectionAdvanced` | `s` | Waiting for a newly created dedicated lease, including connection recovery; healthy idle reuse emits no wait |
| `redis.client.connection.closed` | `ConnectionAdvanced` | `{connection}` | One event after each connected TCP socket closes, including failed TLS or Redis handshakes |

These instruments carry `redis.client.library`, `db.system.name` and
`db.client.connection.pool.name`. Pool names combine configured host, port, database and
purpose (`shared`, `dedicated`, or `pubsub`). Connections with the same identity aggregate,
including connections owned by separate clients. No credential, key or payload enters a pool
name. The process retains at most 64 distinct pool identities and two overflow identities,
`overflow/shared` and `overflow/pubsub`. Names longer than 256 characters also overflow.
Zero-valued series remain observable after closure within that bound; configuration changes
do not reset the budget. The first 64 identities are never evicted during the process lifetime.
After endpoint churn fills that budget, later endpoints use overflow even when the original
endpoints have no live connections. Restarting the process resets the budget; preserving old
series avoids reassigning their history to new endpoints. Overflow aggregates endpoints and
databases, retaining pub/sub identity.

The count adds `db.client.connection.state` and `redis.client.connection.pubsub`.
A multiplexed connection is used while a reply remains owed; a dedicated connection is used
while rented. Pub/sub connections are always used. A canceled command remains pending until
its frame drains. A streamed reply also stays pending until its payload and trailing frame
delimiter drain, including when the caller disposes the stream early. These are transport
observations, not application tasks or pool capacity.
During replacement, both old and new ready sockets can count until the old socket closes.
Connections still negotiating their handshake, in-memory test streams and Unix sockets are
excluded from these TCP measurements.

Current counts, pending replies and relaxed allowances use .NET
`ObservableUpDownCounter<long>`. Redis v0.2 lists synchronous up/down counters; both forms
export an OpenTelemetry non-monotonic Sum. Observation provides truthful baselines to late,
independent or re-enabled listeners without replaying historical lifecycle events. Group
changes affect the next observation; event instruments emit only events selected when they
occur. An exporter collects the observable instruments on its normal interval.

Timeout allowances return to zero when matching completion arrives, the bounded maintenance
window expires or the socket closes. They count connections with increased allowances, not
requests granted an extension. Duplicate notifications do not add allowances or handoffs.
A failed or superseded replacement emits no handoff. Ordinary reconnects are creation/close
events and do not count as `MOVING` handoffs.
Handoffs include idle and borrowed dedicated connections from the retired pool, including
streaming leases, across standalone, Sentinel and Cluster routing. Repeated retirement does
not count them again. Connections already closing or still negotiating at publication are excluded.

Close events add `redis.client.connection.close.reason`: `application_close` for intentional
disposal, retirement or caller cancellation during a handshake, `pool_eviction_idle` for
dedicated idle-capacity overflow, `server_close` for observed peer EOF or connection reset
(including a reset wrapped by TLS), or `error` for transport/handshake failure. An internal
handshake timeout remains an error; caller cancellation requires the matching canceled token.
Unknown I/O failures remain errors. Error closes
also include `error.type` and `redis.client.errors.category` (`network`, `tls`, `auth`,
`server`, or `other`). Respire has no separate healthcheck-driven pool eviction, so it does
not emit `healthcheck_failed`. Close events do not count unsuccessful TCP connection attempts.

Connection instrumentation adds no metric callbacks to command submission. Listener callbacks
run outside transport and dedicated-pool locks; listener exceptions cannot replace connection,
lease or disposal outcomes.
Creation/wait durations, handoff counts and close counts are queued for delivery on the thread
pool. Durations and metric enablement are captured at the event, before queueing; scheduling
delay does not inflate the measured duration. Close reasons and live membership changes also
commit synchronously. At most 64 lifecycle measurements can be queued or executing across
the process. When that limit is reached, new measurements are dropped without waiting or
allocating another work item. If enqueueing fails, the measurement is also dropped.
Capacity becomes available when a callback returns, including after it throws. A listener
that never returns can exhaust this delivery capacity; live observable counts and transport
cleanup still continue, while lifecycle event counts may under-report during saturation.
The `Respire` meter exports `respire.connection.measurements.dropped`, an observable
counter with unit `{measurement}` and no tags. It reports cumulative process-wide capacity
and enqueue drops, including drops before a listener subscribes. It does not count listener
exceptions or unflushed shutdown events. This Respire diagnostic is separate from the Redis
standard metric groups. Scraping reads the stored total directly, so saturation cannot drop
the diagnostic itself and no listener callback runs on the transport path. The delivery budget is shared by
all clients and pools, so blocked listeners on one pool can consume capacity needed by others.
Acquisition and disposal do not wait for delivery. A blocking lifecycle listener therefore cannot
stop a rental from completing, pending replies from failing, pool cleanup, or retirement scheduling.
An exporter can observe live state changes before the corresponding events arrive, and queued
events can arrive out of order. Listeners should remain enabled until queued events have been
collected. Queued lifecycle measurements are best effort and can be lost at process shutdown;
client disposal does not flush them. MOVING captures both shared and dedicated live sockets
at publication, so a lease closed afterwards still counts and a handshake completed afterwards
does not. The retirement cache fence, dedicated-owner notification and idle cleanup precede
queueing handoff measurements.

## Error measurements

`redis.client.errors` is a counter in the `Resiliency` group, independent of command
filters and latency collection. Each measurement is one error at an observed boundary,
not one failed command inferred from a disconnected socket.

Observed caller boundaries include connection establishment, immediate typed and raw
commands, scripts, blocking commands, streamed uploads, cached reads, batch execution,
transaction commit, and fire-and-forget submission. The counter is not an exception
constructor hook: throwing or inspecting an exception elsewhere does not itself emit
a measurement. Selection is checked when an error is reported.

Pub/sub activation keeps one final owner through admission, every control reply,
MOVED/ASK redirects, and rollback. Background sharded recovery and notification
reconciliation have independent internal owners; their redirects and rejected
subscriptions are handled errors, not caller failures. Unsubscribe failures that
cleanup consumes are internal too. Cancellation caused by subscription shutdown
does not emit an error. These owners acquire error-observation storage only on
failure, and retain retry counts when collection or a listener is enabled later.

When adding a command route, identify its final observation owner and test both a
handled retry and the failure delivered to the caller. Delegating routes borrow that
owner; shared producers, deferred results and payload reads need their own lifetime
boundaries. The independent route inventory and ownership consolidation are tracked
in [#1046](https://github.com/thomhurst/Respire/issues/1046). Current runtime controls
cover individual routes; they do not mechanically classify every public entry point.

Include synchronous command construction and execution admission in that review.
Multi-key builders can reject cross-slot inputs before dispatch, and batch execution
can reject a disposed, already-sent, or busy import session before command owners exist.
These preflight boundaries report one final error themselves. Keep their catch scope
limited to construction or admission so a delegated response owner does not report twice.
Test rejected construction with no command sent, successful construction with a reply,
and enabled and disabled collection. Batch admission controls must preserve pending
results and the batch's single-shot state.

The logical boundary rents an error observation even when collection is disabled, so
later activation retains completed retry counts. Sequential routing, transport, and
conversion helpers borrow that observation and never return it or publish a nested
final failure. Return the observation only after those borrowers and cleanup finish.
Shared cache producers and hedge legs can outlive the caller, so they own independent
observations; callers copy completed counts rather than share an outstanding handle.
Generation checks protect storage reuse but do not extend a borrower's lifetime.

Error-instrument publication is also isolated from application failures. A listener
that throws while the counter is first published cannot replace the caller's exception
or permanently disable reporting. Publication can succeed after that listener is removed.

Connection-string parsing failures in `RespireClient.ConnectAsync(string)` are final
connection failures, including null or malformed input. Parsing preserves its synchronous
exception behavior. The same failure-only owner covers parsing, structured setup, handshake and failed-client
cleanup, so authentication or cancellation failures are not counted twice.

Server/admin commands, CLIENT filters and handles, HOTKEYS handles and fan-outs, and
explicit-node commands start their final owner before argument, option and admin
checks. Successful calls rent no error-observation storage. These owners include reply
parsing and cleanup; per-node failures publish only after `ReleaseServerPoolAsync`
finishes. Fan-out discovery failures belong to the enclosing caller owner. Typed conversion retains the operation's
observation through transport retries or redirects, so a converter failure reports
the completed retry count. The conversion source returns its lease before publishing
the caller's result and preserves the original exception and cancellation token.
Cluster-target fan-outs retain transport and target-replacement attempts in one observation; node-result
fan-outs count each failed endpoint before returning its error result. Blocking commands
and scripts include disposed-client rejection. Correction scopes that suppress their
inner observer keep reporting after their own cleanup.

Ordered distributed-cache corrections borrow their independent observation through
socket broadcasts and original-peer recovery. Handled retirement and connection-loss
retries contribute to the caller's final retry count when the caller joins correction.
A failed ordering fence remains the final failure rather than a handled retry.

Tracked scripts share one observation across transport retries, Cluster redirects,
and `NOSCRIPT` fallback. A direct response read owns the final observation. When a
tracked caller uses the correction boundary, final reporting waits for correction
and mutation cleanup and describes the exception that actually reaches the caller.
This includes tracked distributed-cache writes and coordination scripts.

Public scripts also retain transport retries across `NOSCRIPT` fallback. Connection
candidate cancellation reports the failures completed before cancellation. FUNCTION
fan-outs retain one caller owner through discovery, every primary send, conversion,
consistency checks, and cleanup. Targets borrow that owner. Even if several targets
fail, the caller publishes one final failure after every target finishes.
Server fan-outs observe topology discovery failures
before per-node work starts. The sequential Cluster `DBSIZE`, `FLUSHDB`, and `FLUSHALL`
operations retain one owner through discovery, target sends, reply conversion, and
mutation cleanup. A target borrows that owner, so its failure is not counted again
at the public boundary.

Raw, catalog, and interpolated stored-procedure calls retain one owner through
disposed-client admission, transport reroutes, Cluster redirects, and cache cleanup.
The final measurement preserves the completed retry count, including when collection
is enabled while the operation is pending.

FUNCTION routes start their caller owner before argument validation and command
construction, including span calls and the reusable-library LOAD overload. Execution
retains that owner through missing-function reload, library verification, replica
propagation retries, typed conversion, and result disposal. Private reload tasks join
before final reporting and never publish a nested final failure for a recovered library
load. Successful function routes rent no error-observation storage; warmed caller
ownership adds no allocation. Raw `FCALL` and `FCALL_RO` keep their existing failure-only
owner and completed transport retry count, including when collection starts late.

Pending raw, cached, and fire-and-forget submissions keep their final observation
boundary even when collection is disabled at dispatch. Enabling the group or attaching
a listener before completion can therefore observe their final failure. Earlier errors
are not replayed.

`redis.client.errors.category` is `network`, `tls`, `auth`, `server`, `cancelled`, or `other`.
Surfaced cancellation uses `cancelled` because the client cannot infer who requested it.
TLS certificate failures use `tls`; Redis authentication and authorization rejections
use `auth`. `error.type` identifies the exception type. Known wrappers are unwrapped
for classification without changing the exception delivered to the caller.
`db.response.status_code` contains a recognized Redis error prefix, such as `WRONGTYPE`
or `NOSCRIPT`. Unknown prefixes are omitted because scripts and modules can put
application data in them. Exception messages, credentials, keys, and payloads are never
labels. Error measurements currently omit endpoint labels.

`error.type` uses the runtime exception type's full name for the first 128 distinct
canonical names observed in the process. Additional names use `_OTHER`; previously
accepted names remain stable. This bounds this attribute to 129 values, including
exceptions from application codecs and dynamically generated generic types. The
weak type-name cache does not keep collectible types or assemblies alive. Its small
name registry retains only the accepted strings, so a recreated type with an accepted
name still uses that name after the budget is exhausted. Reuse exception types rather
than generating a distinct type for each operation.
The first report for an exception type can initialize its cached name. Warm reports
reuse that name and the common retry-count tags; first-use initialization is outside
the warmed allocation contract. Retry owners retain their counts even while collection
is disabled, so activation before completion does not reset those counts.

`redis.client.errors.internal=true` identifies a handled error: a cluster retry,
`NOSCRIPT` fallback, unsuccessful connection candidate, background reconnect attempt,
physical connection abort, or an error reply discarded after fire-and-forget submission
or cancellation. `false` identifies a final failure delivered at an observed connection
or command boundary. A recovered `NOSCRIPT` produces only an internal measurement;
if its fallback fails, that final failure produces a separate user-visible measurement.
`redis.client.operation.retry_attempts` is zero initially and increases across observed
retries, including redirects followed by script fallback.
It remains an exact non-negative integer, including counts above 16, as required by the
[Redis observability specification](https://redis.io/docs/latest/develop/clients/observability/).
The boxing cache is bounded independently of the reported count; it does not retain a
cache entry for every count. Exporters that need a fixed series budget can exclude this
attribute with a metric view instead of changing the client's retry metadata.
Replica ROLE rejection on shared or dedicated connections is an internal candidate
failure even when primary fallback succeeds. Successful connection establishment is
not counted again for that rejection. Cluster read selection also retains rejected
replica connection candidates before another replica or the primary succeeds. The
aggregate no-healthy-replica wrapper is not a second handled error. A Cluster batch
reports its shared selection failure once, then copies that count to each deferred
command owner; a later command failure includes both selection and send retries.
Standalone read selection, cursor reselection, Nearest selection and dedicated
replica acquisition borrow the enclosing caller's failure-only owner. Rejected
connection and ROLE candidates increment that caller's retry count, including
when collection starts during selection. A later reply or conversion failure
reports the accumulated count after cleanup; successful selection rents no error
observation storage. Optional hedge selection and each hedge leg retain independent
failure-only owners because they can finish after the caller. Their failures are
internal; only the completed result leg's retry history is copied to the caller.
Background replica reconnects and failover probes retain internal ownership and
do not publish an additional caller failure.
A physical socket close already owns its internal event. If that same failure
rejects a selection candidate, selection increments the caller's retry count
without publishing the physical event again. Every affected caller still owns
its distinct final failure.
Failed ordinary and sharded subscription recovery
has its own internal owner; handled redirects and terminal rejections remain separate
events. Cancellation caused by subscription shutdown is excluded.
Additional rejected prefix or transaction queue replies are internal events when
discarded; the retained error is reported once at the caller's final boundary.
Hedged reads add the completed result leg's retry count to retries already handled by
the caller; the discarded leg retains its own internal error boundary.
Standalone submission retries retain the same count through both immediate retirement
and capacity waits; a later write/submission failure reports the accumulated count.
`ConnectAnyAsync` also reports null or empty candidates and enumeration failures once
at its final boundary. These failures and caller cancellation retain the number of
completed failed candidates. Exhaustion retains the last candidate's retry index.

Streaming reads include transport reroutes in the same attempt count as Cluster
redirects. After a successful header, the payload stream retains that count for a later
read failure. Streamed uploads copy the count into their pending replies, including
the `ASKING` prelude, so a discarded reply retains its submission count even after
caller cancellation returns the operation's observation lease. WATCH setup starts its
observation before key mapping and cluster-slot validation; a rejected cross-slot WATCH
reports one final error without submitting a WATCH command.

Multi-key list and sorted-set pops, including typed and blocking variants, and
multi-element list moves report construction-time cross-slot rejection once with
zero retry attempts. No command is submitted; after successful construction, the
existing response-conversion boundary owns subsequent failures.
Typed XREAD and XREADGROUP also report cross-slot validation failures once before
submitting a command. Continuous XREAD retains one observation across pages: recovered
failures are internal, and a later terminal failure includes completed retry attempts
even when collection was disabled during those retries. Ending enumeration after a
successful page does not create a final error.
Guarded cache removal reports its final error after the removal lease is
revoked or expires and any owned timeout is translated to the caller's `UNLINK` error.
Lease placement and the removal script borrow the same retry owner. Background
revocation retains a separate lifetime because it can outlive the caller's lease.
Distributed-cache GET and refresh, and semaphore acquisition, renewal and release, retain their final error
boundary until required TTL correction or owner-only release finishes. No final
measurement is published while that cleanup is pending. If correction replaces the
original failure, the final measurement describes the exception delivered to the caller.

Semaphore owners start before expiry validation, cancellation checks, connection
preflight or permit gate waits. Verification includes reply conversion in its owner.
Capacity mismatch reports the mapped `RespireSemaphoreCapacityMismatchException` once.
Script retries and failed surrender attempts retain the caller's retry count. Disposal
and cleanup that outlives the bounded foreground wait report internal failures under
separate lifetimes. Successful semaphore calls acquire no error-observation lease.

Error observation storage is pooled, and each borrower carries its rental generation.
Debug builds reject stale borrowers with `InvalidOperationException`; Release builds
ignore them without touching another operation's retry count or final-error state.
The generation check protects reuse, but ownership must still cover every pending reply
and required cleanup before the observation is returned.

A physical connection abort is counted once at its handling boundary. A command failure
is counted separately only when its own completion reports failure; aborting a socket
does not manufacture a user-visible error for every in-flight command. Re-reading an
exception does not create a new measurement. Caller cancellation retains the original
exception and token. Listener exceptions are isolated from propagation and recovery.

Caller-requested cancellation is intentionally included when it escapes an observed
operation: category `cancelled`, with its original cancellation exception type. It is not
evidence of a transport outage. `RespireTimeoutException` uses `network` for the failed
client transport/deadline boundary; this does not identify whether the underlying cause
was a slow server, pool contention, or a network fault. Filter by `error.type` when
separating cancellations or deadlines from outage alerts. No `timeout` category is introduced.

Wrapper traversal is capped at sixteen links to bound observation work for external
exception chains. A deeper chain is classified from the remaining wrapper, so its
innermost server code may be absent. Reporting never changes the original exception.
Respire exception wrappers declare their classification cause through one internal
accessor. A wrapper with meaningful authentication or multi-candidate connection
semantics keeps its own identity. A new wrapper can opt into cause classification
without adding a type-specific branch to the metric classifier.

Error observations use immutable value handles containing pooled storage and its lease
generation. Copies borrow the same active lease. Validation and mutation share the
storage gate, so a returned handle cannot change retry counts, report a final failure,
or return storage belonging to a later renter. Release builds ignore stale calls;
Debug builds reject stale observation calls, including after the storage is rented
again. Duplicate final reports during active ownership and duplicate disposal remain
idempotent. Owners must still finish their borrowers before returning the lease.
The independent route inventory and broader ownership consolidation remain tracked in
[issue #1046](https://github.com/thomhurst/Respire/issues/1046).

Native lock calls start a failure-only caller owner before token or duration validation,
capability checks and renewal gate waits. Acquisition, native command fallbacks,
EVALSHA/EVAL retries and managed release fencing borrow that owner. The final error
is published only after cache mutation fences, connection fencing and renewal deadline
cleanup finish. Successful calls do not rent error observation storage.
Concurrent managed-release callers each publish their own final failure and retain the
shared attempt's retry count. Disposal reports a swallowed connection, timeout, disposal
or cancellation failure as internally handled; a server rejection that disposal propagates
remains one caller-visible failure. Contention becomes an error only when an
`AcquireOrThrowAsync` call throws `RespireLockNotAcquiredException`.

Failed batch and transaction commands produce one user-visible measurement per faulted
deferred result, after the owner finishes correction and connection cleanup. Reading
`Result` again or calling `ThrowIfAnyFailed` does not add another measurement.
Standalone batch transport retries retain each pending command's own retry count.
A failure of the execution itself, such as a durability acknowledgement failure, is counted when
there are no failed deferred results representing that execution. WATCH aborts and
intentional batch or transaction disposal are not errors for this counter.

Fire-and-forget submission failures are user-visible errors. Replies that its contract
intentionally discards are internal errors, including the replies awaited for cache
invalidation fencing or cluster routing. Exhausted cluster routing still surfaces to
the caller and is counted as a final failure.

Cached reads count final failure once for each waiting caller. A shared producer does
not add another user-visible failure when it faults several coalesced waiters. Internal
producer retries are counted at their handling boundary; each cache waiter inherits the
producer's completed retry count in its final measurement. Cache hits, peek conversion,
and cache-aside factories keep the same caller boundary without renting error observation
storage on success. Each coalesced `GetOrSetAsync` waiter owns its own conversion and final
failure, including failures after the factory succeeds.

Distributed-cache operations start ownership before validation, payload encoding and
correction setup. GET and buffered GET retain it through payload decoding and response
disposal. SET and refresh retain it through foreground TTL correction. Detached correction
passes keep an independent failure-only owner, so they cannot reuse an already completed
caller's lease. Foreground correction retries are included in the caller's completed count;
late handled failures remain internal measurements.

Hedge races report their final
outcome with the completed result leg's retry count; each leg retains its own retry owner
until its reply finishes, including a loser that outlives the caller. Each failed discarded
hedge leg contributes one internal measurement, including a late loser; when both legs
fail, their internal observations are separate from the race's final caller failure.

Stream read pages count validation, key resolution, command construction, reply parsing,
and cancellation at the same final boundary. Consumer-group iterators establish ownership
on their first `MoveNextAsync`, including options and batch-size validation; page dispatch
borrows that owner until enumeration ends or the iterator is disposed. Page APIs and
options-based iterators reject null group or consumer arguments before sending a command.

Continuous `ReadAllAsync` reads retain one failure-only owner across pages and recovery.
Resolving an initial `$` cursor belongs to that owner and fails without recovery. Recovered
read failures are internal measurements; a later terminal failure carries the total retry
count and contributes one final measurement after page cleanup. Successful reads and early
iterator disposal do not rent error observation storage or report a final failure.

Streaming GET counts header or acquisition failure at the command boundary. After a
stream is returned, its first observed payload failure is counted once; repeated reads of
the same failed stream do not add measurements. Canceling an individual read counts that
read's cancellation without marking the payload failed: reading can resume, and a later
payload failure has its own measurement. A socket failure without an observed
stream read failure contributes only its internal connection measurement. Invalid
buffer arguments, unsupported stream operations, and reads after disposal are excluded
from payload error observations.

Payload read failures acquire the failure-only observation lease at the error boundary.
Successful reads acquire no error observation storage. Streaming uploads retain the
enclosing caller's owner from preflight through source reads, route retries, reply parsing,
and dedicated connection and mutation cleanup. Internal upload entry points start a
failure-only owner when none is supplied. An error reply already completed when a frame
write fails is counted once as internal with its copied retry count; the caller-visible
write failure remains final. Checked streaming prefixes likewise keep prefix and discarded
command errors separate, including deferred completion and caller cancellation while both
replies drain. Cancellation status, tokens, and the original payload failure are preserved.

## Reads by availability zone

When `ClientAvailabilityZone` is configured, the `Respire` meter exposes the observable
counter `respire.read.availability_zone` (unit `{read}`). It counts read commands accepted
by physical connections, tagged with `server.availability_zone` and
`respire.availability_zone.status=known`. These are attempts, not successful operations:
a redirected or retried command can count more than once. Local cache hits do not count.
Individually submitted batch reads count; composite transactions do not.
All read policies count, including `Primary` and `Replica`; affinity is not required.

Missing zone metadata uses `respire.availability_zone.status=unknown` without a zone tag.
The process retains counters for at most 64 distinct zone names; all clients share this
process-wide budget. Names longer than 128 UTF-16 code units go directly to overflow
without consuming this budget. This limit applies only to exported metric labels;
routing retains the full metadata and compares zone names ordinally. Additional names share
`respire.availability_zone.status=overflow`, also without a zone tag. Totals survive
connection disposal. The first 64 names retain their counters for the process lifetime;
names are never evicted, so later names continue to use overflow even after old connections
close. This preserves monotonic totals without resetting or relabeling a counter.
Zone names come from server metadata, not command keys or values. A server reporting
many distinct names can exhaust the budget, whether through normal topology changes,
misconfiguration, or hostile metadata. Overflow is intentionally reachable: it bounds
the number of exported zone series without dropping read counts. It is not a trust check.
The status tag distinguishes real zone names such as `unknown`
from missing metadata. Observation happens outside transport locks; accepting a read
only increments its connection's cached counter.

## Hedged reads

Opt-in [hedged reads](../fundamentals/connections.md#hedged-reads) expose three instruments:

| Instrument | Kind | Meaning |
| --- | --- | --- |
| `respire.read.hedge.sent` | Counter | Additional requests started |
| `respire.read.hedge.won` | Counter | Successful additional replies returned to callers |
| `respire.read.hedge.extra_load` | Histogram | One 0/1 sample per completed eligible logical read |

The mean of `extra_load` is the ratio of additional requests to eligible logical reads.
Multiply by 100 for a percentage. Sent/won tags identify the hedge endpoint; extra-load tags
identify the original endpoint. All use `server.address` and `server.port`, without keys or
payloads. Logical completion records the sample even if a losing reply is still draining.
Transport redirects and topology probes are not hedge starts. A histogram window can exceed
the configured lifetime percentage when saved credit is spent during that window.

## Logging

Pass an `ILoggerFactory` through `RespireOptions` or use the dependency-injection integration. Logs cover connection lifecycle and recovery without logging command payloads.

## Health state

For health checks or dashboards, combine `IsConnected` with `ConnectionStateChanged`:

```csharp
redis.ConnectionStateChanged += change =>
    healthState.Update(change.Endpoint, change.State, change.Error);
```

A transient reconnection is expected to move through connection states; use application-level thresholds before paging an operator.

## Thread-pool scheduling

A shared background thread samples thread-pool scheduling once per second while at least
one client has monitoring enabled (the default). It queues one probe at a time and measures
how long the pool takes to execute it. It can report a pending probe even when every worker
is blocked. Successful Redis commands do not update this monitor.

The `Respire` meter exposes four observable gauges:

| Instrument | Unit | Meaning |
| --- | --- | --- |
| `respire.thread_pool.scheduling.delay` | seconds | Latest probe's scheduling delay |
| `respire.thread_pool.workers.busy` | threads | Maximum minus available worker threads |
| `respire.thread_pool.workers.min` | threads | Configured minimum worker threads |
| `respire.thread_pool.work.pending` | work items | Process-wide queued thread-pool work |

When delay reaches `ThreadPoolWarningThreshold` (500 ms by default), the
`Respire.ThreadPool` logger emits a warning with counters and remediation guidance.
Warnings are limited to one per client every 30 seconds. Inspect synchronous blocking
and long-running work; prefer asynchronous I/O. Respire never changes thread-pool limits.
Clients can have different thresholds and logger providers, so warnings are delivered
per client. Multiple clients sharing a logging sink can therefore report the same stall.
Logging is best effort and runs serially on the sampler thread. Logger providers must
return promptly; a blocking provider delays sampling and other clients' warnings.

`RespireTimeoutDiagnostics.ThreadPoolProbe` retains the latest immutable sample, including
`CapturedAt` and `IsPending`. A pending delay is a lower bound until that probe executes.
Inspect the timestamp when judging freshness. GC pauses or process suspension can also
increase delay, so a warning alone does not prove thread-pool starvation.
Recovery becomes visible on a later sample, not immediately when the probe runs; a
recovered pool can still show a delayed observation until the next one-second sample.

Configure the threshold or disable monitoring for a client:

```csharp
var options = new RespireOptions
{
    Endpoints = { new RespireEndpoint("localhost") },
    ThreadPoolMonitoring = false,
    ThreadPoolWarningThreshold = TimeSpan.FromSeconds(1)
};
```

Disabling monitoring removes that client's subscription and warnings. Other clients may
keep the shared probe running, so its process-wide sample can still appear in timeout
diagnostics. Disposing the final subscribed client stops sampling and clears the current
sample. Metrics have no value before the first sample or after the final subscription ends.
Diagnostics captured after the final subscription ends have no probe sample; previously
captured timeout diagnostics retain their immutable sample.
If a probe is still queued when monitoring restarts, its original queue timestamp is
retained. Its delay includes the interval with no subscribers because that work item
has still not executed; restarting monitoring does not reset an existing scheduling stall.

## Cluster retirement

`RespireClient.GetClusterRetirementSnapshot()` returns an immutable aggregate snapshot
for a Cluster client, or null for a standalone/Sentinel client. Use the concrete client
registration when collecting these diagnostics from dependency injection.

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

Capture performs no network I/O. It locks the router while reading retained membership
and briefly reads dedicated-pool state under each pool's gate. Transport counters may
change during capture, so the result is a best-effort observation rather than a globally
atomic completion barrier. Avoid polling it on every command. Prefix views report the
same underlying router. Capture after root-client disposal begins throws
`ObjectDisposedException`; a capture racing disposal may return its final observation.
Already captured snapshots remain usable and contain no router, connection, pool, or
exception references. No history is retained by the client for diagnostics.
`CapturedAt` is UTC wall-clock metadata; retirement ages use monotonic timestamps and
are not computed by subtracting wall-clock values.

| Observation | Meaning |
| --- | --- |
| `RetiringGenerationCount`, `OldestRetirementAge` | Detached generations still owned; age uses monotonic elapsed time and is zero when none remain. |
| `UndrainedGenerationCount` | Transport drain/identity publication has not completed, including failures before that boundary. |
| `AwaitingFenceGenerationCount` | Transport drain finished, but a captured physical-peer/client-ID fence remains unacknowledged. |
| `PendingCorrectionFenceCount` | Currently published owed identities across retained generations; more may appear while transport drain is unfinished. |
| `CleanupFailedGenerationCount` | Unexpected cleanup failures; ordinary retriable fence failures do not count. |
| `BorrowedDedicatedConnectionCount`, `ConnectingDedicatedConnectionCount` | Active leases and unfinished acquisitions in operation pools attached to retained generations; excludes active generations and correction/control pools. |

Categories can overlap: a generation awaiting a fence can still own a borrowed blocking
operation. Zero fences alone does not prove accepted work finished or correction ordering
was established. Fully retired generations disappear from the next snapshot. Permanently
unavailable peers can retain generations indefinitely; age is diagnostic, never permission
to abandon an owed fence. The existing one-to-30-second fence retry backoff and explicit
client-disposal behavior are unchanged. Any future policy that abandons ordering guarantees
requires a separate explicit contract.

## Error ownership for contributors

The internal error metric foundation provides failure-only `ErrorObservation` leases.
These leases do not consume responses, own transport references, or wrap deferred results.
Command-family integration is tracked in
[#1023](https://github.com/thomhurst/Respire/issues/1023); the leases and the test-only
route-owner guard do not enable error metrics for every command path.

Pooled response conversion now counts caller-visible converter failures in
`redis.client.errors` after disposing any response that was not transferred to
the caller. This applies to both immediate replies and pending conversions.
Input failures remain the responsibility of the underlying send unless the
conversion receives its final owner's lease, which also preserves retry counts.
Exceptions and cancellation tokens retain their original identity and status.

Native raw, typed, string and byte response sources support explicit error
observation through response inspection, conversion, cleanup and caller reference
release. Immediately admitted ordinary standalone raw, typed, string and byte dispatch
returns the original native response source. Its inspection owns final publication, with
no extra dispatch source, ownership lock or continuation forwarding on successful commands.
This path excludes cache coordination, circuits, Sentinel, Cluster, replica reads,
streaming, scripts and activity tracing; raw operation-duration metrics also retain the
normal route. Capacity waits and retirement recovery retain the normal dispatch owner.
Other core raw sends and typed, string and byte dispatch establish a logical
response owner before disposal checks, route selection and cancellation handling.
Primary, replica and Cluster ready sends borrow that owner. Retired-connection
handoffs, capacity waits, MOVED/ASK retries and Sentinel replacement share its retry
history. The final error is published once after response inspection, conversion
and cleanup. Borrowed native inspection disables independent final publication.
This covers core dispatch boundaries; command-family argument construction remains
the responsibility of each family's entry point.
Successful inspection and conversion keep a default lease and rent no error
observation storage.

`ExecuteAsync` and `ExecuteFireAndForgetAsync` retain a caller response owner before
raw parsing, catalog validation, key-prefix rewriting and Cluster slot validation.
Raw blocking commands, including ASK redirects and the `ASKING` reply, borrow that
owner. SORT, multi-key list moves and list/sorted-set pops retain the same boundary
through construction and typed conversion. MGET, MSETNX and ZINTERCARD also cover
local multi-key validation. Cluster-wide sends share the caller's retry history
across targets and complete cache fences and discovery cleanup before publishing a
final failure. Successful calls rent no error observation storage.

`SendShutdownAsync` starts its caller boundary before option validation and the
`AllowAdmin` check, retaining ownership until its control socket is released. It
counts caller-visible preflight and write failures; completion still confirms only
the local write, and server-side errors remain outside this submission API.

When adding a core public method, update its source-adjacent
`<source-file>.cs.ownership.json` declaration in the same change. There is no shared
inventory file to update. The guard treats every public method on a public core
type as a route, including synchronous methods on new types. Explicit `NonRoutes`
entries exclude local value operations and helpers, with a reason for each source
signature. A new method fails until it is classified. The declarations pin
public source signatures, not the number or location of telemetry recorder calls.
`Surfaces` maps each declared signature to an executable final owner: `OwnerType`
uses the same signature on that implementation type, and `Overrides` names a
different complete source member when delegation changes the signature.
`AdditionalOwnerTypes` pins alternative implementations, such as batch and
transaction implementations of the same queue interface. Generic
arity uses a backtick followed by the parameter count. Parameter names and defaults
are omitted; parameter types, modifiers and return types are retained. Interface
declarations alone cannot be final owners, except executable default interface
methods. Same-signature implementation owners must have a public executable method
with the same static or instance kind, and list the contract in their source base
types (including inherited and partial declarations). A `new` hiding method owns the
contract only when its type re-lists that interface. A partial route's declaration
may sit beside either its defining or implementing source file. Explicit interface implementations retain their interface-qualified
source signature; they cannot masquerade as implicit public implementations.

Declare the owner before argument validation, disposal checks, cancellation checks,
command construction or setup parsing can fail. Explain the complete lifetime in
the surface's `Contract`. Forwarding overloads and facet interfaces retain the
logical caller's ownership when delegating to their declared implementation.
Native pooled inspection and typed converter inspection have separate boundaries;
keep conversion and response cleanup inside the caller's lifetime. Raw, catalog and
interpolated commands use their public caller boundary even when dispatch is shared.
Transfer the same final-owner lease to the appropriate final inspection boundary;
these declarations never authorize two final publications for one caller.

`Boundaries` declares non-public branches by complete source member, ownership role
and lifetime contract. Use `final` for a caller's final inspection, `helper` for
delegated work, `borrower` for shared attempts or retries, and `internal` for an
observation with no caller final failure. Helpers and borrowers name the enclosing
owner; they cannot declare themselves final owners. Internal observations have no
caller owner. Shared helpers may serve many enclosing public routes: the boundary's
owner anchor identifies the relevant dispatch lifetime, while each applicable
surface retains its distinct logical caller. Do not interpret an anchor as a
global lease shared by unrelated callers.

Keep explicit declarations for cache producers and each waiter, upload fills and
later download reads, fan-out targets, deferred execution and later pending-result
inspection, and cleanup. A shared producer cannot publish every waiter's final
failure. A returned stream's later read has its own caller boundary. Queue-time
validation, batch execution and pending inspection have distinct lifetimes.
Returned per-node failures must not also become duplicate parent failures.

Run `CommandRouteOwnershipTests` on net8.0 and net10.0. The guard scans the core
library source, including catalog dispatch, plus the distributed-cache implementation,
and validates each target framework
independently. A route's executable owner and inherited interface contract must
exist on the same target; a body in another framework branch cannot supply them.
Declarations for target-specific routes apply only where those routes are public.
New signatures, missing declarations and removed executable owners fail;
negative controls exercise those failures. The source-local declarations specify
required ownership. They do not prove that runtime instrumentation is present or
that a delegate passes its lease correctly. Route-family integration must also test final
publication, retries and original exception/cancellation behavior. Extension package
integration requires its own route declarations when its scope is added.

Keep this guard test-only. Do not add production reflection, a successful-path
observation rental, or a universal response wrapper to satisfy it. Preserve native
transport references, deferred result contracts and existing allocation tests.

Keep a default `ErrorObservation.FinalOwner` on a successful path. Call `StartFailure`
only after the first failure or handled retry, and complete the lease in `finally`.
There is one final owner per logical caller, even when independent callers receive the
same exception instance. `PublishFinal` records at most one final failure; repeated
inspection of that generation returns `false`. It observes the exception without consuming
or rethrowing it. Preserve the original exception with `throw;` and retain its
cancellation token.

Helpers borrow a separate lease with `Borrow`. Borrowers can create nested borrowers,
record handled retries with `RecordHandled`, and complete their own lease. They cannot
publish a final caller failure. Every handled retry increments the shared count once;
the final failure captures the total count. Exporters run outside the ownership gate,
so concurrent retry events may arrive out of order while retaining their exact counts.

Borrow before final publication or completion of that lease. `Borrow` on a closed lease
returns an empty borrower, whose `RecordHandled` returns `false`; it cannot recover the
completed operation's retry history. Core dispatch acquires a shared failure-only
lease at its first handled failure and retains it until final caller inspection.
Native response sources also retain a copied retry
count independently, so replies discarded after caller completion still record internal
errors without borrowing a closed or reused final owner.

Copying an owner or borrower value shares its existing completion right; it does not
create another reference. Repeated completion is harmless, including after the pooled
storage has been reused, so cleanup in `finally` cannot replace the original failure.
Storage returns to the bounded pool only after the owner and every distinct borrower
complete. Owner completion does not complete a still-live borrower. Final publication
closes retry reporting and new borrowing. `RecordHandled` and `PublishFinal` return
`false` for default, completed, closed, or stale leases without emitting a metric.
`Borrow` returns a default, inert borrower in those states. These checks apply in every
build, so a final-inspection race or stale asynchronous callback cannot replace the
original failure or affect a new caller's observation. Do not retain completed leases
for later asynchronous work. Storage returns outside the ownership gate after the last
completion. Retry counts saturate at `int.MaxValue`.

Failure-only owners and legacy route observations use the same retry and final-publication
bookkeeping. Their lifetime gates, generations, references and completion rights remain
separate. A handled event captures the count before advancing it; counts saturate at
`int.MaxValue`. Final publication freezes that logical observation's count and rejects
later handled events or count updates, including reentrant exporter callbacks. This does
not suppress late native replies: discarded replies retain their own copied attempt count
and publish internal errors independently after the caller observation has closed.

When adding a command path, declare its public boundary and delegated final owner in
the independent route inventory. Helper, borrower, transport, and cleanup observations
must remain distinct from the caller's final publication. Preserve checked native
response lifetimes and deferred-result inspection; do not introduce a universal wrapper.

Single-command string, hash, key, list, and sorted-set query facets start their caller
boundary before argument validation, serialization, key mapping, and command construction.
The same boundary covers typed reply conversion and command cleanup. Transparent
redirects and connection retries borrow that boundary: a recovered failure is internal,
and a failure returned to the caller is counted once with its complete retry count.
Preflight failures keep their original exception and cancellation behavior. Successful
calls keep an empty failure-only lease, including synchronous client-cache hits.

Lua script execution starts its caller boundary before script validation and command
construction. EVALSHA transport retries, redirects and NOSCRIPT fallback share that
boundary through typed conversion and cleanup. SCRIPT LOAD, EXISTS and FLUSH keep one
caller boundary across all Cluster primaries and join every target before publishing
one final failure. Successful script calls rent no error observation storage.

Collection scans (`HSCAN`, `SSCAN`, `ZSCAN`, including `HSCAN NOVALUES`) and standalone
`SCAN` count validation, cancellation and malformed pages once. Replica-policy pages
retain their server affinity and final owner through cursor and item parsing and reply
cleanup. Each page owns its retry history; cancellation between yielded items is a
separate enumeration failure. Ending an enumeration early does not count as an error.

Resumable Cluster pages and direct Valkey `CLUSTERSCAN` pages retain a failure-only
owner from checkpoint or argument validation through page construction, response
cleanup and discovery completion. Capability probes, unsupported-command fallbacks,
retired connections and redirects borrow that owner. Recovered failures are internal;
caller-visible failures count once with the full page retry count. Exception identity
and cancellation token/status are preserved. Successful pages do not rent error lease
storage from either observation pool.

Batch execution counts setup rejections before command ownership starts. Each queued
command then retains a failure-only owner through transparent retries, typed conversion,
and cleanup. Durability batches release their dedicated connection and client-cache fence
before publishing a final error, including WAIT/WAITAOF argument and reply failures.
Transactions retain shared retry history through commit preflight, EXEC replies and
connection cleanup. WATCH setup and hash-import session calls start ownership before
key routing, argument validation or command construction. Successful deferred execution
rents no error observation lease.

Deferred command failures are reported once after execution cleanup. Inspecting a pending
before execution, after a WATCH abort, or after its queue is discarded counts that lifecycle
failure once; repeated `Result` or awaiter inspection does not count it again. A later
execution failure remains a separate boundary from an earlier not-ready inspection.

## Sentinel primary changes

Sentinel batch, durability-batch, and transaction acquisition failures still emit an error
activity and `db.client.operation.duration`, including time spent discovering or connecting.
Those failure records omit `server.address` and `server.port` because no data connection was
acquired. Successful batch, durability, transaction, blocking, and script durations also
include discovery/acquisition time while retaining the selected primary's endpoint on the operation;
the Sentinel seed is never substituted as the executing Redis server. Blocking, transaction,
and correction-identity timeouts before data-peer selection likewise carry endpoint-less
connecting diagnostics; a selected physical connection retains its own diagnostic identity.

`ConnectionStateChanged` reports the retired endpoint and validated replacement for Sentinel
handoffs triggered by events, disconnects, or `READONLY` replies. Prefix views share these events.
Sentinel event monitors log failover hints for the configured service at Information and replica
events at Debug. A monitor that exhausts its `ReconnectPolicy` increments
`respire.connection.reconnect.exhausted` with `respire.reconnect.scope = sentinel-monitor` and logs
a warning. It logs at Information when a later primary publication resumes it. The `respire.sentinel.failover` counter
records primary endpoint changes with `server.address` and `server.port` tags. Initial
discovery and reconnection to the same endpoint do not increment it. Published failover
measurements remain queued even when disposal suppresses lifecycle callbacks. Lifecycle observers run
outside discovery and transport work; queued events are suppressed after client disposal.
The process-wide `respire.sentinel.guarded_logging.failures` counter records non-fatal logger
callback failures caught by the notification router's `SafeLog` and `LogSentinelEvent`
wrappers and the resolver's `LogOptionalDiscoveryFailure` wrapper for peer/configuration
failures, without endpoint or exception tags. Those wrappers also isolate non-fatal
metric-listener exceptions. Other Sentinel logs are outside the counter's
coverage; throwing loggers can still interrupt those paths. Inspect the logging provider
when this counter increases.
The process-wide `respire.sentinel.generations.retired` gauge reports retired generations
still owned while accepted work or correction fences drain. Continued growth warrants
investigation. A persistently nonzero value after normal commands and borrowed leases have
finished can indicate an unreachable correction peer or failed cleanup; inspect the warning
logs. Fence retries retain their one-to-30-second backoff, but warnings are limited to one
per retired generation every five minutes. An unexpected terminal cleanup failure logs that
the generation remains retained until disposal; the gauge deliberately continues counting
that ownership. Retention has no deadline that abandons an unacknowledged fence. Client disposal aborts
and joins retained connection work.
See [Sentinel connections](../fundamentals/connections.md#redis-sentinel) for drain and no-replay behavior.

## Maintenance notifications

With maintenance handling enabled, each valid notification produces a `redis.maintenance`
consumer activity and an event named after its wire kind (`MOVING`, `MIGRATING`, `MIGRATED`,
`FAILING_OVER`, `FAILED_OVER`, `SMIGRATING`, or `SMIGRATED`). Activities include the receiving
endpoint/database, `respire.maintenance.kind`, `respire.maintenance.sequence_id`, and any
announced seconds/target endpoint. They retain the receive timestamp and have no application
command parent. Information logs identify the kind, sequence, and receiving endpoint.

The `redis.client.maintenance.notifications` counter counts notifications delivered to diagnostics,
with endpoint and kind tags. Sequence IDs are deliberately absent from metric tags.
Diagnostics run serially on a thread-pool worker for each physical connection. Listener
exceptions are isolated; a slow listener cannot block RESP parsing or timeout handling.
Each queue retains at most 256 pending events, dropping the oldest on overflow and reporting
those drops through `respire.maintenance.notifications.dropped` when delivery resumes.
These diagnostics are best effort, may finish after connection disposal, and are not an
acknowledged event stream. Keep listeners short. Malformed notifications and historical
completion replays during negotiation do not produce maintenance diagnostics.

Cluster clients count `SMIGRATED` notifications that did not update slot ownership proactively
in `respire.cluster.slot_migrations.skipped`, tagged with `reason`: `queue_full` (the 128-item
topology queue dropped its oldest notification), `malformed` (an entry with an invalid slot list,
or entries past the 16384-slot enumeration budget), `duplicate` (a sequence ID already seen on
the same connection), `deferral_evicted` (an entry that waited for an earlier migration was
evicted to keep the waiting list bounded), or `deferral_expired` (an entry waited 30 seconds
without the earlier migration arriving). Queue drops also log a warning at most once every 30 seconds per client. In every
case, `MOVED` handling and topology discovery still correct the route.
