---
title: Pub/sub
description: Consume Redis channels as async streams.
---

# Pub/sub

Respire exposes subscriptions as `IAsyncEnumerable<RespireMessage>`. Leave the loop and dispose the subscription to clean up. For typed keyspace, keyevent, and Redis 8.8 subkey events, see [Keyspace notifications](keyspace-notifications.md).

## Subscribe

`SubscribeAsync` returns after the server acknowledges SUBSCRIBE, so the subscription is live before the first publish.

Each subscription kind has two overloads: a single `RespireChannel` or a
`ReadOnlySpan<RespireChannel>`, followed by optional `options` and
`cancellationToken` arguments. Strings convert implicitly to channels. Pass
`cancellationToken:` by name when omitting options; pass multiple channels in a
collection expression or a `RespireChannel[]` (string arrays do not convert).

```csharp
await using var configured = await redis.SubscribeAsync(
    "orders", options: new RespireSubscriptionOptions(BufferSize: 32));
```

```csharp
await using var subscription =
    await redis.SubscribeAsync(["orders", "payments"], cancellationToken: stoppingToken);

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

Pattern and Redis 7 sharded subscriptions have their own entry points:

```csharp
await using var patterns = await redis.SubscribePatternAsync("events:*");
await using var shard = await redis.SubscribeShardedAsync("events:eu-west");
```

- Buffering starts at acknowledgement. Capacity and overflow policy apply before enumeration starts.
- A subscription allows one active enumerator. Dispose it before starting another.
- `Kind` and immutable `Targets` describe what it covers. `IsDisposed` reports whether it has ended.
- Await `Completion` to tell apart explicit disposal, disposal of the owning client, and `ReconnectExhausted` (a configured reconnect attempt limit ended it).

## Publish

```csharp
long receivers = await redis.PublishAsync("orders", orderJson);
long shardReceivers = await redis.PublishShardedAsync("events:eu-west", payload);
```

## Binary channels and explicit subscription kinds

`RespireChannel` holds the exact channel bytes. Strings convert implicitly after UTF-16 validation. Byte arrays and `ReadOnlyMemory<byte>` convert implicitly by copying, so later changes to the source buffer do not affect the channel. Reuse a constructed channel to avoid a copy per publish.

```csharp
RespireChannel binary = new byte[] { 0xff, 0x00, 0x3a, 0x41 };
await using var subscription = await redis.SubscribeAsync(binary, cancellationToken: stoppingToken);
await redis.PublishAsync(binary, "payload", stoppingToken);

var pattern = RespireChannel.Pattern(new byte[] { 0xff, 0x00, 0x3a, (byte)'*' });
await using var patterns = await redis.SubscribeAsync(pattern, cancellationToken: stoppingToken);
var shard = RespireChannel.Sharded("events:{eu-west}");
await using var sharded = await redis.SubscribeAsync(shard, cancellationToken: stoppingToken);
await redis.PublishAsync(shard, "payload", stoppingToken); // SPUBLISH

