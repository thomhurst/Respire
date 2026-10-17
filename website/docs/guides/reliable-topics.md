---
title: Reliable topics
description: Durable fanout with named subscriptions, acknowledgements, replay, and dead letters.
---

# Reliable topics

`RespireReliableTopic<T>` in **Respire.Streaming** retains publications in a Redis
stream. Each named subscription is an independent consumer group: every subscription
receives every retained publication, while consumers sharing a subscription compete
for its messages. Subscription state survives client disposal and disconnection.

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var topic = new RespireReliableTopic<string>(client, "orders",
    Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit: 5,
    maximumLength: 100_000);

// Register durable subscriptions before taking their consumers offline.
var billing = await topic.SubscribeAsync("billing");
var shipping = await topic.SubscribeAsync("shipping");
await topic.PublishAsync("order-42");

// A different client can reopen "billing" after reconnecting.
foreach (var message in await billing.ReceiveAsync(TimeSpan.FromSeconds(30), max: 10))
{
    Console.WriteLine(message.Payload); // Replace with an idempotent application operation.
    bool acknowledged = await billing.AckAsync(message);
}
// Shipping still receives its own copy, even after billing acknowledges.
```

The default subscription start is the beginning of retained history. Pass
`RespireStreamId.New` to start a **new** subscription at the current stream end.
Reopening an existing subscription never resets its position, regardless of `start`.
Different subscription names receive independent copies; identical names share work.

## Delivery, rejection, and dead letters

`ReceiveAsync` is nonblocking and returns at most 1–1000 deliveries. Each has `Id`,
`Payload`, `DeliveryCount` (starting at one), and an opaque `Receipt`. Server time
sets a visibility deadline per delivery. Another consumer cannot receive it until
that deadline expires. Receivers may use different visibility durations.

Pass the message to `AckAsync` to complete only that subscription's delivery.
Acknowledgement preserves source history for other subscriptions and replay.
`NackAsync(message, delay)` revokes the receipt immediately and schedules another
delivery. Zero delay permits immediate retry. ACK and NACK return `false` for an
expired, revoked, superseded, wrong-subscription, or already completed receipt.
Another instance of the same subscription can complete the receipt.

After `deliveryLimit` deliveries (default five), the next receive that encounters
the expired or rejected message moves it to that subscription's `DeadLetterStreamKey`
and acknowledges its pending entry. Other subscriptions remain unaffected.
Dead letters retain binary `payload`, `source_id`, `delivery_count`, and `subscription`.
Read them with `client.Streams.ReadAsync(subscription.DeadLetterStreamKey)` and apply
your own dead-letter retention. To reprocess one, decode and publish its payload;
that new publication goes to every subscription.

Receive calls drive bounded recovery and dead-letter movement; no background worker
is created. An empty batch can mean recovery cleaned trimmed entries or moved dead
letters. Keep polling. `ConsumeAsync` supplies an asynchronous polling loop:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var topic = new RespireReliableTopic<string>(client, "orders",
    Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit: 5,
    maximumLength: 100_000);
var billing = await topic.SubscribeAsync("billing");
using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var stoppingToken = stopping.Token;

await foreach (var message in billing.ConsumeAsync(TimeSpan.FromSeconds(30),
    batchSize: 10, cancellationToken: stoppingToken))
{
    Console.WriteLine(message.Payload); // Replace with an idempotent application operation.
    await billing.AckAsync(message, stoppingToken);
}
```

The caller explicitly ACKs or NACKs messages. Breaking enumeration leaves unacknowledged
deliveries leased. The default empty-poll interval is 100 milliseconds; `pollInterval`
overrides it. Durations are rounded up to milliseconds and bounded by
`Int32.MaxValue` milliseconds; visibility, poll interval, and maximum age must be
positive, while NACK delay may be zero.

Delivery is **at least once**. A lost receive reply or codec failure leaves the batch
leased until expiry and counts toward its delivery limit. Cancellation cannot undo
a dispatched Redis operation. Retrying publication after an ambiguous network result
may publish a duplicate. Use application identifiers and idempotent processing.

## Replay and retention

`SeekAsync(RespireStreamId.Beginning)` replays retained history. `SeekAsync(id)`
positions the group **after** that ID. `SeekAsync(DateTimeOffset)` includes entries
at or after the specified timestamp, using the timestamp in stream IDs.
`SeekAsync(RespireStreamId.New)` skips to the current end.

Seek changes the position for every consumer sharing the subscription. Stop those
consumers first. Seek atomically rejects any subscription with pending deliveries,
including delayed NACKs; acknowledge or exhaust them before seeking. Replayed entries
start a fresh delivery count. Other subscriptions keep their own positions.

Choose one optional retention bound on the topic constructor:

- `maximumLength`: exact `XADD MAXLEN` trimming on publication.
- `maximumAge`: exact `XADD MINID` trimming using server time on publication.
- Neither: retain source history indefinitely, including acknowledged messages.

Retention is a hard bound and can discard unread or pending messages. Offline
subscriptions can recover only retained history. Age retention runs when publishing,
not on a timer. Trimmed pending entries and their metadata are cleaned when their
leases become due and a consumer polls. Size retention to cover expected downtime.

## Codecs, storage, and compatibility

Supply thread-safe `Func<T, byte[]>` and `Func<byte[], T>` delegates, just as for
the [reliable queue](reliable-queues.md) and [job queue](job-queues.md). Reuse the
same serialization, compression, and encryption pipeline across these APIs: serialize,
compress, then encrypt on write; decrypt, decompress, then deserialize on read.
The topic treats payload bytes as opaque and preserves them in dead letters.
All participants must use compatible codecs and delivery limits. The client stays
caller-owned; subscriptions require no disposal.

Requires Redis 7+ or Valkey with Lua, streams, hashes, sorted sets, and permissions
for the topic commands. Tests cover Redis 7, Redis 8, Valkey, RESP2/RESP3 and Cluster.
Scripts preflight types and mutation permissions. A private deadline index supports
per-delivery visibility and delayed rejection on all supported servers.

Topic names must be nonblank and contain no braces. Subscription names are nonblank
UTF-8 strings. Generated keys share `{respire-topic:<name>}`; hashed subscription
suffixes isolate metadata and dead letters. Client key prefixes are supported, and
resolved keys must be distinct and share one Cluster slot. Reserve this namespace:
do not externally alter consumer groups, their pending lists, or delivery metadata.
Redis persistence, replication and eviction settings determine server durability.

| API | Delivery model |
| --- | --- |
| [Plain pub/sub](pub-sub.md) | Live subscribers receive broadcasts; disconnected subscribers miss them. |
| Reliable topic | Every named subscription gets each retained publication; its consumers share work and explicitly ACK/NACK. |
| [Reliable queue](reliable-queues.md) | One consumer receives each message; ACK removes the source entry. |
| [Stream workers](stream-workers.md) | Hosted handlers consume application streams with configured groups and recovery. |
