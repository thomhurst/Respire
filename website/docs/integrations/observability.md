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

Operation latency uses the stable `db.client.operation.duration` histogram and records seconds, as required by the semantic convention.

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
acquired. Successful acquisition retains the selected primary's endpoint on the operation;
the Sentinel seed is never substituted as the executing Redis server.

`ConnectionStateChanged` reports the retired endpoint and validated replacement for reactive
Sentinel handoffs. Prefix views share these events. The `respire.sentinel.failover` counter
records primary endpoint changes with `server.address` and `server.port` tags. Initial
discovery and reconnection to the same endpoint do not increment it. Published failover
measurements remain queued even when disposal suppresses lifecycle callbacks. Lifecycle observers run
outside discovery and transport work; queued events are suppressed after client disposal.
The process-wide `respire.sentinel.generations.retired` gauge reports retired generations
still owned while accepted work or correction fences drain. Continued growth warrants
investigation. A persistently nonzero value after normal commands and borrowed leases have
finished can indicate an unreachable correction peer or failed cleanup; inspect the warning
logs. Retention has no deadline that abandons an unacknowledged fence. Client disposal aborts
and joins retained connection work.
See [Sentinel connections](../fundamentals/connections.md#redis-sentinel) for drain and no-replay behavior.
