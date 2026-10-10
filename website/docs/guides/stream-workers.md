---
title: Hosted stream consumers
description: Process Redis stream entries with scoped handlers, bounded concurrency and explicit acknowledgements.
---

# Hosted stream consumers

Install `Respire.Streaming` and register an `IRespireClient` with dependency injection.
The worker uses the existing client's stream facet, including a key-prefixed view. The
worker does not own or dispose that client. `Respire.DependencyInjection` can register
the client for you.

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

Each registration creates a separate hosted service with unique consumer names by default.
Set `ConsumerName` to a stable name for a particular host slot when its next process should
resume its own pending deliveries. Each reader appends its zero-based index to that name.
Active hosts and registrations sharing a group must use different names; keep the same
consumer count across restarts to retain all reader identities. Reducing `ConsumerCount`
leaves pending entries owned by the removed reader indexes; idle recovery claims those entries
after the visibility timeout.
Handlers are registered as scoped services unless already registered. A new asynchronous
DI scope is created for each entry and disposed after handling and acknowledgement finish.
Avoid registering a handler as a singleton when it depends on scoped services.

## Typed payloads

Use the two-type overload with an explicit deserializer. The deserializer receives the
entry's owned field bytes and may run concurrently on different consumers. This supports
custom codecs and source-generated JSON without enabling reflection serialization:

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

`ConsumerCount` bounds simultaneous handlers and readers. Each reader fetches at most
`BatchSize` entries and processes that batch sequentially, so the number of prefetched
entries is bounded by their product. There is no additional message or task queue.
Batch size limits entry count rather than payload bytes; enforce producer payload limits
when memory must be bounded independently of message size.

By default startup creates the stream and group at the beginning. Only `BUSYGROUP` is
tolerated; an existing group's position is retained. Set `GroupStart = RespireStreamId.New`
to start a new group after existing entries, or `CreateGroup = false` to require an existing
group. `ReadWait` is a positive blocking wait, defaulting to five seconds. Blocking reads
use Respire's dedicated blocking connection path.

Return `Ack` only after successful processing. By default `Nack`, a serializer or handler exception,
an unknown result, or handler cancellation leaves the entry pending. The worker continues
reading new entries and retries pending entries after the visibility timeout. A warning
reports the exception type for handler/serializer failures
without including exception messages or payloads. DI activation, scope disposal, read and
acknowledgement or dead-letter completion failures fault the background service and follow the application's `HostOptions`
background-service failure policy. Handler activation failures, including transient dependency
construction failures, stop the worker rather than retry activation. The first consumer's
infrastructure failure reaches the host even when a sibling handler ignores cancellation;
that sibling keeps its scope until it completes. Expected shutdown cancellation does not
produce the handler-failure warning.

On graceful shutdown, readers stop immediately and active handlers keep their cancellation
token while draining. Undispatched entries from a fetched batch remain pending. When the
host's shutdown token expires, active handlers are canceled and the stop wait ends. A handler
that ignores cancellation keeps its scope until it actually completes; its result is not
acknowledged after cancellation. Set the host's shutdown timeout to cover normal handler
duration and ensure handlers cooperate with cancellation. Process termination can still
interrupt handling or acknowledgement.

## Pending messages and reliability

The worker automatically recovers idle pending entries, including abandoned consumers,
Nacks, handler failures and deliveries interrupted by shutdown. `MinimumIdleTime` is the
visibility timeout, defaulting to one minute. `RecoveryPollInterval` defaults to five seconds.
Both settings must be positive and at most `Int32.MaxValue` milliseconds; fractional
milliseconds round up on the server. Set the visibility timeout above normal processing
time, including time spent waiting behind earlier entries in a prefetched batch. For sequential
handlers, budget at least `BatchSize` times the worst-case handler duration, plus acknowledgement
and dispatch time. There is
no lease extension: a long-running handler can overlap a recovery attempt. Handlers must
be idempotent and consumer identities must be unique among active hosts and registrations.

Each existing reader runs at most one recovery page per polling interval and fetches
at most `BatchSize` recovered entries with their fields. Delivery counts increase on each
claim. On compatible servers, `XAUTOCLAIM` examines at most ten times `BatchSize` pending
IDs per page. Its cursor
survives empty batches and deleted pending IDs and resets only at the end of the scan.
A full scan can therefore take several polling intervals. Recovery does not create another
queue or increase handler concurrency or the number of prefetched entries. The blocking
new-entry wait is capped by the next recovery poll; recovery pauses while that reader handles
its current batch. On Redis 8.4 or later, recovery uses `XREADGROUP CLAIM` and may fill a
page with new entries after eligible pending entries. Its delivery-count metadata
describes previous attempts; the worker adds this delivery before applying `DeliveryLimit`.
Because `CLAIM` can omit deleted bodies, each native recovery poll also inspects at most
`BatchSize` pending IDs with `XPENDING` and `XRANGE`, acknowledging only IDs whose bodies
are gone. This cleanup needs `XRANGE` and `XACK` permission. Recovery checks both permissions
before claiming and uses `XAUTOCLAIM` instead when either is denied. Its independent cursor advances
past live entries and resets at the end of the PEL, so deleted IDs cannot remain hidden
behind live entries. The scan and cleanup run atomically with the recovery read.

