---
title: Pub/sub
description: Consume Redis channels as async streams.
---

# Pub/sub

Respire models subscriptions as `IAsyncEnumerable<RespireMessage>`. Leaving the loop and disposing the subscription performs cleanup—no delegate bookkeeping required.

For typed keyspace, keyevent, and Redis 8.8 subkey events, see [Keyspace notifications](keyspace-notifications.md).

## Subscribe

`SubscribeAsync` returns once the server has acknowledged the SUBSCRIBE, so the subscription is live before the first message is published—no polling on `PublishAsync`'s receiver count.

```csharp
await using var subscription =
    await redis.SubscribeAsync(["orders", "payments"], stoppingToken);

await foreach (RespireMessage message in
    subscription.WithCancellation(stoppingToken))
{
    if (message.Kind == RespireMessageKind.Gap)
    {
        Console.Error.WriteLine($"Delivery gap: {message.Gap}. Reload authoritative state before applying more messages.");
        continue;
    }
    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

Pattern and Redis 7 sharded subscriptions use dedicated entry points:

```csharp
await using var patterns = await redis.SubscribePatternAsync("events:*");
await using var shard = await redis.SubscribeShardedAsync("events:eu-west");
```

Messages are buffered from the moment the subscription is acknowledged. The configured capacity and overflow policy apply even before enumeration starts.

A subscription is a single-consumer stream: only one enumerator may be active at a time. Dispose
it before starting another. `Kind` and immutable `Targets` describe what the subscription covers,
while `IsDisposed` reports whether it has ended. Await `Completion` to distinguish explicit
disposal, disposal of the owning client, and `ReconnectExhausted` when a configured
reconnect attempt limit ends the subscription.

## Publish

```csharp
long receivers = await redis.PublishAsync("orders", orderJson);
long shardReceivers = await redis.PublishShardedAsync("events:eu-west", payload);
```

## Binary channels and explicit subscription kinds

`RespireChannel` owns the exact channel bytes. Strings convert implicitly after UTF-16 validation;
byte arrays and `ReadOnlyMemory<byte>` convert implicitly by copying their contents. Mutating the
original buffer after construction, subscription, or publication cannot change a channel's identity.
Reuse a constructed channel to avoid copying it for each publication.

```csharp
RespireChannel binary = new byte[] { 0xff, 0x00, 0x3a, 0x41 };
await using var subscription = await redis.SubscribeAsync(binary, stoppingToken);
await redis.PublishAsync(binary, "payload", stoppingToken);

var pattern = RespireChannel.Pattern(new byte[] { 0xff, 0x00, 0x3a, (byte)'*' });
await using var patterns = await redis.SubscribeAsync(pattern, stoppingToken);
var shard = RespireChannel.Sharded("events:{eu-west}");
await using var sharded = await redis.SubscribeAsync(shard, stoppingToken);
await redis.PublishAsync(shard, "payload", stoppingToken); // SPUBLISH

RespireChannel[] targets = ["orders", binary];
await using var multiple = await redis.SubscribeAsync(targets, stoppingToken);
```

The metadata-aware `SubscribeAsync` uses `Kind` to select SUBSCRIBE, PSUBSCRIBE, or SSUBSCRIBE.
Multi-target subscriptions require one kind; use separate subscriptions for mixed kinds. Named
`SubscribePatternAsync` and `SubscribeShardedAsync` also accept binary targets and explicitly select
their command family. Patterns cannot be published. Existing string subscription and publication
overloads remain available, including sharded subscriptions across Redis Cluster primaries.

Equality and hashing compare only bytes, independently of kind. Equivalent text and UTF-8 byte
targets deduplicate within a subscription. `ClusterSlot` uses raw bytes and Redis hash-tag rules.
Empty channels, including `default(RespireChannel)`, are valid. Unpaired UTF-16 surrogates throw
`ArgumentException` before subscription work begins; arbitrary binary values are accepted.
Names such as `__keyspace@0__:key` remain ordinary channels, with no inferred notification routing.

**Pre-release API change:** `RespireMessage.Channel`, nullable `Pattern`, and subscription `Targets`
now contain `RespireChannel` values. Use `.Bytes` for lossless identity and `.ToString()` for UTF-8
display. Display replaces invalid UTF-8 and can make distinct channels look identical. For exact
diagnostics use `Convert.ToHexString(channel.Bytes.Span)`; channel bytes are not telemetry tags.

## Sharded subscriptions in Redis Cluster

With `UseCluster = true`, `SubscribeShardedAsync` groups channels by their hash-slot owner and
uses one dedicated subscription connection per primary. Channels on different slots can share
one subscription; channels on the same primary share its connection. Duplicate consumers share
one server-side subscription until the last consumer disposes. `SPUBLISH` uses the command
connection for the channel's slot. Channel names are never affected by a client's key prefix.

`MOVED` replies, unsolicited `SUNSUBSCRIBE` frames during resharding, and refreshed topology
all trigger routing to the current owner. Socket failures restore the affected channels without
resubscribing healthy primaries. The existing subscription and its buffer survive these changes.
A reconnect gap marker precedes messages from the replacement subscription; Redis pub/sub
cannot replay messages lost while a channel changes owners.

Sharded Cluster subscriptions share one recovery episode and configured attempt budget,
separate from regular channel and pattern subscriptions. Exhausting that budget completes all
sharded subscriptions with `ReconnectExhausted`; regular subscriptions remain usable. Recreate
the client to create sharded subscriptions after exhaustion. Notifications across Cluster
primaries are a separate feature and remain unsupported.

## Read message data

`RespireMessage` exposes text, bytes, channel and pattern metadata. Deserialize application messages with the client's configured serializer:

```csharp
OrderCreated order = message.As<OrderCreated>();
```

Channel, pattern, and payload bytes remain valid after enumeration advances. Exact-channel
messages share immutable registered channel storage; pattern messages own a copy of the concrete
channel name from the incoming frame. Accessing `.Bytes` does not allocate. Text conversion is
explicit and can allocate; do not mutate the exposed read-only storage through unsafe APIs.

## Reconnection and pressure

`RespireOptions.ReconnectPolicy` can bound and delay automatic reconnection and
resubscription. Its budget resets only after all live routes are acknowledged. Exhaustion
ends live subscriptions; recreate the client to subscribe again. New subscriptions during
configured recovery fail rather than bypassing its delay. Null preserves the existing
immediate attempt followed by 250 ms exponential waits capped at five seconds. See
[connection recovery](reconnect-policy.md#pubsub-reconnection-and-resubscription) for
attempt semantics, cancellation, lifecycle events, and telemetry.

Subscriptions resubscribe after reconnection. The subscription buffer is bounded; configure
`SubscriptionOverflow` in `RespireOptions` to drop either the oldest buffered message or the
newest incoming message when a consumer falls behind. Override those defaults for one
subscription with `RespireSubscriptionOptions`:

```csharp
var options = new RespireSubscriptionOptions(
    BufferSize: 128,
    Overflow: SubscriptionOverflow.DropNewest);
