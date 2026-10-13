---
title: Keyspace notifications
description: Parse binary-safe key, event, and Redis 8.8 subkey notifications.
---

# Keyspace notifications

Subscribe to Redis keyspace, keyevent, and Redis 8.8 subkey notifications, and parse them without copying. Use them for application events about key changes. Delivery is at most once.

## Configure the server explicitly

Notifications are off by default, and subscribing never sends `CONFIG SET`. Configure the deployment, or use admin-gated `redis.Server.SetConfigAsync("notify-keyspace-events", "KEA")`. The setting is server-wide, adds overhead, and managed services may restrict it.

```shell
redis-cli CONFIG SET notify-keyspace-events KEA
# Redis 8.8: add the subkey channel flags and hash event class.
redis-cli CONFIG SET notify-keyspace-events KEASTIVh
```

- `K`/`E` enable keyspace/keyevent channels; `S`/`T`/`I`/`V` enable subkey channels. Add the event classes you need.
- `A` excludes the miss, new-key, overwrite, and type-change classes; add `m`, `n`, `o`, or `c` explicitly.

See Redis's [configuration reference](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/#configuration).

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

`TryParseKeyNotification` exposes the database, event, key, and subkeys as slices of the message; parsing and struct enumeration do not copy. Unrecognized events have `Type == Unknown`, with the name in `RawType`.

Prefixes:

- Factory prefixes and keys are physical. `WithKeyPrefix` and `WithPubSubPrefix` do not change notification descriptors.
- The parser's prefix argument filters and strips that prefix. With configured or nested key prefixes, pass the full physical prefix, not just the last part. For text prefixes, get it from the view with `Encoding.UTF8.GetBytes(view.ResolveKey(RespireKey.Empty).ToString())`, and resolve descriptor keys through the same view.
- `Key`/`KeyBytes` are the stripped key; `KeyStartsWith` and `TryCopyKey` work on it. `Channel` and `RawValue` keep the original physical message.

| Factory | Match | Command |
| --- | --- | --- |
| `KeySpaceSingleKey(key, database)` | Every event for one physical key | `SUBSCRIBE` |
| `KeySpacePattern(pattern, database)` | Redis glob against physical keys | `PSUBSCRIBE` |
| `KeySpacePrefix(prefix, database)` | Literal prefix, with glob characters escaped | `PSUBSCRIBE` |
| `KeyEvent(type, database)` | Keys affected by one event | `SUBSCRIBE`, or `PSUBSCRIBE` for all databases |

- An explicit database applies regardless of the client's selected database.
- Omitting the nullable database on a pattern or event factory matches every database. Exact-key factories require a database.
- Raw event-name overloads support future and module events. `Unknown` is not a valid event name; pass the raw name.

Descriptor rules:

- Descriptors cannot be published or converted to another subscription kind. To use the same bytes as an ordinary channel, construct `new RespireChannel(descriptor.Bytes)`. A reserved-looking name alone never changes routing.
- Channel equality compares bytes only, not kind or notification metadata.
- Delivered `Channel` and `Pattern` values are wire names without the descriptor's metadata, even when ordinary channels and descriptors share a route. Read the emitting database from the parsed `Database`; keep the descriptor if you need its routing intent.

## Redis 8.8 subkeys

Subkey factories: `SubKeySpaceSingleKey`, `SubKeySpacePattern`, `SubKeySpacePrefix`, `SubKeyEvent`, `SubKeySpaceItem`, and `SubKeySpaceEvent`. They follow the same physical-key and database rules. Redis 8.8 emits these events for hash fields.

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

- `GetSubKeys()` offers `Count`, `FirstOrDefault`, `CopyTo`, and an allocating `ToArray`.
- Empty field names count. Delimiter bytes, NUL, and invalid UTF-8 survive parsing.
- Parsing returns false for truncated, overflowing, or inconsistent length prefixes, gap markers, and empty event names.
- Redis emits no item-channel notifications for keys containing a newline, so `SubKeySpaceItem` rejects them. Other layouts accept them.
- Event names containing `|` cannot use `SubKeySpaceEvent`.
- Older Redis versions can acknowledge these channels but never emit subkey events, so a successful subscribe is not feature detection.

