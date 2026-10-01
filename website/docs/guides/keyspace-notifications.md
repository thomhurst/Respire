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
Delivered message `Channel` and `Pattern` values contain wire names, without the subscription
descriptor's routing/database metadata. This remains consistent when ordinary names and
notification descriptors share a route, regardless of registration order. Read the emitting
database from the parsed notification's `Database` property; retain the original descriptor
when its routing intent is needed.

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
or inconsistent frames return false. Gap markers and empty event names also return false.
Unknown nonempty event names remain available losslessly through `RawType`.

Redis does not emit item-channel notifications for keys containing newline, so
`SubKeySpaceItem` rejects such keys. Other layouts support them. Event names containing `|`
cannot use `SubKeySpaceEvent`. Older Redis versions can acknowledge reserved channel names
but do not generate subkey notifications; subscription success is not feature detection.
See the [Redis subkey format specification](https://redis.io/docs/latest/develop/pubsub/subkeyspace-notifications/).

## Delivery, pressure, and Redis Cluster

Subscription return is an acknowledgement barrier. Enumeration uses the existing bounded
Pub/Sub buffer and `RespireSubscriptionOptions`; drops update `DroppedMessages`, delivery-gap
markers, and `respire.pubsub.messages.dropped`. Cancellation/disposal and automatic
resubscription use the same lifecycle as ordinary subscriptions. See [Pub/sub](pub-sub.md).

Redis notifications have at-most-once delivery. Reconnect cannot replay missed events, and
expiry events report actual deletion rather than an exact TTL deadline. Use a durable log
when replay is required. [Redis delivery semantics](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/)

Redis Cluster ordinary `SUBSCRIBE` and `PSUBSCRIBE` channels are cluster-wide through the
cluster bus. Keyspace and subkey notifications are node-specific. Respire therefore keeps
ordinary application Pub/Sub on its existing single logical subscription and uses dedicated
Pub/Sub connections for notification descriptors:

- Exact-key descriptors subscribe only on the current primary that owns the key's slot.
- Prefix, pattern, keyevent, and subkeyevent descriptors subscribe on every current primary.
- Connections are shared by notification routes that use the same primary. One logical
  subscription does not unsubscribe another route's channel.
- `SubscribeAsync` returns only after every required primary acknowledges its route. If
  activation fails or is cancelled, Respire removes its routes and closes connections whose
  server-side subscription state is uncertain.
- A primary added by topology discovery is acknowledged before an old primary's route is
  removed. Exact-key subscriptions move when slot ownership changes.
- A failure on one primary reconnects that primary's notification connection. Delivery from
  healthy primaries continues. `ConnectionStateChanged` reports endpoint-specific reconnect
  state; `Connected` for an endpoint follows acknowledgement of its current notification routes.
- `ReconnectPolicy.MaxAttempts` bounds both per-primary reconnects and the attempts to subscribe
  a primary that topology discovery adds. Each failing primary gets the full attempt budget. When
  the limit is reached, affected subscriptions complete with `ReconnectExhausted`. A primary that
  cannot be reached stays unavailable to new notification subscriptions until it leaves the
  discovered topology or the client is recreated. A subscription rejected by a reachable primary
  (for example `NOPERM`), or one that fails on a connection other subscriptions still use, ends
  only that subscription.

Every primary must have the needed `notify-keyspace-events` flags configured by the deployment.
Respire does not read or change this setting. Redis can acknowledge a subscription while emitting
no events when notification flags are disabled.

Each primary preserves its own message order. A merged subscription has no total order across
primaries. Redis Pub/Sub is at-most-once: messages emitted during a disconnect are lost. Slot
moves, promotions, or topology changes can also expose a short gap or duplicate event because
Redis provides no event IDs or replay. The existing bounded buffer, overflow policy,
`DroppedMessages`, and `respire.pubsub.messages.dropped` metric apply to the merged stream.

Notifications are application events, separate from Respire's
[`CLIENT TRACKING` response cache](../fundamentals/client-side-caching.md). They do not
replace that cache's invalidation protocol.