RespireChannel[] targets = ["orders", binary];
await using var multiple = await redis.SubscribeAsync(targets, cancellationToken: stoppingToken);
```

- `SubscribeAsync(RespireChannel)` uses `Kind` to choose SUBSCRIBE, PSUBSCRIBE, or SSUBSCRIBE.
- A multi-target subscription needs one kind. Use separate subscriptions for mixed kinds.
- `SubscribePatternAsync` and `SubscribeShardedAsync` also accept binary targets.
- Patterns cannot be published.
- Equality and hashing compare bytes only, ignoring kind. Equivalent text and UTF-8 targets deduplicate within a subscription.
- `ClusterSlot` uses raw bytes and Redis hash-tag rules.
- Empty channels, including `default(RespireChannel)`, are valid. Arbitrary binary is accepted.
- Unpaired UTF-16 surrogates throw `ArgumentException` before any subscription work.
- Names such as `__keyspace@0__:key` are ordinary channels; no notification routing is inferred.

`RespireMessage.Channel`, nullable `Pattern`, and `Targets` are `RespireChannel` values. Use `.Bytes` for exact identity and `.ToString()` for UTF-8 display. Display replaces invalid UTF-8, so distinct channels can look identical; use `Convert.ToHexString(channel.Bytes.Span)` for exact diagnostics. Do not use channel bytes as telemetry tags.

## Explicit pub/sub prefixes

Key prefixes do not change channels. Add a channel namespace separately:

```csharp
IRespireClient tenant = redis.WithKeyPrefix("keys:42:").WithPubSubPrefix("events:42:");
await using var subscription = await tenant.SubscribeAsync("orders", cancellationToken: stoppingToken);
await tenant.PublishAsync("orders", orderJson, stoppingToken); // events:42:orders
RespireChannel physical = ((IRespireNameResolver)tenant).ResolveChannel("orders");
```

`WithPubSubPrefix(string)` validates UTF-16 and encodes UTF-8. The `RespireKey` overload accepts any bytes, including NUL and invalid UTF-8, and snapshots them:

```csharp
RespireKey prefix = new byte[] { 0xff, 0x00, (byte)':' };
IRespireClient binaryTenant = redis.WithPubSubPrefix(prefix);
```

Views:

- Both overloads reject empty prefixes. Nested views append prefixes in order.
- Key-prefix, read-routing, and cache-bypass views keep the pub/sub prefix.
- Views share the root's connections and subscriptions. Disposing a view leaves the root and other subscriptions usable.
- Byte-identical physical routes share one server subscription until the last consumer disposes. Reconnect restores them and emits the usual gap markers.

Prefixing rules:

- Literal and sharded channels get the exact prefix bytes.
- For patterns, `*`, `?`, `[`, `]`, and `\` in the prefix are escaped so the prefix stays literal. Prefix `tenant*:` with pattern `order*` subscribes to `tenant\*:order*`.
- `Channel`, `Pattern`, and `Targets` keep the physical names, including prefix and escaping. Nothing is stripped. Gap markers still have an empty channel and null pattern.
- Only typed subscribe and publish methods apply the prefix. Raw, catalog, and interpolated commands and the [Server PUBSUB queries](pub-sub-introspection.md) use physical names.
- `ResolveChannel` returns the physical literal, pattern, or sharded target. Pass it to an unprefixed client or a Server query. Passing it to a prefixed client prefixes it twice.
- Prefixes do not change key arguments. Backplanes use a prefix only when the client you give them opts in.
- [Notification descriptors](keyspace-notifications.md) keep their server-owned channel names. Build them from physical keys with `ResolveKey`, and filter and strip the key namespace yourself when parsing. An ordinary channel that looks like a notification name still gets the prefix.

Set a default root namespace with `RespireOptions.PubSubPrefix` (default empty, independent of `KeyPrefix`):

| Source | Form |
| --- | --- |
| Connection string (URI or comma-delimited) | `pubSubPrefix=events%3A`. Percent-decoded once; last entry wins; empty clears it. |
| Dependency injection | `PubSubPrefix` on the builder. |
| Aspire | `Aspire:Respire:Options:PubSubPrefix`, or per connection `Aspire:Respire:<connectionName>:Options:PubSubPrefix`. |
| Structured options / configuration callbacks | Any value, including binary bytes; callbacks can replace or clear it. |

Redis pub/sub is shared across logical databases. A channel prefix is a naming convention, not a security boundary; use Redis ACLs for access control.

## Sharded subscriptions in Redis Cluster

With `UseCluster = true`, `SubscribeShardedAsync` groups channels by the primary that owns their slot:

- One dedicated subscription connection per primary. Channels on different slots can share one subscription.
- Duplicate consumers share one server-side subscription until the last disposes.
- `SPUBLISH` uses the command connection for the channel's slot.
- Key prefixes never affect channels. A pub/sub prefix applies before slot selection, so hash tags in the full physical channel decide SSUBSCRIBE and SPUBLISH routing, including after resharding.

On `MOVED`/`ASK`, unsolicited `SUNSUBSCRIBE` during resharding, or a topology refresh, Respire routes to the current owner. Socket failures restore only the affected channels. The subscription and its buffer survive. A reconnect gap marker comes before messages from the new owner; messages lost during the move cannot be replayed.

Recovery:

- Sharded subscriptions share one recovery episode and attempt budget, separate from regular and pattern subscriptions.
- Every affected primary stays unhealthy until the episode ends. Success clears them all; exhaustion marks them disconnected.
- New sharded subscriptions fail while an episode is active, even for healthy primaries, with or without a `ReconnectPolicy`. Subscribe again after recovery. Existing subscriptions keep their buffers and routes.
- With a null policy, recovery retries forever: an immediate attempt, then exponential waits from 250 ms, capped at five seconds. New subscriptions keep failing promptly during a long outage. Set `ReconnectPolicy.MaxAttempts` to bound recovery.
- Exhaustion completes all sharded subscriptions with `ReconnectExhausted`. Regular subscriptions keep working. Recreate the client to create sharded subscriptions again.

Keyspace notifications in Cluster use their own connections and recovery; see [Keyspace notifications](keyspace-notifications.md#redis-cluster).

## Read message data

`RespireMessage` exposes text, bytes, channel, and pattern. Deserialize with the client's configured serializer:

```csharp
OrderCreated order = message.As<OrderCreated>();
```

Channel, pattern, and payload bytes stay valid after enumeration advances. `.Bytes` does not allocate; text conversion can. Do not mutate the read-only storage through unsafe APIs.

## Reconnection and pressure

Subscriptions resubscribe after reconnection. `RespireOptions.ReconnectPolicy` can bound and delay this:

- The budget resets only after all live routes are acknowledged.
- Exhaustion ends live subscriptions; recreate the client to subscribe again.
- New subscriptions during configured recovery fail instead of skipping its delay.
- With no policy (default `null`): an immediate attempt, then exponential waits from 250 ms, capped at five seconds.

See [connection recovery](reconnect-policy.md#pubsub-reconnection-and-resubscription) for attempts, cancellation, events, and telemetry.

The subscription buffer is bounded:

| Option | Default | Meaning |
| --- | --- | --- |
| `RespireOptions.SubscriptionBufferSize` | 1,024 | Messages buffered per subscription. |
| `RespireOptions.SubscriptionOverflow` | `DropOldest` | When a consumer falls behind, drop the oldest buffered message (`DropOldest`) or the newest incoming one (`DropNewest`). |
| `RespireSubscriptionOptions.MaxPayloadBytes` | `null` (unlimited) | Larger payloads are discarded before queuing and produce a `PayloadTooLarge` gap. |

Override per subscription with `RespireSubscriptionOptions`; a `null` argument inherits the client setting:

```csharp
var options = new RespireSubscriptionOptions(
    BufferSize: 128,
    Overflow: SubscriptionOverflow.DropNewest)
{
    MaxPayloadBytes = 16 * 1024,
};
await using var telemetry = await redis.SubscribeAsync(
    "telemetry", options, stoppingToken);
