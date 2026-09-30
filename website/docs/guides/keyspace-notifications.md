---
title: Keyspace notifications
description: Parse binary-safe key, event, and Redis 8.8 subkey notifications.
---

# Keyspace notifications

Notification factories describe physical Redis keys and choose `SUBSCRIBE` or `PSUBSCRIBE`.
`RespireMessage.TryParseKeyNotification` exposes the database, event, key, and subkeys as
owned memory slices. Parsing and struct enumeration do not copy message bytes. Unrecognized
event names have `Type == Unknown` and remain available through `RawType`.

## Configure the server explicitly

Notifications start disabled. Configure the deployment or use admin-gated
`redis.Server.SetConfigAsync("notify-keyspace-events", "KEA")`. Subscription never sends
`CONFIG SET`. These settings affect the entire server and add processing overhead; managed
services may restrict them.

```shell
redis-cli CONFIG SET notify-keyspace-events KEA
# Redis 8.8: add the subkey channel flags and hash event class.
redis-cli CONFIG SET notify-keyspace-events KEASTIVh
```

Choose `K`/`E` for keyspace/keyevent channels, or `S`/`T`/`I`/`V` for subkey channels, plus
needed event classes. `A` does not include miss, new-key, overwrite, or type-change classes;
add `m`, `n`, `o`, or `c` explicitly. See Redis's [configuration reference](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/#configuration).

## Subscribe and parse

```csharp
await using var redis = await RespireClient.ConnectAsync("redis://localhost");
await using var subscription = await redis.SubscribeAsync(
    RespireChannel.KeySpacePrefix("tenant:42:", database: 0));

await foreach (var message in subscription)
{
    if (message.Kind == RespireMessageKind.Gap)
    {
        // Reload application state from its authoritative source.
        continue;
    }
    if (!message.TryParseKeyNotification("tenant:42:"u8, out var notification)) continue;
    Console.WriteLine($"{notification.Database}: {notification.Type} {notification.Key}");
}
```

The factory's prefix is physical; the parser's explicit prefix filters and strips that prefix.
`WithKeyPrefix` does not alter notification descriptors or ordinary Pub/Sub channels.
`Key`/`KeyBytes` expose the stripped logical key; `Channel` and `RawValue` retain the original
physical message. `KeyStartsWith` and `TryCopyKey` operate on the exposed key.

| Factory | Match | Command |
| --- | --- | --- |
| `KeySpaceSingleKey(key, database)` | Every event for one physical key | `SUBSCRIBE` |
| `KeySpacePattern(pattern, database)` | Redis glob against physical keys | `PSUBSCRIBE` |
| `KeySpacePrefix(prefix, database)` | Literal prefix, with glob characters escaped | `PSUBSCRIBE` |
| `KeyEvent(type, database)` | Keys affected by one event | `SUBSCRIBE`, or `PSUBSCRIBE` for all databases |

An explicit database targets that database regardless of the client's selected database.
A nullable database omitted from a pattern/event factory matches every database. Exact-key
factories require an explicit database. Raw event-name overloads support future/module events.
`Unknown` is not a factory event name; supply the raw name instead.

Notification descriptors cannot be published or converted to a different subscription kind.
Construct `new RespireChannel(descriptor.Bytes)` to deliberately use equivalent bytes as an
ordinary application channel. Reserved-looking names alone never change routing semantics.
Channel equality remains byte-based; it does not compare kind or notification metadata.

## Redis 8.8 subkeys

The subkey factories are `SubKeySpaceSingleKey`, `SubKeySpacePattern`, `SubKeySpacePrefix`,
`SubKeyEvent`, `SubKeySpaceItem`, and `SubKeySpaceEvent`. They retain the same physical-key
and database rules. Redis 8.8 currently emits these events for hash fields.

```csharp
await using var subscription = await redis.SubscribeAsync(
    RespireChannel.SubKeySpaceSingleKey("profile:42", database: 0));
await foreach (var message in subscription)
{
    if (!message.TryParseKeyNotification(out var notification)) continue;
    foreach (var field in notification.GetSubKeys())
    {
        // field is ReadOnlyMemory<byte>; decoding is optional and may lose binary identity.
        Console.WriteLine($"{notification.Type}: field has {field.Length} bytes");
    }
}
```

`GetSubKeys()` offers `Count`, `FirstOrDefault`, `CopyTo`, and an explicitly allocating
`ToArray`. Empty field names count as subkeys. Delimiter bytes, NUL, and invalid UTF-8 survive
parsing. Decimal length prefixes are validated before exposing slices; truncated, overflowing,
or inconsistent frames return false. Gap markers also return false.

Redis does not emit item-channel notifications for keys containing newline, so
`SubKeySpaceItem` rejects such keys. Other layouts support them. Event names containing `|`
cannot use `SubKeySpaceEvent`. Older Redis versions can acknowledge reserved channel names
but do not generate subkey notifications; subscription success is not feature detection.
See the [Redis subkey format specification](https://redis.io/docs/latest/develop/pubsub/subkeyspace-notifications/).

## Delivery, pressure, and Cluster

Subscription return is an acknowledgement barrier. Enumeration uses the existing bounded
Pub/Sub buffer and `RespireSubscriptionOptions`; drops update `DroppedMessages`, delivery-gap
markers, and `respire.pubsub.messages.dropped`. Cancellation/disposal and automatic
resubscription use the same lifecycle as ordinary subscriptions. See [Pub/sub](pub-sub.md).

Redis notifications have at-most-once delivery. Reconnect cannot replay missed events, and
expiry events report actual deletion rather than an exact TTL deadline. Use a durable log
when replay is required. [Redis delivery semantics](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/)

Cluster notification delivery remains tracked by [#298](https://github.com/thomhurst/Respire/issues/298).
Descriptors retain `RoutingScope`, `RoutingSlot`, and `NotificationDatabase` for that layer.
Currently Cluster subscriptions using a notification descriptor fail before network I/O;
nonzero notification databases are invalid. This prevents silently subscribing to only one
arbitrary primary. Every primary needs its own notification configuration and coverage.
Ordinary application Pub/Sub remains unchanged.

Notifications are application events, separate from Respire's
[`CLIENT TRACKING` response cache](../fundamentals/client-side-caching.md). They do not
replace that cache's invalidation protocol.
