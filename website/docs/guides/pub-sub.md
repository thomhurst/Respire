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

The synchronous `Subscribe`, `SubscribePattern` and `SubscribeSharded` overloads return the same stream but defer the SUBSCRIBE until enumeration starts, so a publish that races the first `await foreach` can miss the subscriber. Prefer the async variants unless the subscription is created far from where it is consumed.

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

Subscriptions resubscribe after reconnection. The subscription buffer is bounded; configure `SubscriptionOverflow` in `RespireOptions` to drop either the oldest buffered message or the newest incoming message when a consumer falls behind.

Pub/sub is transient: Redis does not retain messages for disconnected subscribers. Use streams when delivery tracking and replay matter.
