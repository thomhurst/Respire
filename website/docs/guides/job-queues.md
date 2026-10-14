---
title: Typed job queues
description: Schedule deduplicated jobs with priority, retry backoff, progress, durable completion events and tracing.
---

# Typed job queues

`RespireJobQueue<T>` in `Respire.Streaming` adds an application job ID and bounded enqueue
deduplication to the [hosted stream worker](stream-workers.md). Workers share a Redis consumer
group, explicitly acknowledge jobs, and recover deliveries abandoned by another consumer.

Jobs support immediate enqueue, delayed scheduling, priority, TTL and durable retry backoff.
Handlers can report progress, observers can replay retained completion events, and tracing links
each delivery attempt to its enqueue.

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
    public async ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireJob<EmailJob> job, CancellationToken cancellationToken)
    {
        // Perform an idempotent application operation using job.Id before returning Ack.
        // job.Payload is EmailJob; job.EntryId identifies this ready-stream entry.
        if (!await job.ReportProgressAsync(50, cancellationToken))
            return RespireStreamWorkerResult.Nack; // Another delivery already owns this job.
        return RespireStreamWorkerResult.Ack;
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

Before appending, the script checks permissions for both writes and rollback. If recording the
deduplication key fails, it removes the new entry before returning the error, so a retry cannot
create a duplicate from that failed append.

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
| `Ack` | Atomically publish a `completed` event and acknowledge this delivery. |
| `Nack` | Apply the queue retry policy; dead-letter when the delivery limit is reached. |
| `job.RetryAfter(delay)` | Return `Nack` with a delay from completion, overriding the queue backoff for this attempt. |
| `DeadLetter` | Atomically append the original envelope plus worker metadata to the dead-letter stream, publish a `dead-letter` event, and acknowledge the current attempt. |

A thrown handler or deserialization exception follows the `Nack` policy. With no options supplied,
registration uses a delivery limit of five; if you supply `RespireStreamWorkerOptions`, its
`DeliveryLimit` value applies (`null` means unlimited retries). A dead-letter destination is
configured automatically unless you supply your own same-slot `DeadLetterStream`.

`MinimumIdleTime` is the visibility timeout from delivery, not a delay measured from the handler's
return. `RecoveryPollInterval` controls the recovery scan cadence. There is no lease extension:
choose a timeout longer than normal processing and keep handlers idempotent. Without a retry
backoff or explicit `RetryAfter`, `Nack` retains the visibility-timeout recovery behavior.

Configure bounded exponential backoff with the registration overload accepting `queueOptions`:

```csharp
using Respire.Streaming;

var queueOptions = new RespireJobQueueOptions
{
    RetryBackoff = TimeSpan.FromSeconds(2),
    MaximumRetryBackoff = TimeSpan.FromMinutes(1),
    SchedulingPollInterval = TimeSpan.FromMilliseconds(250),
    SchedulingBatchSize = 64,
};
```

Pass this as the final argument after `RespireStreamWorkerOptions` in `AddRespireJobQueue`.
For total delivery attempt `n`, the delay is `min(MaximumRetryBackoff, RetryBackoff * 2^(n-1))`.
Handler exceptions use this policy too, discarding any explicit retry delay set before the exception.
Returning `job.RetryAfter(TimeSpan.FromSeconds(30))` overrides
the delay, including the backoff cap, but never the delivery limit or job TTL. Zero requests
an immediate retry. Delays round up to whole milliseconds and must be at most `Int32.MaxValue`
milliseconds. Retry delays start when Redis commits the retry transition, after the handler returns.
Crashes before that transition leave the current entry pending for visibility recovery.

Retries atomically stage a new entry and acknowledge the old pending entry. The new ready entry
has a new `EntryId`; `job.Id` and payload stay unchanged. The delivery limit counts attempts across
these entries, including startup replay and recovery. Dead-letter `_respire.attempt` is the count
on its current ready entry; add `_respire.job.attempts` (zero when absent) for the total count.

Each invocation receives its own DI scope. `ConsumerCount` bounds concurrent handlers per worker
registration; it is not a distributed concurrency cap across hosts. The default batch size is one.
Random consumer names recover abandoned pending work after the visibility timeout. For stable
names, use a distinct name per active host; see [consumer names](stream-workers.md#consumer-names).
Queue groups start at the beginning so jobs enqueued before worker startup are processed.

Completion is fenced by consumer identity and delivery attempt. A slow handler that finishes
after another consumer recovers its job cannot acknowledge, dead-letter or reschedule that newer attempt.
This prevents lost queue deliveries, but cannot undo the slow handler's external side effects.

Read dead letters through the stream API using `queue.DeadLetterStreamKey` (or your override).
Entries retain `_respire.job.id` and binary `_respire.job.payload`, plus worker fields such as
`_respire.source_id`, `_respire.attempt`, and `_respire.reason`. For a controlled re-enqueue after
fixing a poison job, choose a new application ID or wait for the original deduplication window
and preserve an application-level idempotency key. Reading a dead letter does not requeue it.

## Delay, priority and expiry

Use `EnqueueScheduledAsync` with `RespireJobEnqueueOptions` for scheduling. A producer sending
string payloads can use explicit UTF-8 codecs:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "reminders", Encoding.UTF8.GetBytes);
await queue.EnqueueScheduledAsync("reminder:customer-42",
    "customer@example.com", new RespireJobEnqueueOptions
    {
        Delay = TimeSpan.FromMinutes(5),
        Priority = 10,
        TimeToLive = TimeSpan.FromHours(1),
        DeduplicationWindow = TimeSpan.FromHours(24),
    });
```

The returned `EntryId` identifies the staged enqueue, and duplicates return that same ID with
the original payload and options. Promotion assigns a new ready-stream ID. Deduplication is
shared with `EnqueueAsync`, so switching methods does not bypass an active deduplication window.

Pass a non-null `RespireJobQueueOptions` to the registration overload to enable scheduling,
TTL checks and durable retries (including `RetryAfter`). The original overload, or null queue
options, retains immediate-only processing and requires no access to the scheduling keys.
Enable scheduling on every worker for a scheduled queue, and grant the permissions below
before switching producers to `EnqueueScheduledAsync`.

Each worker reader promotes at most `SchedulingBatchSize` due jobs (default 64, maximum 256) before reading or recovering
work, at most once per `SchedulingPollInterval`. Redis `TIME` determines due times and expiry; application clocks are not used. Clock jumps
on the Redis server or after failover can advance or postpone eligibility. Delay is a minimum,
not an execution deadline. Busy handlers, polling, ready-stream backlog and recovery can add latency.
Scheduling pauses when there are no active job workers and resumes on restart. A blocking reader
wakes at least every `SchedulingPollInterval` (default one second); handlers are never interrupted
to run a scheduling pass. An index entry whose staged body was deleted is removed and skipped
so it cannot block later jobs; the missing payload cannot be recovered.

The scheduler selects the oldest due jobs first (equal due times use Redis lexicographic staging
ID order), then orders that bounded batch by descending integer `Priority`, preserving selection
order for equal priorities. Priority defaults to zero and accepts negative values. This is **batch
priority**, not a global priority heap: it cannot reorder entries already in the ready stream,
preempt running handlers, or outrank another batch. Continuous newer high-priority arrivals cannot
starve an older due job while scheduling makes progress. Concurrent consumers may start or finish
handlers in a different order. Use scheduled enqueue with zero delay for priority-only jobs;
ordinary `EnqueueAsync` appends directly to the ready stream.

`TimeToLive` starts at enqueue and includes delay, backlog and retries. Null means no expiry;
a specified TTL must be positive and at most `Int32.MaxValue` milliseconds. The scheduler drops
expired staged jobs, and the worker acknowledges expired ready deliveries without invoking a
handler or adding a dead letter. TTL is an admission check immediately before dispatch, not a
cancellation deadline for a running handler; it cannot undo external side effects. Expired staged
bodies are removed on a scheduling pass after their due time. Expired ready bodies follow normal
stream retention. With `DeleteAcknowledgedEntries`, retries and expiry use the same Redis 8.2+
group-aware `XACKDEL ACKED` deletion policy as successful completion (and require that permission).
On Redis 8.2+, these transitions also require `COMMAND INFO` to detect `XACKDEL` before staging
or acknowledging. Denied discovery fails the transition and leaves the original delivery pending.
Older servers retain acknowledged entries for trimming. Expiry does not remove the deduplication record.

## Progress and completion observers

Call `await job.ReportProgressAsync(percent, cancellationToken)` with an integer from 0 to 100
inside a hosted handler. `true` confirms the report was appended; `false` means the delivery
has been acknowledged, recovered by another attempt, or its body has been removed. Reports
are fenced by both consumer name and delivery count, including recovery with the same name.
Await reports before returning. Progress neither extends the visibility timeout nor completes
the job; returning `Ack` is what publishes success. A retry may start progress again from zero,
so observers should compare the event's `EntryId` and `Attempt`. The attempt counter is scoped
to a ready-stream entry; a durable retry creates a new entry with a fresh counter. Progress
need not be monotonic.

Each queue has an independent event stream at `queue.EventsStreamKey`. A producer-only client
can observe it without registering a hosted worker:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "email", Encoding.UTF8.GetBytes);
using var shutdown = new CancellationTokenSource();
var cursor = RespireStreamId.Beginning; // Load the last successfully processed event ID on restart.
await foreach (var update in queue.ReadEventsAsync(cursor, shutdown.Token))
{
    Console.WriteLine($"{update.EntryId} attempt {update.Attempt}: {update.Kind} {update.Percent}");
    cursor = update.Id; // Persist this checkpoint after your observer's side effects succeed.
}
```

`Kind` is `progress`, `completed`, or `dead-letter`. Every event contains `JobId`, `EntryId`,
`Attempt`, and its own cursor `Id`; only progress includes `Percent`, and only dead letters
include `Reason` (`explicit`, `nack`, `processing-failed`, or `delivery-limit`). Events omit
the payload and exception message. Immediate jobs without durable retries retain the enqueue
result's `EntryId`. Scheduling promotion and durable retries create new ready-stream IDs;
use a unique application `JobId` per logical job to correlate events across those transitions.
Expired jobs produce no terminal event. Deserialization
errors can dead-letter without invoking a handler and still produce a terminal event.

Each enumeration sees all events after its cursor; observers do not compete for events.
The default cursor reads from the beginning. The underlying stream reader reconnects after
transient transport failures, resuming after its last yielded event, with backoff capped at
3.2 seconds. Cancellation ends enumeration; permanent errors surface to the caller. On process
restart, use your persisted cursor. Saving a cursor before completing side effects can lose
observer work; saving it afterwards can replay a duplicate after a crash. Make observer side
effects idempotent using the event ID. A progress request with an uncertain reply can also
append again when retried.

Terminal publication, dead-letter append where applicable, and acknowledgement run in one
fenced Redis script. A consumer crash before that script leaves the job pending for recovery;
a crash after it leaves the terminal event available even when the consumer never receives the
reply. Repeating completion cannot append another event for an acknowledged delivery.
An event write failure leaves the job pending. If a later dead-letter or acknowledgement write
fails, the script removes its newly appended events before returning the error. Such errors
fault the worker; repair the key type/permissions and restart it to recover the pending delivery.

Events have **no automatic expiry or trimming**. Set a retention policy with Redis stream APIs
on `EventsStreamKey`, based on the slowest observer's durable checkpoint. Trimming, eviction,
deleting the stream, or losing Redis persistence can lose events, including during an outage;
the reader does not detect a gap caused by trimming. Progress can generate substantial storage,
so report at useful milestones. Do not expire or trim unprocessed ready-stream bodies.

## Enqueue-to-processing tracing

Subscribe OpenTelemetry tracing to `RespireStreamWorkerTelemetry.ActivitySourceName`
(`Respire.Streaming`) and metrics to `RespireStreamWorkerTelemetry.MeterName`.
`job enqueue` is a producer span. Its W3C `traceparent` and optional `tracestate` are persisted
with the winning enqueue's envelope; a deduplicated call never replaces them. Each `job process`
consumer span is a new root with an `ActivityLink` to that producer context, including replay,
retry, and recovery by another host. Handler child spans use the processing span as parent.
Job queues reserve the `traceparent` and `tracestate` fields and use them regardless of the
generic stream worker's field-name options.

Without a sampled producer activity, enqueue propagates an existing ambient W3C context.
Without either context, it writes no tracing fields. Processing creates no activity without a
listener, and never adopts the worker host's ambient activity. Invalid incoming context is
ignored. Listener exceptions do not change enqueue or processing outcomes.

The existing stream-worker duration, dead-letter, lag, and pending metrics cover job workers.
Their dimensions contain only the configured, bounded `TelemetryName` and fixed outcome/reason
values. Job IDs, entry IDs, payloads, and raw queue keys are not metric dimensions. Choose a
fixed application role for `TelemetryName`, not a per-tenant or per-job name.

## Storage and delivery guarantees

Queue names must be nonblank and cannot contain braces. The generated ready stream is
`{respire-job:<name>}:ready`; its default dead-letter stream appends `:dead`, and deduplication
keys append `:dedup:<SHA256-of-job-ID>`. The staging stream appends `:scheduled`, and its due-time
sorted set appends `:due`. The event stream appends `:events`. The shared hash tag keeps the atomic operations in one
Redis Cluster slot, including through key-prefixed clients. Reserve this namespace exclusively
for the queue. All worker hosts must agree on the codecs and worker policy for a queue.

Producers require Redis 7.0 or later for [Lua ACL preflight](https://redis.io/docs/latest/develop/programmability/lua-api/#redisacl_check_cmdcommand-arg).
In addition to script execution,
allow `GET` and `SET` on deduplication keys, and `XADD` and `XDEL` on the ready stream; `XDEL`
is required to roll back an append if recording its deduplication key fails.
Scheduled producers and workers additionally need `TIME`, `TYPE`, `XRANGE`, `XADD`, `XDEL`,
`ZADD`, `ZRANGEBYSCORE` and `ZREM` on the generated scheduling keys, plus the normal group,
pending, acknowledgement and recovery permissions. Transition scripts check key types and
cleanup permissions before writes, and compensate a staging append when indexing fails.
Hosted job workers also require Redis 7.0 or later because registration configures a
dead-letter destination and terminal completion uses Lua ACL preflight. Allow `XADD` and
`XDEL` on both event and dead-letter streams, alongside the underlying worker's read,
recovery, and acknowledgement permissions; rollback requires `XDEL`. These destinations must
resolve to distinct keys in the ready stream's Cluster slot. Observers require `XREAD`
(and `XREVRANGE` when starting at `$`). Recovery alone in
the underlying stream worker supports Redis 6.2, but that is not sufficient for this job queue.
Use real Redis for queue tests;
`RespireFakeServer` supports only its built-in stream-worker scripts, not the job queue scripts.

Consumer crashes leave delivered jobs in the group's pending list for redelivery. The guarantee is
at-least-once processing while Redis retains the stream and group state. Redis durability, replica
failover, persistence and memory eviction settings still determine survival of server failures.
Use an appropriate persistence policy and protect queue keys from eviction. Do not delete groups,
expire streams, or trim unprocessed entries. Acknowledged stream bodies are retained by default;
plan retention deliberately using the worker's acknowledgement/deletion options and stream APIs.
Promotion moves a staged job to the ready stream and removes its scheduling metadata in one Lua
execution. Retry stages the next attempt and acknowledges the current attempt in one fenced Lua
execution. A scheduler or consumer process crash cannot interrupt these server-side transitions:
before commit the original is recoverable; after commit the destination is durable to the extent
of Redis persistence. A lost reply may hide a successful transition, but repeating it cannot
reschedule an acknowledged attempt or promote an already removed staging entry. Do not edit,
trim, expire or evict the staging stream or due index independently.
