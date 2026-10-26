---
title: Hosted stream consumers
description: Process Redis stream entries with scoped handlers, bounded concurrency and explicit acknowledgements.
---

# Hosted stream consumers

`Respire.Streaming` runs Redis consumer-group readers as hosted services. Each delivery goes to a
scoped handler, which returns `Ack`, `Nack` or `DeadLetter`.

The optional [queue and stream dashboard](stream-dashboard.md) provides authorized inspection
of consumer groups, pending entries, and dead letters, with retry and delete actions.

```bash
dotnet add package Respire.Streaming
```

Register an `IRespireClient` with dependency injection (for example with
`Respire.DependencyInjection`). The worker uses that client's stream facet, including a key-prefixed
view, and never disposes it.

<!-- doc-test-tail-declaration: split-before=public sealed class OrderHandler -->
```csharp
using Microsoft.Extensions.Hosting;
using Respire;
using Respire.DependencyInjection;
using Respire.Streaming;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireStreamWorker<OrderHandler>("orders", "fulfillment", new()
{
    ConsumerCount = 4,
    BatchSize = 8,
    MinimumIdleTime = TimeSpan.FromMinutes(2),
    RecoveryPollInterval = TimeSpan.FromSeconds(5),
});
await builder.Build().RunAsync();

public sealed class OrderHandler : IRespireStreamHandler<RespireStreamEntry>
{
    public ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireStreamEntry entry, CancellationToken cancellationToken)
    {
        var orderId = entry.GetString("order-id");
        // Complete your idempotent application operation before returning Ack.
        return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
```

:::warning Handlers must be idempotent
Redis can deliver an entry more than once: after a lost acknowledgement, a recovery that overlaps a
slow handler (there is no lease extension), or a restart.
:::

- Each registration is a separate hosted service.
- Handlers are registered as scoped unless already registered. Each entry gets a new async DI scope,
  disposed after handling and acknowledgement. Avoid singleton handlers that depend on scoped
  services.

Both registration overloads require a nonblank `stream` containing well-formed UTF-16. Null values
throw `ArgumentNullException`, empty or whitespace-only values throw `ArgumentException`, and
unpaired surrogates throw `EncoderFallbackException`. Validation happens before adding services or
reserving the telemetry name, so rejected registrations leave the service collection unchanged.
Valid stream names are preserved exactly, including supplementary characters and a literal U+FFFD;
Unicode normalization is not applied, so composed and decomposed names remain distinct Redis keys.

### Consumer names

By default each registration picks a random consumer name at every start. Set `ConsumerName` to a
stable name per host slot so the next process resumes its own pending deliveries. Each reader
appends `-` and its zero-based index (`orders-host-1-0`, `orders-host-1-1`, ...).

Group names and non-null `ConsumerName` values must be nonblank, well-formed Unicode strings.
Registration rejects unpaired UTF-16 surrogates with `EncoderFallbackException` before adding
services, preventing distinct names from becoming the same Redis identity through UTF-8 replacement
fallback. Valid names are preserved exactly, including supplementary characters and a literal U+FFFD;
no Unicode normalization is applied. A null `ConsumerName` keeps the generated-name behavior.

- Active hosts and registrations sharing a group must use different names.
- Keep `ConsumerCount` the same across restarts. Entries owned by removed reader indexes are
  recovered after the visibility timeout.
- A stable name replays its own pending entries once at startup, before new entries.
- Random names accumulate consumer metadata across restarts. Remove unused consumers only after
  their pending entries are recovered.

## Options

| Option | Default | Purpose |
| --- | --- | --- |
| `ConsumerName` | random at each start | Stable consumer-name base for resuming own pending entries. |
| `ConsumerCount` | `1` | Readers, and therefore maximum concurrent handlers. |
| `BatchSize` | `1` | Entries fetched per read; processed sequentially by that reader. |
| `ReadWait` | 5 seconds | Blocking wait for new entries. |
| `MinimumIdleTime` | 1 minute | Visibility timeout before idle pending entries are recovered. |
| `RecoveryPollInterval` | 5 seconds | How often each reader looks for idle pending entries. |
| `CreateGroup` | `true` | Create the stream and group at startup. |
| `GroupStart` | `RespireStreamId.Beginning` | Position for a newly created group. |
| `DeleteAcknowledgedEntries` | `false` | Use `XACKDEL ACKED` on Redis 8.2+. |
| `DeadLetterStream` | `null` | Destination for dead-lettered entries. |
| `DeliveryLimit` | `null` | Maximum attempts before dead-lettering; requires `DeadLetterStream`. |
| `TelemetryName` | `"default"` | Metric and activity dimension; unique per service collection. |
| `MetricsPollInterval` | 30 seconds | Lag and pending gauge refresh interval. |
| `MetricsPollTimeout` | 5 seconds | Timeout for each gauge query. |
| `TraceParentField` | `"traceparent"` | Stream field holding the W3C parent; `null` disables extraction. |
| `TraceStateField` | `"tracestate"` | Stream field holding W3C vendor state; `null` ignores it. |