On Redis 8.8 or later, unsuccessful processing can release the fenced delivery with `XNACK FAIL`,
preserving its delivery count. Since Redis makes released entries immediately claimable,
the worker releases only when that delivery's `MinimumIdleTime` has already elapsed.
Otherwise it leaves the entry pending for ordinary idle recovery and immediately frees
the reader to process more work. Ownership and attempt are checked atomically before release,
so a recovered delivery cannot be released by its stale handler. On older servers, `Nack`
always leaves the entry pending for ordinary idle recovery.

A configured stable
`ConsumerName` replays that consumer's own pending IDs once at startup, in bounded pages,
before reading new entries. A Nack during this replay follows the same visibility timeout
and is not retried in
a hot loop; normal idle recovery retries it later. Random identities recover previous
processes' deliveries after the visibility timeout but accumulate consumer metadata across
restarts. Remove unused consumers only after their pending deliveries have been recovered.

Inspect pending entries with `client.Streams.PendingSummaryAsync` and `PendingAsync`.
Without a delivery limit, a persistent failure is retried until the application succeeds or an operator intervenes.

## Delivery limits and dead-letter inspection

Set `DeadLetterStream` to a logical stream key and optionally set a positive `DeliveryLimit`.
The source and destination must be distinct after applying the registered client's key prefix,
and must share a Redis Cluster slot even on standalone Redis. For example, use source
`"{orders}:events"` and destination `"{orders}:dead"`, or register a view with prefix
`"{orders}:tenant:"` and use logical keys `"events"` and `"dead"`. Keys are validated before
group creation. The prefix is applied once by each command, including atomic completion.

Enabling `DeadLetterStream` limits each source entry to **1,024 field/value pairs**, excluding
the five metadata pairs added during completion. Validate this limit in producers before
appending entries. The worker rejects a larger delivery before invoking its handler or
serializer, faults with an explicit size-limit error, and leaves the source pending without
writing a dead-letter record. Lua completion also checks the limit before expanding arguments
or mutating either stream. Resolve an oversized entry manually before restarting the worker;
restarting alone does not make it supported. Workers without `DeadLetterStream` have no such limit.

Return `RespireStreamWorkerResult.DeadLetter` to complete a delivery explicitly. This requires
`DeadLetterStream` but does not require a delivery limit. Returning `DeadLetter` without configuring a destination is
treated as a handler failure: the worker logs the exception type, leaves the delivery pending
for retry, and continues processing other entries.

A limit of three permits processing on attempts one, two and three. A Nack, handler exception,
serializer exception or unknown result on the third attempt is dead-lettered.
An Ack still completes normally at that limit.
Counts come from Redis: the initial delivery counts as one, and startup replay and each
idle recovery increment the count. Prefetched entries also count as deliveries even if
shutdown prevents their handlers from starting. If the final permitted attempt is abandoned,
the next replay or recovery increments the count and dead-letters without invoking another
handler. Thus the recorded count can exceed the limit, while handler invocations remain bounded.
Handler cancellation during shutdown leaves the entry pending for this later recovery.

Inspect the destination with `client.Streams.ReadAsync` or Redis `XRANGE`. Each dead-letter
entry has its own newly generated stream ID. Its first five field/value pairs contain:

- `_respire.source_id`: the original stream ID.
- `_respire.group`: the group name, bounded to 256 bytes.
- `_respire.attempt`: the fenced Redis delivery count.
- `_respire.reason`: `explicit`, `nack`, `processing-failed` or `delivery-limit` (at most 64 bytes).
- `_respire.exception_type`: the exception type for a processing failure, otherwise empty (at most 256 bytes).

The original field/value pairs follow these metadata pairs in their original order, without
changing any bytes. Original fields may use the same names as metadata; use positional fields
or raw `XRANGE` when names collide. Exception messages, stack traces and arbitrary failure
text are never included by default. The destination is not trimmed automatically; operators
own retention, inspection and requeue policy. Requeue only after fixing the poison payload or handler.

