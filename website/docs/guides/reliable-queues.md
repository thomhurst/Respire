---
title: Reliable message queues
description: Receive typed messages with visibility leases, delayed rejection, delivery limits and dead letters.
---

# Reliable message queues

`RespireReliableQueue<T>` in **Respire.Streaming** provides a message queue without
a hosted worker or job scheduler. Send a payload, receive a batch with a visibility
timeout, then acknowledge it or reject it with an optional delay.

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireReliableQueue<string>(client, "notifications",
    Encoding.UTF8.GetBytes, Encoding.UTF8.GetString, deliveryLimit: 5);

await queue.SendAsync("Order shipped", deduplicationId: "order-42-shipped");

foreach (var message in await queue.ReceiveAsync(TimeSpan.FromSeconds(30), max: 10))
{
    try
    {
        Console.WriteLine(message.Payload); // Replace with your idempotent application operation.
        bool acknowledged = await queue.AckAsync(message);
        // false means the lease expired or this receipt was already completed.
    }
    catch
    {
        await queue.NackAsync(message, TimeSpan.FromSeconds(10));
    }
}
```

Supply thread-safe serialization and deserialization delegates. Source-generated
JSON codecs and arbitrary binary payloads work; the queue does not select a JSON
library. The client remains caller-owned. Every instance sharing the same queue
must use matching codecs and delivery limits.

## Delivery and receipts

`ReceiveAsync` is nonblocking and returns at most `max` messages (1–1000). Poll it
at an interval appropriate for your application. Each delivery has a stable `Id`,
a `Payload`, a `DeliveryCount` starting at one, and an opaque `Receipt`. Pass the
message to `AckAsync` or `NackAsync`; either can be called through another instance
using the same queue and client key prefix.

Start with small batches: recovery runs in one atomic script, so larger batches
can increase latency for other clients sharing the server.

The server's clock sets the visibility deadline for each delivery. Different
receivers can use different durations. Until the deadline, other receivers cannot
receive that message. Once it expires, the next receiver can redeliver it with a
new receipt. ACK and NACK return `false` for an expired, revoked, superseded or
already completed receipt. A stale receiver cannot acknowledge a newer delivery.

ACK removes the message and its delivery metadata. NACK immediately revokes the
receipt and sets a new due time. A zero delay permits immediate redelivery; a
positive delay replaces the remaining visibility timeout. Durations are rounded
up to milliseconds and bounded by `Int32.MaxValue` milliseconds. Visibility and
deduplication windows must be positive; NACK delay can be zero.

Delivery is **at least once**, so application effects must tolerate duplicates.
Cancellation or a connection failure cannot undo a dispatched operation. A lost
receive response or a deserializer failure leaves the whole batch leased; it can
be received again after expiry and counts toward the delivery limit. A lost ACK
response leaves its outcome uncertain, even if retrying returns `false`.

## Delivery limit and dead letters

After a message has been delivered `deliveryLimit` times (default five), the next
receive that encounters its expired lease or due NACK moves it atomically to
`DeadLetterStreamKey`. No background service is required: receive calls perform
bounded recovery and dead-letter work, processing due entries first. A batch can
be empty while the call moves exhausted messages; continue polling.

Dead-letter entries retain binary `payload`, the original `source_id`, and the
final `delivery_count`. Read them with the ordinary stream APIs, for example
`await client.Streams.ReadAsync(queue.DeadLetterStreamKey)`.

Dead letters are retained until your application trims or deletes them. To replay
one, deserialize its payload and send it again; use a new deduplication ID or wait
for the previous deduplication window to expire.

## Deduplication and storage

An optional `deduplicationId` suppresses duplicate sends for a bounded window
(default one day). Duplicate sends return the original stream ID and preserve the
first payload, even after ACK or dead-lettering. They do not extend the window.
Reuse the same ID when retrying a send after an uncertain network outcome. Without
an ID, every call appends a new message. Deduplication does not make processing
exactly once. Window expiry removes only deduplication metadata, never messages.

Choose a window long enough to cover send retries, including retries after ACK or
dead-lettering. Each distinct deduplication ID retains a separate string key for
that window; shorter windows reduce memory use at high send rates.

Queue names must be nonblank and contain no braces. Generated keys share the hash
tag `{respire-queue:<name>}` and support Redis Cluster and client key prefixes.
The implementation validates that resolved keys are distinct and in the same slot.
Reserve this namespace for the queue: do not externally trim its ready stream,
alter its group, add other consumer groups, or edit its delivery metadata. Configure
Redis persistence and eviction to suit your durability requirements.

## Server compatibility and choosing an API

Requires Redis 7+ or Valkey with Lua and the queue's stream, hash, sorted-set and
deduplication string commands permitted. Scripts preflight types and mutation
permissions before changing queue state. Redis 8.2+ uses `XACKDEL` when available;
older servers, Valkey, denied discovery and unavailable native commands use atomic
`XACK` plus `XDEL`. Capability discovery happens on the serving server per operation,
so reconnects and failovers do not retain stale capability decisions.

The queue deliberately uses a deadline index for recovery and delayed NACK.
[`XREADGROUP CLAIM`](https://redis.io/docs/latest/commands/xreadgroup/) uses one idle
threshold per read, while [`XNACK`](https://redis.io/docs/latest/commands/xnack/)
makes a message immediately claimable. Neither directly expresses differing
visibility durations alongside delayed rejection. The same deadline algorithm on
all supported servers preserves those semantics. Streams hold payloads and a
private consumer group tracks initial delivery; receipts fence subsequent work.

| API | Use it when |
| --- | --- |
| Reliable queue | You want to send and receive messages and explicitly ACK/NACK them. |
| [Job queue](job-queues.md) | You want hosted handlers, scheduling, priorities, job flows, progress or job logs. |
| [Stream workers](stream-workers.md) | You want to consume existing streams and control their groups and recovery policy. |

Reliable queues share key validation and server capability helpers with job queues
and stream workers, but use their own key namespace and delivery contract. Their
streams are not interchangeable with job queues or dashboard-managed job streams.