```

- There is no blocking or throwing policy: it would stall the shared reader and affect other subscriptions.
- `MaxPayloadBytes` limits retained payloads only; the transport still parses frames with its normal buffers. Each subscription on a route has its own limit.
- `DroppedMessages` counts discards for the subscription, including oversized payloads. The `respire.pubsub.messages.dropped` counter reports the same.

Redis does not keep messages for disconnected subscribers. Use streams when you need delivery tracking or replay.

## Detecting delivery gaps

Streams contain `RespireMessageKind.Gap` items as well as messages. Check `Kind` before reading a payload: a gap has empty channel and payload, and `As<T>()` throws.

Replace the gap log with your state reload and the message log with your handler:

```csharp
await using var subscription = await redis.SubscribeAsync("orders", cancellationToken: stoppingToken);
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

`message.Gap` reports:

| Property | Meaning |
| --- | --- |
| `Reason` | `Reconnect`, `BufferOverflow`, `PayloadTooLarge`, or a combination. |
| `StartedAt` | When the client saw the connection fail (can be later than the real interruption). |
| `EndedAt` | The target's resubscription acknowledgement. |
| `Duration` | Length of the interval. |
| `DroppedMessages` | Known local discards. Messages lost while disconnected are not counted. |

Marker placement:

- Each affected target gets a reconnect gap before any message received after its acknowledgement, even in the same socket read. Multi-target subscriptions can report several gaps.
- Messages buffered before the reconnect stay before the marker.
- Overflow markers sit at the loss point: before retained data for `DropOldest`, after buffered data for `DropNewest`.
- Adjacent markers merge their reasons, intervals, and counts. Markers use no data capacity and are never dropped; at most one pending marker is stored beyond the message capacity.

`subscription.DeliveryGap` and the `respire.pubsub.delivery.gaps` counter fire for every interruption or discard, even without enumeration, so they can exceed the number of stream markers. Counter tags are `respire.subscription.kind` and `respire.subscription.gap.reason`; channel names and payloads are not tags. Handlers run synchronously on the receive path: keep them short, hand off work, and never block on Redis or subscription calls. Handler exceptions are logged and delivery continues.

When reloading, use versions or idempotent updates to reconcile buffered messages with the fresh snapshot. Use Streams when you need replay or acknowledged delivery.
