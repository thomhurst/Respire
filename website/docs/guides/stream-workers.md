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

Return `Ack` only after successful processing. `Nack`, a serializer or handler exception,
an unknown result, or handler cancellation leaves the entry pending. The worker continues
reading new entries and retries pending entries after the visibility timeout. A warning
reports the exception type for handler/serializer failures
without including exception messages or payloads. DI activation, scope disposal, read and
acknowledgement failures fault the background service and follow the application's `HostOptions`
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

Each existing reader runs at most one `XAUTOCLAIM` page per polling interval and fetches
at most `BatchSize` recovered entries with their fields. Delivery counts increase on each
claim. The scan examines at most ten times `BatchSize` pending IDs per page. Its cursor
survives empty batches and deleted pending IDs and resets only at the end of the scan.
A full scan can therefore take several polling intervals. Recovery does not create another
queue or increase handler concurrency or the number of prefetched entries. The blocking
new-entry wait is capped by the next recovery poll; recovery pauses while that reader handles
its current batch. `Nack` leaves the entry pending rather than making it immediately claimable.

A configured stable
`ConsumerName` replays that consumer's own pending IDs once at startup, in bounded pages,
before reading new entries. A Nack during this replay remains pending and is not retried in
a hot loop; normal idle recovery retries it later. Random identities recover previous
processes' deliveries after the visibility timeout but accumulate consumer metadata across
restarts. Remove unused consumers only after their pending deliveries have been recovered.

Inspect pending entries with `client.Streams.PendingSummaryAsync` and `PendingAsync`.
There is no delivery limit or dead-letter stream yet. A persistent failure is retried until
the application succeeds or an operator intervenes.

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

The remaining reliable worker features are tracked independently:
[delivery limits and atomic dead-letter completion (#1230)](https://github.com/thomhurst/Respire/issues/1230),
[capability-aware CLAIM/XNACK/XACKDEL (#1231)](https://github.com/thomhurst/Respire/issues/1231),
[worker metrics and tracing (#1232)](https://github.com/thomhurst/Respire/issues/1232), and
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
Redis remain necessary.
