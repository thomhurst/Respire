---
title: Typed job queues
description: Enqueue deduplicated jobs and process them with scoped handlers, visibility recovery and dead letters.
---

# Typed job queues

`RespireJobQueue<T>` in `Respire.Streaming` adds an application job ID and bounded enqueue
deduplication to the [hosted stream worker](stream-workers.md). Workers share a Redis consumer
group, explicitly acknowledge jobs, and recover deliveries abandoned by another consumer.

This API currently queues jobs for immediate processing. Delay, priority, job TTL and retry
backoff are tracked in [#1649](https://github.com/thomhurst/Respire/issues/1649); progress,
completion events and producer tracing are tracked in
[#1650](https://github.com/thomhurst/Respire/issues/1650).

## Register and enqueue

Install `Respire.Streaming` and `Respire.DependencyInjection`, then register a client, explicit
codecs, and a handler. The codecs may run concurrently and support source-generated JSON.

<!-- doc-test-tail-declaration: split-before=public sealed record EmailJob -->
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire;
using Respire.DependencyInjection;
using Respire.Streaming;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireJobQueue<EmailHandler, EmailJob>("email",
    job => JsonSerializer.SerializeToUtf8Bytes(job, JobJson.Default.EmailJob),
    bytes => JsonSerializer.Deserialize(bytes, JobJson.Default.EmailJob)
        ?? throw new JsonException("Missing job payload."),
    new()
    {
        ConsumerCount = 4,
        MinimumIdleTime = TimeSpan.FromMinutes(2),
        RecoveryPollInterval = TimeSpan.FromSeconds(5),
        DeliveryLimit = 5,
        TelemetryName = "email-jobs",
    });

using var host = builder.Build();
var queue = host.Services.GetRequiredKeyedService<RespireJobQueue<EmailJob>>("email");
var submitted = await queue.EnqueueAsync("welcome:customer-42",
    new EmailJob("customer@example.com"), deduplicationWindow: TimeSpan.FromHours(24));
Console.WriteLine($"Entry {submitted.EntryId}, new: {submitted.Enqueued}");
await host.RunAsync();

public sealed record EmailJob(string Recipient);

[JsonSerializable(typeof(EmailJob))]
internal partial class JobJson : JsonSerializerContext { }

public sealed class EmailHandler : IRespireStreamHandler<RespireJob<EmailJob>>
{
    public ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireJob<EmailJob> job, CancellationToken cancellationToken)
    {
        // Perform an idempotent application operation using job.Id before returning Ack.
        // job.Payload is EmailJob; job.EntryId identifies this enqueue across redeliveries.
        return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
```

A producer-only process can construct `new RespireJobQueue<EmailJob>(client, "email", serialize)`
without registering a worker. The producer and worker must use the same queue name and client
key prefix. Neither takes ownership of the supplied client. Each queue name can be registered
once per service collection, and worker `TelemetryName` values must also be unique.

## Deduplication and job identity

Enqueue runs one Lua script that checks an expiring deduplication key, appends to the ready
stream, and records the new stream ID. Concurrent submissions with the same job ID return that
original ID; only one has `Enqueued == true`. The first payload wins, so resubmitting an ID does
not update a queued job. The default deduplication window is one day; a custom window must be
positive and at most `Int32.MaxValue` milliseconds. Fractions round up to one millisecond.

The window starts at the successful enqueue and duplicate calls do not extend it. Acknowledgement
or dead-letter completion does not remove the deduplication record. Once it expires, the same
application ID can create a new stream entry, even if the earlier entry is still pending.
The deduplication window is **not a job TTL** and does not delete the payload.

A network failure or cancellation after sending may leave the enqueue outcome uncertain.
Retry with the same job ID within the window. An expired or evicted deduplication record allows
another enqueue, so handlers still need idempotent side effects. A deduplicated submission returns
the earlier stream ID even if an operator has since deleted that entry.

## Processing, retry and dead letters

A handler returns one of these explicit results:

| Result | Effect |
| --- | --- |
| `Ack` | Acknowledge this delivery after processing completes. |
| `Nack` | Leave the delivery eligible for recovery after its visibility timeout; dead-letter when the delivery limit is reached. |
| `DeadLetter` | Atomically append the original envelope plus worker metadata to the dead-letter stream and acknowledge the current attempt. |

A thrown handler or deserialization exception follows the `Nack` policy. With no options supplied,
registration uses a delivery limit of five; if you supply `RespireStreamWorkerOptions`, its
`DeliveryLimit` value applies (`null` means unlimited retries). A dead-letter destination is
configured automatically unless you supply your own same-slot `DeadLetterStream`.

`MinimumIdleTime` is the visibility timeout from delivery, not a delay measured from the handler's
return. `RecoveryPollInterval` controls the recovery scan cadence. There is no lease extension:
choose a timeout longer than normal processing and keep handlers idempotent. Returning `Nack`
does not accept a custom retry delay in this version.

Each invocation receives its own DI scope. `ConsumerCount` bounds concurrent handlers per worker
registration; it is not a distributed concurrency cap across hosts. The default batch size is one.
Random consumer names recover abandoned pending work after the visibility timeout. For stable
names, use a distinct name per active host; see [consumer names](stream-workers.md#consumer-names).
Queue groups start at the beginning so jobs enqueued before worker startup are processed.

Completion is fenced by consumer identity and delivery attempt. A slow handler that finishes
after another consumer recovers its job cannot acknowledge or dead-letter that newer attempt.
This prevents lost queue deliveries, but cannot undo the slow handler's external side effects.

Read dead letters through the stream API using `queue.DeadLetterStreamKey` (or your override).
Entries retain `_respire.job.id` and binary `_respire.job.payload`, plus worker fields such as
`_respire.source_id`, `_respire.attempt`, and `_respire.reason`. For a controlled re-enqueue after
fixing a poison job, choose a new application ID or wait for the original deduplication window
and preserve an application-level idempotency key. Reading a dead letter does not requeue it.

## Storage and delivery guarantees

Queue names must be nonblank and cannot contain braces. The generated ready stream is
`{respire-job:<name>}:ready`; its default dead-letter stream appends `:dead`, and deduplication
keys append `:dedup:<SHA256-of-job-ID>`. The shared hash tag keeps the atomic operations in one
Redis Cluster slot, including through key-prefixed clients. Reserve this namespace exclusively
for the queue. All worker hosts must agree on the codecs and worker policy for a queue.

Redis 6.2 or later and permission to run Lua scripts are required. Use real Redis for queue tests;
`RespireFakeServer` supports only its built-in stream-worker scripts, not the enqueue script.
The existing worker's tracing and metrics remain available; enqueue-to-processing trace propagation
is not yet provided by this API.

Consumer crashes leave delivered jobs in the group's pending list for redelivery. The guarantee is
at-least-once processing while Redis retains the stream and group state. Redis durability, replica
failover, persistence and memory eviction settings still determine survival of server failures.
Use an appropriate persistence policy and protect queue keys from eviction. Do not delete groups,
expire streams, or trim unprocessed entries. Acknowledged stream bodies are retained by default;
plan retention deliberately using the worker's acknowledgement/deletion options and stream APIs.