`MinimumIdleTime`, `RecoveryPollInterval`, `MetricsPollInterval` and `MetricsPollTimeout` must be
positive and at most `Int32.MaxValue` milliseconds.

## Typed payloads

Use the two-type overload with an explicit deserializer. It receives the entry's owned field bytes
and can run concurrently on different consumers. This works with custom codecs and
source-generated JSON, without reflection:

<!-- doc-test-tail-declaration: split-before=public sealed record Order -->
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Respire.Streaming;

var services = new ServiceCollection();
services.AddRespireStreamWorker<TypedOrderHandler, Order>("orders", "fulfillment",
    entry => JsonSerializer.Deserialize(entry["payload"], OrderJson.Default.Order)
        ?? throw new JsonException("Missing order payload."));

public sealed record Order(string Id);

[JsonSerializable(typeof(Order))]
internal partial class OrderJson : JsonSerializerContext { }

public sealed class TypedOrderHandler : IRespireStreamHandler<Order>
{
    public ValueTask<RespireStreamWorkerResult> HandleAsync(Order order, CancellationToken token)
        => ValueTask.FromResult(RespireStreamWorkerResult.Ack);
}
```

## Reads, completion and shutdown

**Concurrency.** Prefetched entries are bounded by `ConsumerCount` × `BatchSize`; there is no other
queue. `BatchSize` counts entries, not bytes, so enforce payload limits in producers if memory must
be bounded.

**Group creation.** Startup creates the stream and group at the beginning, keeping an existing
group's position. Set `GroupStart = RespireStreamId.New` to skip existing entries, or
`CreateGroup = false` to require an existing group.

**Results.** Return `Ack` only after successful processing. These leave the entry pending for retry
after the visibility timeout, while the worker keeps reading:

- `Nack`
- a handler or deserializer exception (logged as a warning with the exception type only, no message
  or payload)
- an unknown result
- handler cancellation

**Faults.** These stop the background service, which then follows your `HostOptions`
background-service failure policy:

- DI activation (including transient dependency failures, which are not retried) and scope disposal
- read, acknowledgement and dead-letter completion failures
- a lost acknowledgement reply (the result is uncertain, so the worker does not repeat it)

**Shutdown.** Readers stop at once and active handlers drain with their cancellation token.
Undispatched entries stay pending. When the host shutdown timeout expires, handlers are cancelled; a
handler that ignores cancellation keeps its scope until it finishes, and its result is not
acknowledged. Set the shutdown timeout to cover normal handler duration. Process termination can
still interrupt handling or acknowledgement.

## Pending messages and reliability

The worker recovers idle pending entries automatically: abandoned consumers, Nacks, handler failures
and deliveries interrupted by shutdown.

Set `MinimumIdleTime` above normal processing time, including time waiting behind earlier entries
in a batch: at least `BatchSize` × worst-case handler duration, plus acknowledgement time.

Recovery details:

- Each reader runs at most one recovery page per `RecoveryPollInterval`, of up to `BatchSize`
  entries. A full scan can take several intervals. Recovery adds no concurrency or prefetch.
- Each claim increments the Redis delivery count.
- Redis 8.4+: recovery uses `XREADGROUP CLAIM` and also removes pending IDs whose bodies were
  deleted. This cleanup needs `XRANGE` and `XACK` permission; without it, recovery uses
  `XAUTOCLAIM`.
- Redis 8.8+: a failed delivery whose `MinimumIdleTime` has elapsed is released with `XNACK FAIL`,
  keeping its delivery count. Otherwise, and on older servers, `Nack` leaves it for idle recovery.
- A stale handler cannot acknowledge or release a delivery that was recovered by another owner or
  attempt, even under the same consumer name. Return `Ack` to use this check; worker entries have
  no unconditional `entry.AckAsync()`.

Inspect pending entries with `client.Streams.PendingSummaryAsync` and `PendingAsync`. Without a
delivery limit, a persistent failure retries until it succeeds or an operator intervenes.

### Server requirements

| Feature | Requires |
| --- | --- |
| Idle recovery | Redis 6.2+ (or compatible) with `XAUTOCLAIM`, `EVAL`, `EVALSHA`, `XPENDING` and the usual consumer-group commands. Redis 7+ also removes deleted pending IDs while scanning. |
| Startup replay, acknowledgement | Lua, even with no idle entries |
| Dead-letter completion | Redis 7+ (or compatible) with Lua `redis.acl_check_cmd`, plus `XRANGE`, `TYPE`, `XADD`, `XACK` |
| `DeleteAcknowledgedEntries` | Redis 8.2+ (older servers use `XACK` and keep the body) |

- Feature detection uses `INFO SERVER` on each operation. If it is denied or the server is not
  Redis, the worker uses the compatible path; unsupported native commands also fall back.
- Other ACL failures, including denied `XNACK` or `XACKDEL`, and transport failures fault the worker.
- External tools must not reset delivery counts (`XCLAIM RETRYCOUNT`), rewind the group
  (`XGROUP SETID`) or recreate a live group.

`DeleteAcknowledgedEntries = true` uses `XACKDEL ACKED`, which deletes a body only after every group
has read and acknowledged it. By default, acknowledged entries are kept. Dead-letter completion
always keeps the source body. See Redis documentation for
[CLAIM](https://redis.io/docs/latest/commands/xreadgroup/),
[XNACK](https://redis.io/docs/latest/commands/xnack/) and
[XACKDEL](https://redis.io/docs/latest/commands/xackdel/).

## Delivery limits and dead-letter inspection

Set `DeadLetterStream` to a logical stream key, and optionally a positive `DeliveryLimit`.

- Source and destination must differ after the client's key prefix and must share a Cluster slot,
  even on standalone Redis. For example, use `"{orders}:events"` and `"{orders}:dead"`, or a view
  prefixed `"{orders}:tenant:"` with logical keys `"events"` and `"dead"`.
- Return `RespireStreamWorkerResult.DeadLetter` to dead-letter explicitly. It requires
  `DeadLetterStream` but not a delivery limit. Without a destination, it counts as a handler failure
  and the entry stays pending.

:::warning 1,024-field limit
With `DeadLetterStream` set, each source entry may have at most **1,024 field/value pairs**. Validate
this in producers. The worker faults on a larger entry before calling the handler and leaves it
pending; resolve it manually, because restarting alone does not help. Workers without
`DeadLetterStream` have no limit.
:::

**Delivery limit.** With a limit of three, the handler runs on attempts one to three. A failure on
the third attempt (Nack, exception or unknown result) is dead-lettered; an Ack completes normally.
Counts come from Redis and include startup replay, recovery and prefetched entries that never
started. If the final attempt is abandoned, the next recovery dead-letters without calling the
handler again, so the recorded count can exceed the limit.

**Dead-letter entries.** Inspect them with `client.Streams.ReadAsync` or `XRANGE`. Each has a new
stream ID, and its first five field/value pairs are:

| Field | Value |
| --- | --- |
| `_respire.source_id` | Original stream ID |
| `_respire.group` | Group name (at most 256 bytes) |
| `_respire.attempt` | Redis delivery count |
| `_respire.reason` | `explicit`, `nack`, `processing-failed` or `delivery-limit` (at most 64 bytes) |
| `_respire.exception_type` | Exception type for a processing failure, otherwise empty (at most 256 bytes) |

- The original fields follow unchanged and in order. If names collide with metadata, read
  positionally or use raw `XRANGE`.
- Exception messages, stack traces and failure text are never included.
- The destination is not trimmed; you own retention and requeueing. Requeue only after fixing the
  payload or handler.

**Atomicity.** Dead-lettering appends the record and acknowledges the source in one operation, after
checking `XADD` and `XACK` permissions and the destination type. Source entries are not deleted, so
other groups are unaffected. If the source body was already deleted or trimmed, the delivery is
acknowledged without a record. A rejected write leaves the source pending; a lost reply means both
mutations committed or neither did. There is no blind retry; restart the worker per your host
policy.

## Metrics and distributed tracing

Subscribe to the `Respire.Streaming` meter and activity source with your OpenTelemetry provider.
Install `OpenTelemetry.Extensions.Hosting` and an exporter in the host:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire.Streaming;

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(RespireStreamWorkerTelemetry.MeterName))
    .WithTracing(tracing => tracing.AddSource(RespireStreamWorkerTelemetry.ActivitySourceName));
```