One Lua operation checks the current pending owner and attempt, reads the original fields,
appends the dead-letter entry and acknowledges the source delivery. A stale processor cannot
complete a newer attempt, including another attempt under the same consumer name. Source
entries are not deleted, so other groups keep their independent deliveries. Scripts isolate
operations but do not roll back earlier writes after an error. If external deletion or trimming
has already removed the source body, completion acknowledges only the fenced pending delivery
without creating a dead-letter record; stale owners and attempts still cannot acknowledge it.
For entries with a body, the worker verifies destination type and both `XADD` and `XACK`
ACL permissions before any mutation; all source
and group checks also precede the append. A rejected write or unexecuted operation leaves
the source pending. A lost reply or cancellation after execution remains uncertain: either
both mutations committed or neither did. There is no fallback acknowledgement or blind
completion retry. Restart the failed worker according to the application's host policy;
an already acknowledged delivery cannot produce another dead-letter record from the same attempt.

Dead-letter completion requires Redis 7 or a compatible server with Lua `redis.acl_check_cmd`,
plus `XRANGE`, `TYPE`, `XADD`, `XACK` and the worker's other commands available to its ACL user.
The fake server recognizes the built-in completion operation; use real Redis controls to
verify ACL behavior. The fake supports fault injection before and after execution to test
transport failures, write rejection, cancellation and lost replies.