See the [Redis subkey format specification](https://redis.io/docs/latest/develop/pubsub/subkeyspace-notifications/).

## Field-level cache decision

Respire's response cache uses `CLIENT TRACKING`, not subkey notifications, because in Redis 8.8:

- Subkey events do not cover whole-key mutations or JSON paths.
- Notifications on a separate connection cannot prevent a stale in-flight read from entering the cache.
- Disconnects, buffer drops, and Cluster ownership changes would need extra cache handling.

The [archived evaluation](https://github.com/thomhurst/Respire/blob/d30260306be7cfb440bfbc7d5f7b1fc8e186d5a6/docs/SUBKEY_CACHE_SPIKE.md) has the details. Subkey notifications suit an application cache that is explicitly best effort. The [partial hash cache](../fundamentals/client-side-caching.md#partial-hash-reads) relies on whole-key tracking invalidations.

## Delivery, pressure, and Redis Cluster

Notification subscriptions behave like ordinary [Pub/sub](pub-sub.md) subscriptions: `SubscribeAsync` returns after acknowledgement, and they share the bounded buffer, `RespireSubscriptionOptions`, `DroppedMessages`, gap markers, `respire.pubsub.messages.dropped`, cancellation, disposal, and automatic resubscription.

Delivery is at most once. Reconnect cannot replay missed events. Expiry events report the actual deletion, not the exact TTL deadline. Use a durable log when you need replay. See [Redis delivery semantics](https://redis.io/docs/latest/develop/pubsub/keyspace-notifications/).

### Redis Cluster

Ordinary channels are cluster-wide, but keyspace and subkey notifications are per node. Respire keeps ordinary Pub/Sub on one logical subscription and gives notification descriptors dedicated connections:

- Exact-key descriptors subscribe only on the primary that owns the key's slot. They move when slot ownership changes.
- Prefix, pattern, keyevent, and subkeyevent descriptors subscribe on every primary.
- Routes on the same primary share a connection. Unsubscribing one route never removes another's channel.
- `SubscribeAsync` returns after every required primary acknowledges. For a cluster-wide descriptor it first reloads the slot map with one `CLUSTER SLOTS` call, so new or promoted primaries are included. If activation fails or is cancelled, Respire removes its routes and closes connections with uncertain state.
- A newly discovered primary is acknowledged before an old primary's route is removed.
- A failure on one primary reconnects only that primary; others keep delivering. `ConnectionStateChanged` reports per-endpoint state, and `Connected` follows acknowledgement of that endpoint's routes.
- When a primary rejects one route of a multi-channel subscription during replay, the other routes report a `Reconnect` gap at once. The rejected route reports its gap when topology reconciliation restores it.
- A subscription rejected by a reachable primary (for example `NOPERM`), or one that fails on a connection other subscriptions still use, ends only that subscription.

`ReconnectPolicy.MaxAttempts` bounds per-primary reconnects and attempts to subscribe on a newly discovered primary. Each failing primary gets the full budget. When it runs out:

1. Every subscription with a route on that primary completes with `ReconnectExhausted`, including cluster-wide subscriptions whose other primaries are healthy, so they never silently miss events. Their routes on other primaries are released.
2. Topology reconciliation stops retrying that primary, which stays `Disconnected`.
3. A later `SubscribeAsync` that needs the primary tries again. The primary is forgotten once it leaves the topology.

Every primary needs the `notify-keyspace-events` flags set by the deployment. Respire never reads or changes them, and Redis acknowledges subscriptions even when notifications are disabled.

Ordering and loss:

- Each primary keeps its own order; a merged subscription has no total order.
- Events emitted during a disconnect are lost. Slot moves, promotions, and topology changes can cause a short gap or a duplicate, because Redis has no event IDs or replay.
- The merged stream uses the same bounded buffer, overflow policy, `DroppedMessages`, and `respire.pubsub.messages.dropped` metric.

Notifications are application events. They do not replace the [`CLIENT TRACKING` response cache](../fundamentals/client-side-caching.md) invalidation protocol.