Worker telemetry is independent of the core client's `RespireMetrics` setting.

**`TelemetryName`** should be a fixed, low-cardinality name per application role, such as
`"order-processing"`. It must match `[A-Za-z0-9._-]{1,128}` (case-sensitive) and be unique within
the service collection, including the default; duplicates fail at registration. Never put tenant,
message or consumer IDs in it. No metric includes keys, group or consumer names, message IDs,
attempts, exception text or payloads.

| Instrument | Kind / unit | Meaning |
| --- | --- | --- |
| `respire.stream.worker.processing.duration` | Histogram / `s` | Each delivery attempt, including deserialization, scope lifetime and fenced completion. |
| `respire.stream.worker.group.lag` | Observable gauge / `{message}` | Latest known undelivered group count from `XINFO GROUPS`. |
| `respire.stream.worker.group.pending` | Observable gauge / `{message}` | Latest group pending count from the same query. |
| `respire.stream.worker.dead_letters` | Counter / `{message}` | Confirmed atomic dead-letter appends. |

| Tag | Values |
| --- | --- |
| `respire.worker.name` | `TelemetryName` (all instruments) |
| `respire.worker.outcome` | Duration only: `ack`, `nack`, `dead-letter`, `deleted`, `stale`, `canceled`, `error` |
| `respire.worker.reason` | Dead-letter counter only: `explicit`, `nack`, `processing-failed`, `delivery-limit` |