await using var telemetry = await redis.SubscribeAsync(
    "telemetry", options, stoppingToken);
```

Blocking and throwing policies are intentionally unavailable because they would stop the shared
pub/sub reader and affect unrelated subscriptions. `DroppedMessages` reports the number discarded
for that subscription, and the `respire.pubsub.messages.dropped` counter exposes the same event to
metrics collectors.

Pub/sub is transient: Redis does not retain messages for disconnected subscribers. Use streams when delivery tracking and replay matter.

## Detecting delivery gaps

**Pre-release behavior change:** subscription streams now include `RespireMessageKind.Gap` items.
Check `Kind` before reading or deserializing a published payload. A gap has empty channel/payload
fields, and `As<T>()` throws because there is no published value.

The following example logs each item. Replace the gap log with your application's
state reload, and replace the message log with its normal update handler.

```csharp
await using var subscription = await redis.SubscribeAsync("orders", stoppingToken);
await foreach (var message in subscription.WithCancellation(stoppingToken))
{
    switch (message.Kind)
    {
        case RespireMessageKind.Gap:
            Console.Error.WriteLine($"Delivery gap: {message.Gap}. Reload authoritative state before applying more messages.");
            break;
        case RespireMessageKind.Message:
            Console.WriteLine($"{message.Channel}: {message.Text}");
            break;
        default:
            throw new InvalidOperationException($"Unknown subscription item: {message.Kind}");
    }
}
```

`message.Gap` reports `Reason` (`Reconnect`, `BufferOverflow`, or both), `StartedAt`, `EndedAt`,
`Duration`, and the known local `DroppedMessages` count. Connection loss counts are unknown.
The reconnect interval begins when the client observes a failed connection, which can be later
than the actual interruption, and ends at the target's resubscription acknowledgement.

Each affected target produces a reconnect gap before any messages received after its acknowledgement,
including when both arrive in one socket read. Multi-target subscriptions can report more than one
gap as targets resume. Messages already buffered before reconnect remain before the marker.
Overflow markers sit at the loss position: before retained data for `DropOldest`, after previously
buffered data for `DropNewest`. Adjacent markers coalesce by combining reasons, observed intervals,
and discard counts. Markers do not consume data capacity and cannot themselves be dropped; their
storage remains bounded by the configured message capacity plus one pending marker.

`subscription.DeliveryGap` and the `respire.pubsub.delivery.gaps` counter report each detected target
interruption or local discard, even without enumeration. Their count can exceed the number of
coalesced stream markers. Counter tags are `respire.subscription.kind` and
`respire.subscription.gap.reason`; channel names and payloads are not tags. Event handlers run
synchronously on the receive path: keep them short, signal background work, and never block on
Redis or subscription operations. Handler exceptions are logged without stopping message delivery.

Reloading is application-specific: use versions or idempotent updates when reconciling buffered
messages with a fresh snapshot. Redis pub/sub cannot replay lost messages; use Streams when replay
or acknowledged delivery is required.
