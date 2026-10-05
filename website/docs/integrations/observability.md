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
| `Resiliency` | `resiliency` | Maintenance notifications and geographic failovers |
| `ConnectionBasic` | `connection-basic` | Reserved for connection lifecycle measurements in [#928](https://github.com/thomhurst/Respire/issues/928) |
| `ConnectionAdvanced` | `connection-advanced` | Reserved for detailed connection measurements in #928 |
| `Command` | `command` | Logical operation duration, including Redis commands used for pub/sub and streams |
| `ClientSideCaching` | `client-side-caching` | Cache requests and evictions |
| `PubSub` | `pubsub` | Reserved for pub/sub processing measurements in #928 |
| `Streaming` | `streaming` | Reserved for stream processing measurements in #928 |

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
`OTHER`. Empty, non-ASCII, or names longer than 80 characters also use `OTHER`. The limit
includes compound labels and does not reset when configuration changes. Other standard
attributes still identify configured endpoints, database numbers, batch sizes, and errors.
Metric selection and the label limit do not alter trace names or existing span attributes.

Without a listener, or with the command group disabled or the command excluded, metric
instrumentation avoids timestamps, metric tag formatting, and compound name construction.
Tracing can independently require its own timestamps and span attributes. This does not
make streamed or blocking operations allocation-free; their transport contracts still apply.

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
| `db.client.operation.duration` | Unchanged | `s` | Existing logical operation latency and database attributes |

The four standardized counters carry `redis.client.library=Respire:<version>` and
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
connection, error, pub/sub, streaming, and dashboard coverage is tracked
by [#866](https://github.com/thomhurst/Respire/issues/866).

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
