---
title: Pub/sub
description: Consume Redis channels as async streams.
---

# Pub/sub

Respire models subscriptions as `IAsyncEnumerable<RespireMessage>`. Leaving the loop and disposing the subscription performs cleanup—no delegate bookkeeping required.

## Subscribe

`SubscribeAsync` returns once the server has acknowledged the SUBSCRIBE, so the subscription is live before the first message is published—no polling on `PublishAsync`'s receiver count.

```csharp
await using var subscription =
    await redis.SubscribeAsync(["orders", "payments"], stoppingToken);

await foreach (RespireMessage message in
    subscription.WithCancellation(stoppingToken))
{
    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

Pattern and Redis 7 sharded subscriptions use dedicated entry points:

```csharp
await using var patterns = await redis.SubscribePatternAsync("events:*");
await using var shard = await redis.SubscribeShardedAsync("events:eu-west");
```

Messages are buffered from the moment the subscription is acknowledged, so nothing is lost between `SubscribeAsync` returning and the `await foreach` starting.

A subscription is a single-consumer stream: only one enumerator may be active at a time. Dispose
it before starting another. `Kind` and immutable `Targets` describe what the subscription covers,
while `IsDisposed` reports whether it has ended. Await `Completion` to distinguish explicit
disposal from disposal of the owning client.

## Publish

```csharp
long receivers = await redis.PublishAsync("orders", orderJson);
long shardReceivers = await redis.PublishShardedAsync("events:eu-west", payload);
```

## Read message data

`RespireMessage` exposes text, bytes, channel and pattern metadata. Deserialize application messages with the client's configured serializer:

```csharp
OrderCreated order = message.As<OrderCreated>();
```

## Reconnection and pressure

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