- The dead-letter counter excludes deleted source bodies and lost replies, so it counts confirmed
  appends, not a durable audit total.
- Gauges poll `XINFO GROUPS` only while a listener exists, one query at a time, never overlapping.
  The response includes all groups on the stream. Failures, timeouts and missing groups clear the
  sample. Unknown lag (older servers) is never reported as zero.
- Stopping clears samples; disposal removes the meter. Listener exceptions do not affect message
  handling.

### Trace context

The worker reads the `traceparent` and optional `tracestate` fields before deserialization.
Rename them with `TraceParentField` and `TraceStateField` (they must differ), or set either to `null`
to disable it. Producers must write each field at most once. The parent must be
[W3C version-00 trace context](https://www.w3.org/TR/trace-context/):

```text
traceparent = 00-<32 lowercase hex trace ID>-<16 lowercase hex span ID>-<2 lowercase hex flags>
tracestate  = vendor=value,other=value
```

- `traceparent`: exactly 55 printable ASCII bytes; nonzero trace and span IDs. Invalid, oversized or
  duplicate parents start a new root.
- `tracestate`: at most 512 ASCII bytes and 32 members with distinct keys. Spaces and tabs around
  members are allowed; control characters inside keys or values are not. Invalid state is
  discarded, keeping a valid parent.
- No baggage or payload fields are copied. Treat trace state as untrusted input.

A producer can write the current W3C context with the payload:

```csharp
using System.Diagnostics;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("localhost:6379");
using var producerSource = new ActivitySource("Orders");
using var publish = producerSource.StartActivity("publish order", ActivityKind.Producer);
var parent = publish ?? Activity.Current;
var fields = new List<(string Field, RespireValue Value)> { ("payload", orderJson) };
if (parent is { IdFormat: ActivityIdFormat.W3C, Id: { } traceparent })
{
    fields.Add(("traceparent", traceparent));
    if (!string.IsNullOrEmpty(parent.TraceStateString))
        fields.Add(("tracestate", parent.TraceStateString));
}
await client.Streams.AddAsync("{orders}:events", fields.ToArray());
```

Subscribe your tracing provider to `"Orders"` too. The worker creates a sampled `Consumer` activity
named `"stream process"` for every attempt, including replay, retries and delivery-limit completion.
Retries are siblings under the producer parent. Missing or invalid context starts a new root rather
than inheriting a host activity.

## Producer retries

[Idempotent stream appends](command-retries.md#idempotent-stream-appends) attach stable identities
to eligible retry-enabled typed `XADD` calls on Redis 8.6+. Deduplication lasts only within the
stream's retention limits. It does not prevent consumer redelivery, so handlers must still be
idempotent.

## Test without Redis

Use `RespireFakeServer` from `Respire.Testing` and connect with its `CreateOptions()` to test
handlers without Docker. It supports group creation, blocking reads, `XAUTOCLAIM`, `XDEL`, the
worker's built-in scripts, pending inspection and group metadata, plus fault injection before and
after execution. It does not run arbitrary Lua or enforce ACLs; test those against real Redis.

| Constructor argument | Models |
| --- | --- |
| (default) | Redis 7.0 consumer registration |
| `createConsumersOnEmptyReads: true` (with `clock: null`) | Redis 7.2+ registering consumers on empty reads |
| `autoClaimDeletesPendingEntries: false` | Redis 6.2 returning null for deleted pending IDs |
| `streamWorkerVersion` | Worker scripts for a chosen Redis version |
