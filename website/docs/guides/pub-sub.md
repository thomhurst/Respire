---
title: Pub/sub
description: Consume Redis channels as async streams.
---

# Pub/sub

Respire models subscriptions as `IAsyncEnumerable<RespireMessage>`. Leaving the loop and disposing the subscription performs cleanup—no delegate bookkeeping required.

## Subscribe

```csharp
await using var subscription = redis.Subscribe("orders", "payments");

await foreach (RespireMessage message in
    subscription.WithCancellation(stoppingToken))
{
    Console.WriteLine($"{message.Channel}: {message.Text}");
}
```

Pattern and Redis 7 sharded subscriptions use dedicated entry points:

```csharp
await using var patterns = redis.SubscribePattern("events:*");
await using var shard = redis.SubscribeSharded("events:eu-west");
```

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