[Redis consumer groups retain deliveries until acknowledgement](https://redis.io/docs/latest/commands/xreadgroup/).
An application operation can finish before its acknowledgement is lost, so handlers must
be idempotent. Replay and recovery capture delivery counts inside the same atomic Lua
operation that delivers the entries; new-entry deliveries carry their initial attempt of one.
Acknowledgement atomically checks both the pending owner and that attempt before `XACK`.
A stale handler cannot acknowledge a different owner's delivery or a later attempt under
the same consumer name. Return `Ack` from the handler to use this fence. Worker entries do
not expose an unconditional `entry.AckAsync()` completion path.

Cancellation and transport failures do not trigger fallback acknowledgement. If an
acknowledgement reply is lost, its result is uncertain: an executed acknowledgement may
already have removed the pending entry, while an unexecuted one leaves it recoverable.
The worker faults on that infrastructure failure and does not blindly repeat completion.
Configure the host failure policy and restart strategy for that case.

Idle recovery requires Redis 6.2 or a compatible server with `XAUTOCLAIM`, `EVAL`, `EVALSHA`,
`XPENDING` and the usual consumer-group commands available to its ACL user. Redis 7 or later
also removes deleted pending IDs while scanning. Startup replay and acknowledgement need
Lua even when no idle entries exist. External tools must not reset delivery counters with
`XCLAIM RETRYCOUNT`, rewind the group with `XGROUP SETID`, recreate a live group, or otherwise
reuse an attempt token. Consumer-name uniqueness remains required even with attempt fencing.

Capability discovery runs inside each atomic operation on its serving server. It uses
`INFO SERVER`, without caching across reconnects, redirects, or mixed-version deployments.
Denied or unavailable discovery and non-Redis servers retain the compatible path. Unsupported
native commands also fall back. Denied native cleanup permissions use compatible recovery;
other ACL failures, including denied `XNACK` or `XACKDEL`, fault the worker. Transport and
other operational failures also fault the worker instead of repeating a possibly executed write.

Set `DeleteAcknowledgedEntries = true` to use fenced `XACKDEL ACKED` on Redis 8.2 or later.
`ACKED` deletes a body only after every existing group has read and acknowledged it,
preserving unread entries and entries pending in other groups. Older servers use `XACK`
and retain the body. By default, acknowledgements retain entries on every version.
Atomic dead-letter completion always uses `XACK` and retains the source body.
See Redis documentation for [CLAIM](https://redis.io/docs/latest/commands/xreadgroup/),
[XNACK](https://redis.io/docs/latest/commands/xnack/), and
[XACKDEL](https://redis.io/docs/latest/commands/xackdel/).

## Metrics and distributed tracing

Subscribe to the `Respire.Streaming` meter and activity source using your application's
OpenTelemetry provider. Install `OpenTelemetry.Extensions.Hosting` and your chosen
exporter in the host application:

```csharp
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire.Streaming;

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics.AddMeter(RespireStreamWorkerTelemetry.MeterName))
    .WithTracing(tracing => tracing.AddSource(RespireStreamWorkerTelemetry.ActivitySourceName));
```

The package creates instruments and activities, not a provider or exporter. Worker telemetry
is independent of the core client's `RespireMetrics` selection. Configure a fixed,
low-cardinality `TelemetryName` for each application role, such as `"order-processing"`.
The default is `"default"`; names are limited to 128 characters. Registrations with the
same name intentionally share metric dimensions. Do not put tenant, message or consumer
identifiers in this name. No metric includes stream keys, group names, consumer names,
message IDs, attempt numbers, exception text or payload fields.

| Instrument | Kind / unit | Meaning |
| --- | --- | --- |
| `respire.stream.worker.processing.duration` | Histogram / `s` | Each delivery attempt, including deserialization, scope lifetime and fenced completion. |
| `respire.stream.worker.group.lag` | Observable gauge / `{message}` | Latest known undelivered group count from `XINFO GROUPS`. |
| `respire.stream.worker.group.pending` | Observable gauge / `{message}` | Latest group pending count from the same query. |
| `respire.stream.worker.dead_letters` | Counter / `{message}` | Confirmed atomic dead-letter appends. |

Every measurement has only `respire.worker.name`, plus a bounded
`respire.worker.outcome` on duration or `respire.worker.reason` on the dead-letter counter.
Outcomes are `ack`, `nack`, `dead-letter`, `deleted`, `stale`, `canceled` or `error`.
Reasons are `explicit`, `nack`, `processing-failed` or `delivery-limit`.
Deleted source bodies are acknowledged without increasing the dead-letter counter.
Lost replies are uncertain and are not counted, even when Redis committed the append.
The counter therefore measures confirmed appends rather than a durable audit total.

Each registration runs one serial polling loop, independent of handler count. It queries
only when either gauge has a listener. `MetricsPollInterval` defaults to 30 seconds;
`MetricsPollTimeout` defaults to five seconds. Both must be at least one millisecond and at most
`Int32.MaxValue` milliseconds. A new interval starts after the previous query completes,
so requests never overlap or retry in a hot loop. `XINFO GROUPS` returns all groups on the
source stream, so response size depends on the number of groups; only the registered
group's snapshot is retained. Poll failures, missing groups and timeouts clear the sample.
Older Redis servers, or Redis groups with indeterminate lag, have no lag measurement;
unknown lag is never reported as zero. Gauges are snapshots, not exact per-message events.
Stopping cancels polling and clears samples immediately. Disposal removes the worker's
meter and rejects late query results and new measurements from handlers still draining.
Listener callbacks already running may finish after disposal. Metric callbacks do not
hold the group snapshot lock, so shutdown can cancel readers and honor its deadline
while a callback is still running.
Telemetry listener exceptions do not change message acknowledgement, recovery, handler
failure policy or transport exceptions.

By default the worker extracts `traceparent` and optional `tracestate` stream fields before
deserialization and handler invocation. Configure `TraceParentField` and `TraceStateField`
to use other names; set `TraceParentField = null` to disable extraction or
`TraceStateField = null` to ignore vendor state. Names must be distinct when both are set.
The producer must supply each field at most once. The supported parent format is the
[W3C version-00 trace context](https://www.w3.org/TR/trace-context/):

```text
traceparent = 00-<32 lowercase hex trace ID>-<16 lowercase hex span ID>-<2 lowercase hex flags>
tracestate  = vendor=value,other=value
```

Trace and span IDs must be nonzero. Parent bytes must be exactly 55 printable ASCII bytes.
Vendor state is optional, at most 512 printable ASCII bytes and 32 distinct W3C members.
Invalid, oversized or duplicate parent fields start a new root; invalid vendor state is
discarded without discarding a valid parent or rejecting the message. No baggage or
arbitrary payload fields are copied into activities. Treat propagated trace state as
untrusted input and apply application privacy policy when choosing vendor state.

For example, a producer can write the currently sampled W3C context with the payload:

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

Subscribe your tracing provider to `"Orders"` as well to create the producer activity.
Without a producer activity or ambient context the fields are omitted. The worker creates
a `Consumer` activity named `"stream process"` for every attempt when sampled, including
startup replay, idle retries and delivery-limit completion. Retries are sibling activities
with the original producer parent and distinct span IDs. Handler child activities inherit
that attempt. Missing or invalid context creates a root rather than inheriting a host
startup activity. Each attempt disposes its activity and restores the prior ambient
activity, including when a listener throws. An uncooperative handler retains its activity
until that attempt actually completes, just as it retains its handler scope.

The remaining reliable worker features are tracked independently:
[producer retry deduplication (#892)](https://github.com/thomhurst/Respire/issues/892).
The full feature remains open in [#891](https://github.com/thomhurst/Respire/issues/891).

Use `RespireFakeServer` from `Respire.Testing` with its `CreateOptions()` client to test handlers
without Docker. It supports this worker's group creation, blocking reads, `XAUTOCLAIM`,
`XDEL`, the exact built-in atomic worker scripts, pending inspection and group metadata.
It does not interpret arbitrary Lua. Default fake consumer registration matches Redis 7.0.
Use `new RespireFakeServer(clock: null, createConsumersOnEmptyReads: true)` to model Redis 7.2
or later registering consumers on empty new-entry reads. Pass `autoClaimDeletesPendingEntries: false`
to the three-argument constructor to model Redis 6.2 returning null claimed entries for deleted
pending IDs. The worker skips those entries and retains the scan cursor. Compatibility tests against real
Redis remain necessary. The four-argument constructor's `streamWorkerVersion` models
these built-in worker scripts for a selected Redis version; other command support is unchanged.
