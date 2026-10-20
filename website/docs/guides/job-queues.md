---
title: Typed job queues
description: Schedule deduplicated jobs and parent/child flows with retries, retained results, completion events and tracing.
---

# Typed job queues

`RespireJobQueue<T>` in `Respire.Streaming` adds an application job ID and bounded enqueue
deduplication to the [hosted stream worker](stream-workers.md). Workers share a Redis consumer
group, explicitly acknowledge jobs, and recover deliveries abandoned by another consumer.

Jobs support immediate enqueue, delayed scheduling, priority, TTL and durable retry backoff.
Parent/child flows coordinate nested jobs and retain their results with configurable failure policies.
Handlers can report progress, observers can replay retained completion events, and tracing links
each delivery attempt to its enqueue.

For authorized inspection and dead-letter retry, see the [queue and stream dashboard](stream-dashboard.md).
For a runnable worker, enqueue API and dashboard, start with the
[`respire-worker` project template](../getting-started.md#job-worker-and-dashboard).
For messages received and acknowledged by application code without hosted handlers or scheduling,
use a [reliable message queue](reliable-queues.md).

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
original ID; with the default mode only one has `Enqueued == true`. The first payload wins, so resubmitting an ID does
not update a queued job. The default deduplication window is one day; a custom window must be
positive and at most `Int32.MaxValue` milliseconds. Fractions round up to one millisecond.

Before appending, the script checks permissions for both writes and rollback. If recording the
deduplication key fails, it removes the new entry before returning the error, so a retry cannot
create a duplicate from that failed append.

By default (`FirstSubmissionWins`), the window starts at the successful enqueue and duplicate calls do not extend it. Acknowledgement
or dead-letter completion does not remove the deduplication record. Once it expires, the same
application ID can create a new stream entry, even if the earlier entry is still pending.
The deduplication window is **not a job TTL** and does not delete the payload.

A network failure or cancellation after sending may leave the enqueue outcome uncertain.
Retry with the same job ID within the window. An expired or evicted deduplication record allows
another enqueue, so handlers still need idempotent side effects. A deduplicated submission returns
the earlier stream ID even if an operator has since deleted that entry.

### Extending the window

Use `Extend` to renew the winning enqueue's window on each duplicate:

<!-- doc-test-tail-declaration: split-before=public sealed record EmailJob -->
```csharp
using System.Text.Json;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<EmailJob>(client, "email",
    job => JsonSerializer.SerializeToUtf8Bytes(job));
await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend,
    "email:customer-42", new EmailJob("customer@example.com"),
    deduplicationWindow: TimeSpan.FromMinutes(10));

public sealed record EmailJob(string Recipient);
```

For scheduled jobs, set `RespireJobEnqueueOptions.DeduplicationMode` to
`RespireJobDeduplicationMode.Extend`. Transactional `Enqueue` accepts the same mode argument;
`EnqueueScheduled` uses the same options. Renewal happens inside the enqueue script, or inside
`EXEC` for a transaction.

The **first successful enqueue fixes both mode and window** for that generation of the application
job ID. Duplicates retain its payload, trace context, group, delay, priority, lifetime and checkpoint
options, and return its original `EntryId` with `Enqueued == false`. Supplying another mode or
window on a duplicate does not change that policy. This also applies when switching between
immediate, scheduled and transactional APIs: a default call renews an existing Extend record,
and an Extend call cannot change an existing FirstSubmissionWins record.

| State when a duplicate arrives | Extend behavior |
| --- | --- |
| Queued or scheduled | Renew deduplication only; do not postpone the due time or replace options. |
| Active or waiting for a retry | Renew deduplication; leave the delivery, attempt count and retry backoff intact. |
| Completed, dead-lettered, or expired by job TTL | Renew the retained record and return the original ID; do not enqueue another job. |
| Deduplication record expired or evicted | Create a new independent job with the new submission's mode and options. Earlier work may still exist. |

Acquisition, promotion, retries, cooperative yields and completion never renew the window themselves.
Concurrent duplicates and worker acquisition are serialized by Redis; renewal does not mutate a
stream entry or worker ownership. Extend is a sliding suppression window, not debounce or payload
replacement. For pending payload replacement, use `Replace` below. Active follow-up submissions
(`KeepLastIfActive`) are tracked in [#1987](https://github.com/thomhurst/Respire/issues/1987).

Upgrade **all producers**, including transactional producers, before enabling Extend. Its
deduplication record includes the winning mode and duration; older producers understand only
plain stream IDs. Default records remain compatible. Both modes use the existing `GET`/`SET`
permissions; an Extend duplicate needs `SET` to renew, while a default duplicate remains read-only.

The dashboard's bounded pending-delivery table shows the application job ID as the deduplication
identity and the mode retained on the winning entry. These are submission metadata, not an
indication that the deduplication window is still live. No payload is fetched for this display.

### Replacing pending submissions

Select `RespireJobDeduplicationMode.Replace` on the first submission to keep the latest envelope
until a worker first acquires the job:

<!-- doc-test-tail-declaration: split-before=public sealed record EmailJob -->
```csharp
using System.Text.Json;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<EmailJob>(client, "email",
    job => JsonSerializer.SerializeToUtf8Bytes(job));
await queue.EnqueueAsync(RespireJobDeduplicationMode.Replace,
    "email:customer-42", new EmailJob("old@example.com"));
// The stored mode applies even when a duplicate uses the default overload.
await queue.EnqueueScheduledAsync("email:customer-42", new EmailJob("new@example.com"),
    new() { Delay = TimeSpan.FromMinutes(5), Priority = 10 });

public sealed record EmailJob(string Recipient);
```

The application job ID remains the deduplication identity. The first successful enqueue fixes
the mode and the window deadline. A replacement does **not** renew that deadline. Duplicates
cannot change a FirstSubmissionWins or Extend generation into Replace, or change Replace to
another mode. Immediate, scheduled, grouped and transactional submissions share this policy.
For a new scheduled generation, set `RespireJobEnqueueOptions.DeduplicationMode` to `Replace`;
transactional `Enqueue` accepts the mode argument and `EnqueueScheduled` accepts the options.
The transition runs inside `EXEC`, so a discarded or WATCH-aborted transaction replaces nothing.

Each accepted replacement returns its new submission `EntryId` with `Enqueued == true`.
Its payload, trace context, delay, priority, TTL, group and checkpoint settings replace the entire
previous envelope. Delay and TTL restart from that submission's Redis server time; switching
to immediate enqueue clears scheduled options. A replacement with resumability gets a fresh
checkpoint identity. Concurrent submissions are serialized by Redis; the last accepted one wins.
As with other scheduled jobs, promotion creates a different delivery ID.

| State when a duplicate arrives | Replace behavior |
| --- | --- |
| Pending, scheduled, or promoted but unread | Accept a replacement and fence the superseded envelope from delivery. |
| First acquisition wins the race | Preserve the acquired payload and options; return the latest submission ID with `Enqueued == false`. |
| Retry backoff, visibility recovery, or cooperative yield after acquisition | Preserve the acquired generation, including its attempt count, checkpoint and backoff. |
| Completed, dead-lettered, or discarded by a worker after job TTL expiry | Suppress duplicates until the original window expires; do not resurrect the generation. |
| Deduplication window expired or evicted | Start a new independent generation using the new submission's policy. Earlier pending or active work can still run. |

Replacement appends an immutable envelope and atomically updates a durable generation/revision
fence. Worker acquisition freezes that fence in the same script as the consumer-group read.
Workers discard superseded staging entries and acknowledge superseded ready entries without
invoking a handler. No producer deletes a stream entry a worker may own. Superseded delayed
entries are cleaned when their old due time is visited; retained ready entries follow the
queue's normal trimming policy. The dashboard reports `Replace` and the application job ID.

Upgrade **all producers and job workers** before enabling Replace. Older producers do not
understand its deduplication record, and older workers do not enforce its acquisition fence.
Default and Extend records retain their existing formats. Replace also needs `TYPE`, `HGET`,
`HSET`, `HDEL` on the generated `:dedup-deliveries` hash and `PEXPIRETIME` on deduplication keys.
All upgraded hosted job workers pass this hash as an explicit script key, so their ACL key
patterns must permit it even for queues currently using only FirstSubmissionWins or Extend.
Workers retire lifecycle records on terminal completion or job expiry, independently of the
deduplication deadline. Protect the hash from eviction and do not expire, delete, or mutate it
while work exists: missing lifecycle state is treated as terminal to avoid delivering stale work.
Use the hosted job worker rather than reading Replace entries with a generic stream consumer.
Dashboard operator retries clear the terminal generation fence and run independently of the
original deduplication window; their displayed mode is `Separate lifecycle`.

## Transactional enqueue

Use `Enqueue(transaction, ...)` or `EnqueueScheduled(transaction, ..., options)` to queue a
job alongside business writes in an existing `RespireTransaction` or `RespireWatchedTransaction`.
Create the transaction from the **same client instance**, including its key-prefix view, as
the queue. These synchronous methods serialize immediately and return
`RespirePending<RespireJobEnqueueResult>`; read the result only after committing.

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "email", Encoding.UTF8.GetBytes);
await using var transaction = client.CreateTransaction();
var saved = transaction.Set("{respire-job:email}:order:42", "paid");
var job = queue.Enqueue(transaction, "receipt:42", "customer@example.com");
var reminder = queue.EnqueueScheduled(transaction, "reminder:42", "customer@example.com",
    new() { Delay = TimeSpan.FromDays(1) });
await transaction.CommitAsync();
_ = saved.Result;
Console.WriteLine($"Receipt {job.Result.EntryId}, reminder {reminder.Result.EntryId}");
```

No queue or deduplication writes occur before `EXEC`. Disposing without commit discards the
queued job; when WATCH detects a changed key, commit returns false and the pending results
are aborted. A successful EXEC runs the enqueue script together with the caller's writes
without other clients interleaving. A `RespireBatch` only pipelines commands and cannot
provide this guarantee.

Redis transactions **do not roll back runtime errors**. A wrong-type business write can fail
while enqueue succeeds, or enqueue can fail while another write succeeds. Reserve key types,
validate inputs, and inspect every pending result, even after commit succeeds. This API is not
an atomic transaction with an external SQL database. See the [outbox recipe](recipes.mdx#transactional-outbox-relay-over-streams).

All transaction keys must resolve to one Cluster slot. Queue keys contain
`{respire-job:email}` in this example, so give business keys the same tag, or use a shared
hash-tagged prefix such as `{tenant}:` on the client. A different queue tag cannot share the
transaction unless the resolved keys share a slot.

Enqueue uses `EVAL` with the complete existing Lua script, avoiding script-cache misses inside
EXEC. Redis 7.0+ and the usual script/queue command permissions are required. Deduplication,
delay, priority, TTL and groups keep their ordinary enqueue semantics; their clocks start at
server execution, not local queueing. Delayed jobs still need a scheduling-enabled worker.
The ambient W3C trace context is captured when queueing. Retry an ambiguous commit using the
same job IDs within the deduplication window, and make the business writes idempotent too.

## Job groups and fair scheduling

Set `RespireJobEnqueueOptions.Group` when calling `EnqueueScheduledAsync` to attach an
application group, such as a tenant ID. A zero delay makes the job eligible on the next
scheduling pass. Register the worker with a `RespireJobQueueOptions` instance to enable those
passes, as described under [delay, priority and expiry](#delay-priority-and-expiry).

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
await queue.EnqueueScheduledAsync("report:42", "build-report",
    new RespireJobEnqueueOptions { Group = "tenant:42" });
```

The handler reads the identity from `job.Group`. Null means ungrouped, including entries
created before this field existed, immediate `EnqueueAsync` jobs, flow nodes and recurring
jobs. A non-null group must be nonblank valid Unicode. Its exact text is preserved, without
trimming or case folding; it is stored in `_respire.job.group`, never interpolated into a Redis
key. It survives promotion, durable retries, consumer recovery and dead-lettering. Deduplication
remains queue-wide by job ID: changing the group does not create a second job or update the
first job's group. Include the tenant in the job ID if IDs are only unique within a tenant.

Application groups are distinct from the Redis consumer group shared by workers. Scheduling-enabled
workers rotate groups in Redis, selecting at most one job per group visit. The shared cursor and
indexes survive worker restarts; independent worker processes participate in the same rotation.
Selection, promotion and delivery into the consumer group's pending list run in one Lua script.
Grouped jobs stay staged until a reader can take them, so one group's backlog cannot flood the
ready stream ahead of a newly enqueued group. A lost read reply leaves a pending delivery for
ordinary visibility recovery rather than losing or selecting the staged job twice.

Each hosted-worker read visits one group after checking the queue-wide pause and rate-limit policy
in the same Redis script. Denied queue admission leaves staged jobs and the rotation cursor unchanged.
Groups rotate in encoded UTF-8 byte
order, starting after the persisted cursor and wrapping at the end. Empty groups leave the
rotation; a new group joins at its lexical position. Delayed groups consume a visit without
dispatching a job. The candidate budget within a visit is `SchedulingBatchSize`: select the
oldest due members in that group, then dispatch the highest priority candidate, with due time
and lexicographic staging ID breaking ties. Priority never outranks another group's turn.
An expired or missing candidate is removed and consumes that turn. Continuous higher-priority
arrivals within a candidate window can postpone a lower-priority job in the same group; use
equal priorities for deterministic due-time and staging-ID ordering within that group.

For a fixed set of **G** groups with due, live, equal-priority jobs, each group receives one
selection within at most **G** visits. With 10,000 jobs in one group and 10 in another, all 10
small-group jobs are selected within **20 visits**, regardless of the large backlog. Tests
exercise this bound with independent clients and reconnect both midway through the rotation.
This is a selection bound, not a wall-clock completion deadline: handler duration, concurrent
readers (one admission per reader), pause/rate limits, polling, retries and recovery affect starts
and finishes. Fair selection alone does not limit concurrency: configure `MaxConcurrency` below
to bound simultaneous group deliveries. Adding groups or expired/missing candidates changes the bound.

Ungrouped jobs keep their existing stream path. Immediate jobs, activated flow nodes, recurring
ticks and promoted ungrouped scheduled jobs have FIFO precedence over grouped selection.
Workers drain unread ready entries before selecting grouped jobs. A sustained ungrouped backlog
can therefore delay every application group; use a separate queue when both workloads need
independent latency guarantees. Pending replay and visibility recovery also precede new reads
and do not consume a group turn. Durable retries retain the exact group, priority and absolute
expiry, and rejoin its staged index at their new due time. Deduplication remains queue-wide.

The group format adds `:group-order` and `:group-jobs` sorted sets and a `:group-cursor` string
under the ready-stream key. Every key is explicitly supplied to Lua and shares the queue's
Cluster hash tag; even groups containing braces, separators or null characters only appear in
encoded members and envelope values. In addition to scheduling permissions, grant `ZRANGEBYLEX`,
`ZSCORE`, `ZADD`, `ZREM`, `GET` and `SET` on these indexes, plus `XREADGROUP` on the ready stream.
Protect all these keys and the staging stream from independent deletion, trimming or eviction.
Type and mutation/cleanup permission checks precede grouped writes; failed indexing, cursor
updates and retry acknowledgement compensate newly appended bodies.

**Upgrade every scheduling worker before enabling grouped producers.** Older workers cannot
read the new grouped indexes, and mixed worker versions are unsupported for grouped queues.
For queues produced by the earlier metadata-only implementation, stop producers and drain
their ready, pending and scheduled jobs before upgrading (including jobs with long delays),
or start a new queue. Existing entries in the old global due index keep their old scheduling
order; upgrading does not reindex them or retroactively provide group fairness. Retain the
new indexes when restarting workers; there is no client-local cursor to restore.

### Group concurrency, rate limits and pause

Configure limits on an application group through the typed producer. These controls apply to
the `Group` in the job envelope, not the Redis consumer group shared by workers:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
await queue.SetGroupOptionsAsync("tenant-42", new RespireJobGroupOptions
{
    MaxConcurrency = 3,
    RateLimit = new RespireJobRateLimit { PermitLimit = 10, Window = TimeSpan.FromSeconds(1) },
    LeaseDuration = TimeSpan.FromSeconds(60),
});
await queue.PauseGroupAsync("tenant-42");
await queue.ResumeGroupAsync("tenant-42");
```

Policy is stored in Redis and shared by independent clients, workers and restarts. Configure it
from one control plane; worker registration does not overwrite group policy. Null
`MaxConcurrency` and `RateLimit` mean unlimited. Passing null options removes these limits;
pause state is independent. `PauseGroupAsync` and `ResumeGroupAsync` return true only when the
state changes. Pausing blocks new deliveries and recovery admissions for that group; already
admitted handlers can finish. Other groups remain eligible. Queue-wide pause and rate admission
still apply to every group, and resuming a group does not resume its queue.

The group rate limiter uses GCRA with a burst of `PermitLimit` and replenishment at
`PermitLimit / Window`. It limits admitted delivery attempts, including recovery, across all
workers; an empty selection consumes no permit. It is separate from the queue-wide fixed-window
limiter. Each replenishment interval rounds up to a whole millisecond, so extremely high rates
are conservatively capped at one replenished permit per millisecond. Redis `TIME` controls
rate eligibility and lease deadlines, so worker clock skew does
not change the limits. Redis clock changes or failover can advance or postpone eligibility.

Concurrency permits are finite leases, renewed by a running hosted handler. `LeaseDuration`
defaults to 60 seconds and must be at least 100 milliseconds and no greater than
`Int32.MaxValue` milliseconds.
Completion, dead-lettering, retry and unsuccessful processing release their delivery's lease.
A worker crash or lost release leaves the permit available after expiry; a later worker can
recover the pending delivery. Lease ownership includes the consumer and delivery attempt, so
stale completion or repeated release cannot release a replacement attempt's permit.
If ownership or renewal is lost, the handler cancellation token is cancelled and its stale
Redis transitions are rejected. Cancellation is advisory: an application that ignores it, a
long process pause, or an external request already in progress can continue side effects after
the lease expires. Keep handlers idempotent and use application-level fencing where external
effects require stronger guarantees. The lease bounds Redis admissions, not arbitrary external
execution after ownership is lost.

Paused, rate-limited and concurrency-limited groups are parked outside the active rotation.
Workers do not repeatedly scan those groups' staged backlogs. Resume, policy changes, releases
and due rate/lease deadlines make groups eligible again through bounded scheduler passes.
Polling and other eligible work still affect admission latency. Admission limits and active
leases survive worker restarts while Redis retains the queue state.
Startup replay yields on a blocked group instead of walking its remaining pending entries;
the normal bounded recovery pass revisits pending deliveries at its configured cadence.

Group controls add `:group-policy` and `:group-owners` hashes, plus `:group-leases` and
`:group-parked` sorted sets to the ready-stream key. They use the queue's Cluster hash tag and
work through key-prefixed clients; group text is encoded in fields/members rather than Redis
keys. In addition to the grouped-scheduling permissions above, grant `HGET`, `HSET`, `HDEL`,
`TIME`, `ZRANGEBYLEX`, `ZLEXCOUNT`, `ZRANGEBYSCORE`, `ZSCORE`, `ZADD` and `ZREM` on the relevant control keys,
and the normal pending/recovery permissions on the ready stream. Protect these keys from
eviction, expiry and independent modification. Upgrade all workers before relying on group
limits; older workers do not enforce admission or lease fences.

The [queue dashboard](stream-dashboard.md#inspect-and-control-application-job-groups) provides
paginated group counts, policy inspection, and protected pause/resume actions. Applications can
also call `RespireJobGroups.ReadAsync` with the queue's `StreamKey` for bounded pages or
`RespireJobGroups.GetAsync` for a named group, including idle groups with configured policies.

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
return. `RecoveryPollInterval` controls the recovery scan cadence. Ordinary stream deliveries
do not extend this timeout; choose it longer than normal processing and keep handlers idempotent.
Grouped job deliveries additionally renew the group lease described above. Without a retry
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

## Queue-wide pause and rate limits

Pause is persistent Redis state, shared by every hosted worker for the same queue and key prefix:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "email", Encoding.UTF8.GetBytes);
await queue.PauseAsync();
var state = await queue.GetStateAsync();
await queue.SetRateLimitAsync(new RespireJobRateLimit
{
    PermitLimit = 100,
    Window = TimeSpan.FromSeconds(1),
});
await queue.ResumeAsync();
```

`PauseAsync` and `ResumeAsync` return whether the state changed. Each transition atomically
appends a `paused` or `resumed` event to `ReadEventsAsync`; repeated calls emit no extra events.
These events have an empty `JobId`, `EntryId` of `0-0`, and `Attempt` of zero. Enqueue and
scheduling can continue while paused. Already admitted deliveries finish normally; new reads,
startup replay and abandoned-job recovery all check pause atomically before claiming work.
Pause does not cancel handlers or wait for them to drain.

Job readers admit one entry at a time, regardless of the generic worker's `BatchSize`.
For recovery, `BatchSize` bounds the number of single-entry scan pages in a pass. Each page
checks pause and rate limits again; the recovery interval restarts after the bounded pass,
giving fresh jobs a read turn between passes.
Idle or paused readers check again within `SchedulingPollInterval` (one second when no queue
options are supplied), subject to Redis availability. Busy readers check on every claim.
Idle reads also honor a shorter `ReadWait`. Generic stream workers retain blocking, batched reads.

The rate policy uses fixed windows and Redis server time. All hosts share one admission budget,
including replay and recovery attempts. At most `PermitLimit` claims are admitted during a window;
bursts can occur on either side of a window boundary. This limits admissions, not handler completion
times or concurrent execution. When exhausted, workers leave jobs in place and retry after the
remaining window or the next poll, whichever is sooner. Denial never invokes a handler, changes
the entry ID, increments delivery attempts, or uses a failure retry. An uncertain Redis result
can waste a permit until the next window, preserving the upper bound.

Set `RespireJobQueueOptions.RateLimit` to initialize a shared policy at startup. Non-null startup
policies must match existing Redis configuration; a mismatch fails worker startup. Null uses the
existing policy, or unlimited admission for a new queue. Use `SetRateLimitAsync` to change a live
policy, or pass null to disable it. Updates preserve usage in the current window; a changed duration
applies when the next window starts. Update startup configuration too before restarting workers.

`GetStateAsync` returns `IsPaused` and cumulative `RateLimitHits`. Hits count denied reader
admissions, including polls while no jobs are ready, rather than distinct jobs. The dashboard shows
both values for entries configured with `IsJobQueue = true`. The `Respire.Streaming` meter exposes
`respire.job.queue.paused` (latest observed state, 0 or 1) and `respire.job.queue.rate_limit_hits`
(local confirmed denials), tagged only with `respire.worker.name`. Pause observations may remain
unchanged while a handler runs. Sum hit counters across workers; do not sum pause gauges.

## Parent and child job flows

`EnqueueFlowAsync` submits a tree of up to 256 jobs to one queue. Leaves run first; a parent
becomes runnable only after every direct child reaches a terminal state. Nested parents follow
the same rule. All nodes use the queue's existing codec and scoped handler.

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
var created = await queue.EnqueueFlowAsync("report:2026-10-15",
    new RespireJobFlow<string>("combine", "build-report")
    {
        Children = [new("region-a", "fetch-a"), new("region-b", "fetch-b")],
    }, RespireJobFlowFailurePolicy.Continue);
var snapshots = await queue.GetFlowResultsAsync("report:2026-10-15");
```

Inside the handler, `await job.GetChildResultsAsync(cancellationToken)` returns direct-child
snapshots with `Id`, `Status`, and an optional owned `byte[] Result`. Return
`job.Complete(resultBytes)` to acknowledge successfully and retain a binary result. Returning
ordinary `Ack` succeeds with a null result; an empty byte array is distinct from null.
The application chooses its result codec. Ordinary jobs do not retain results.

The failure policy applies throughout the tree, after all siblings finish:

| Policy | Parent behavior after a child fails |
| --- | --- |
| `FailParent` (default) | Fail without calling the handler; propagate that failure to its parent. |
| `Ignore` | Run the handler with failed children omitted from child results. |
| `Continue` | Run the handler with failed children included with `Status == "failed"` and a null result. |

`Nack` and exceptions remain retryable until the worker's delivery limit; they do not finish
a child early. Visibility recovery and configured durable retry backoff preserve flow membership.
Flows enqueue immediately and do not expose per-node delay, priority, or TTL options.

Child results, parent activation, completion events, and acknowledgement commit in one fenced
Redis script. A crash before completion leaves the child recoverable; a crash or lost reply
after completion leaves the parent queued. A stale attempt cannot replace results or activate
a parent twice. Handlers must still make external side effects idempotent.

`GetFlowResultsAsync` returns every node in breadth-first tree order, including failed nodes
under `Ignore`, or an empty list for an unknown flow. Status is `waiting`, `ready`, `completed`,
or `failed`; `ready` includes pending deliveries and retries. Completion events include `FlowKey`
to distinguish identical job IDs in different flows. A parent failed without execution publishes
a `dead-letter` event with reason `child-failed`, entry ID `0-0`, and attempt zero; it has no
delivery to copy to the dead-letter stream.
`ReadEventsAsync` parses these sentinel values normally. A failed root publishes the same
terminal event, so consumers can wait for its job ID and `FlowKey`, or inspect the root snapshot.

Flow IDs have permanent deduplication, independent of ordinary job enqueue IDs. Retry an ambiguous
enqueue using the same flow ID: the first tree wins and later calls return `false`. Job IDs must
be unique within each tree. Cycles, shared children, and cross-queue trees are not supported.
Use a new flow ID for a new run and include that identity in application job IDs when needed
for external idempotency. Each result is limited to **4 KiB**, and combined UTF-8 job IDs and
serialized payloads are limited to **256 KiB** per flow. Store larger data externally and return
a reference. Each completion rewrites the retained tree, bounded to 1 MiB of results plus
256 KiB of input and coordination metadata. `Complete` rejects oversized results before copying;
the completion script also checks the limit before changing Redis state.

Flow payloads and results do not expire automatically. After a flow is terminal and its results
are no longer needed, call `await queue.RemoveFlowAsync(flowId)`. This atomically verifies that
every node is completed or failed and deletes the flow state and flow-ID reservation. It returns
`false` for an absent flow and throws `RespireServerException` for an active flow. Queue entries
and events keep their existing retention policy. A removed ID can be enqueued again, so use a
new ID when consumers must distinguish historical runs. Do not delete, evict, or alter active
flow state directly. Flow keys share the queue's Cluster slot and respect client key prefixes.
The stored schema is versioned; reads, duplicate enqueues, completions, and removal reject an
unknown version explicitly without mutating it.

Flows require Redis 7.0 or later. On an older server, the completion script fails during its
permission preflight, before any Redis mutation or acknowledgement. Its delivery remains
pending and recoverable, but cannot finish until Redis is upgraded; do not downgrade a server
with active flows. In addition to the ordinary queue commands, flows require `GET`, `SET`,
`TYPE`, and (for removal) `DEL` on flow keys
and `XADD`/`XDEL` on the ready stream for activation and rollback.

## Batches and completion callbacks

Use a **batch** for a flat set of independent jobs with aggregate progress and callbacks.
Use a **flow** for a dependency tree where each parent consumes its children's results.
Batch members can finish while membership is still open; callbacks become eligible only
after you seal the batch and every member reaches a terminal outcome.

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
// Use a stable ID so an ambiguous create/add/seal can be retried safely.
var batchId = await queue.CreateBatchAsync(new RespireJobBatchOptions<string>
{
    OnSuccess = new("report-success", "notify-success:report-42"),
    OnFailure = new("report-failure", "notify-failure:report-42"),
    OnComplete = new("report-finished", "record-finish:report-42"),
}, batchId: "report-42");
await queue.AddBatchJobAsync(batchId, "part-1", "process-part-1");
await queue.AddBatchJobAsync(batchId, "part-2", "process-part-2");
await queue.SealBatchAsync(batchId);

var status = await queue.GetBatchStatusAsync(batchId);
Console.WriteLine($"{status!.Succeeded} succeeded; {status.Pending} pending");
```

`CreateBatchAsync` returns the supplied ID or generates one when omitted. Reusing an ID
preserves the first callbacks. Member IDs are unique within a batch, independently of
ordinary enqueue deduplication; adding an existing ID returns `false` and preserves its
payload, even after sealing. Adding a new ID to a sealed batch fails. Each batch supports
**256 members** and **256 KiB** of combined member/callback IDs and serialized payloads.
Members enqueue immediately. Per-member scheduling, groups, priority, TTL, and flow
dependencies are not available; configured worker retry backoff still applies.

The status snapshot exposes `Pending`, `Succeeded`, `Failed`, `Cancelled`, `IsSealed`,
`CancellationRequested`, and `IsCompleted`; an unknown ID returns `null`. Retried attempts
remain pending. A job that fails an attempt and later succeeds contributes one success;
only exhausted retries or explicit dead-letter completion contributes a failure.
Terminal member events include `BatchKey` and use `completed`, `dead-letter`, or `cancelled`.

Dashboard retries of dead-lettered members run as independent jobs with the original job ID
and payload. They do not reopen the batch, change its terminal counts, or publish its callbacks
again, and can run after the completed batch state has been removed.

When a sealed batch has no pending members, Redis atomically publishes `OnSuccess` if
all members succeeded, or `OnFailure` if any member failed or cancellation was requested.
It also publishes `OnComplete`. An empty sealed batch succeeds. Each eligible callback
is **enqueued once**, including concurrent final completions and repeated completion
requests. The completion script commits counts, callback publication, the terminal event,
and acknowledgement together; a rejected acknowledgement restores the previous state
and removes the new callbacks/events. This requires exclusive use of the generated keys
and intact Redis data, just like flows.

Callbacks use the same queue, serializer, and handler as members. They are ordinary jobs
and do not contribute to batch counts. `OnComplete` does not wait for the other callback
to execute; workers may process callbacks concurrently. **Callback execution and external
side effects are not exactly once.** Handlers must tolerate redelivery and use an
application idempotency key for external effects. Include the batch identity in callback
payloads when needed. `IsCompleted` means callbacks have been published, not that they
have finished executing.

Call `await queue.CancelBatchAsync(batchId)` to seal membership and request cancellation.
Workers check cancellation before invoking a member's handler and acknowledge skipped
members as cancelled. Already running handlers are not interrupted and can still produce
side effects; any member completing after the cancellation request counts as cancelled.
Earlier successes/failures retain their counts. Workers must drain remaining members,
including delayed retries, before failure/completion callbacks run. Cancellation does not
cancel callback jobs and leaves an already completed batch unchanged. Seal and cancel
are idempotent; unknown IDs fail.

Batch state has no automatic expiry. Once completed and no longer needed, call
`await queue.RemoveBatchAsync(batchId)` to delete its state and ID reservation. Active
batches cannot be removed. Stream entries, callbacks, and events have independent retention;
removing state does not undo callback publication. Reusing a removed ID starts a new run.
Do not evict, edit, or expire active batch state or trim unprocessed entries. Batch keys
share the queue's Cluster slot and respect client key prefixes. Use batch-aware workers
throughout the queue before adding batch members. Redis 7.0+ and `GET`, `SET`, `TYPE`,
`XADD`, `XDEL`, `XACK` (plus `DEL` for removal) permissions are required.

Dashboard batch presentation is tracked separately in
[#1845](https://github.com/thomhurst/Respire/issues/1845); use `GetBatchStatusAsync` for progress.

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

For ungrouped scheduled jobs, each worker reader promotes at most `SchedulingBatchSize` due jobs (default 64, maximum 256) before reading or recovering
work, at most once per `SchedulingPollInterval`. Redis `TIME` determines due times and expiry; application clocks are not used. Clock jumps
on the Redis server or after failover can advance or postpone eligibility. Delay is a minimum,
not an execution deadline. Busy handlers, polling, ready-stream backlog and recovery can add latency.
Scheduling pauses when there are no active job workers and resumes on restart. A blocking reader
wakes at least every `SchedulingPollInterval` (default one second); handlers are never interrupted
to run a scheduling pass. An index entry whose staged body was deleted is removed and skipped
so it cannot block later jobs; the missing payload cannot be recovered.

The ungrouped scheduler selects the oldest due jobs first (equal due times use Redis lexicographic staging
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

## Resumable jobs and checkpoints

Set `RespireJobEnqueueOptions.MaximumRuntime` on `EnqueueScheduledAsync` (or transactional
`EnqueueScheduled`) to opt a job into checkpointed processing. Register **every worker for
that queue** with non-null `RespireJobQueueOptions`, as for delayed jobs. For example:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "imports", Encoding.UTF8.GetBytes);
await queue.EnqueueScheduledAsync("large-import", "source.csv", new RespireJobEnqueueOptions
{
    MaximumRuntime = TimeSpan.FromSeconds(30),
});
```

In the handler, call `await job.ReadCheckpointAsync(token)` to restore the last cursor and
state; it returns null before the first save. After each completed unit of work, await
`job.SaveCheckpointAsync(nextCursor, stateBytes, percent, token)`. A checkpoint stores a
cursor of at most 1024 UTF-8 bytes, at most 4096 binary state bytes, and optional progress
from 0 to 100. State is returned as owned `ReadOnlyMemory<byte>`. A save returning false
means that this delivery expired or lost ownership; stop doing work for that delivery.

The runtime cap cancels the handler token. Observe it between units of work and in awaited
operations. Let an `OperationCanceledException` propagate after any cancellation cleanup;
when the handler token is cancelled, this tells the worker to stage another run without
spending the failure budget. The next run reads the last saved checkpoint. Returning an
explicit result after runtime expiry keeps its normal meaning: `Ack` completes the job and
deletes its checkpoint, `Nack` spends the failure budget, `RetryAfter(...)` retains its retry
delay and spends the failure budget, and `DeadLetter` dead-letters the job. Other exceptions
still count as failures, while
worker crashes still count as abandoned deliveries under the configured delivery limit.
The cap is cooperative: a handler that ignores cancellation cannot be forcibly stopped.

Graceful shutdown also cancels resumable handlers immediately. If a final checkpoint is
needed, save it using a **separate bounded cancellation token** in the handler's cleanup,
and await the save before rethrowing the cancellation exception. The worker then requeues
with a bounded cleanup token. Returning a result during shutdown does not request this
budget-free yield; if processing is already cancelled, the delivery remains pending for recovery.
If the process is killed before requeueing, visibility-timeout recovery on another worker
reads the last successful save. Set the visibility timeout above the longest runtime slice
plus cleanup time, or use a group concurrency lease, to avoid overlapping live runs.

Checkpoint writes check the pending entry's consumer and delivery count, and the group lease
when present. Each enqueue has its own checkpoint identity; reusing an application job ID
after deduplication expires starts fresh. Acknowledging success deletes the checkpoint
atomically with completion. A job's TTL also expires its checkpoint. Without a TTL, pending
and dead-letter checkpoints remain until successful completion or explicit operator cleanup;
include `:checkpoint:*` keys when removing an abandoned queue. Dashboard dead-letter retry
retains any checkpoint that has not expired. Manually deleting stream entries does not delete
their checkpoints.

The dashboard shows the cursor and optional percentage for its bounded page of pending jobs,
without showing application state. `RespireJobCheckpoints.ReadAsync(client, stream, entryId)`
provides the same inspection for a retained ready-stream entry. Checkpoint operations need
GET/SET on the queue's `:checkpoint:*` keys, and successful completion additionally needs DEL.
All keys stay in the queue's Cluster slot and honor client key prefixes.

Checkpointing does not make external effects exactly once. Make each unit idempotent: a
crash between an external effect and saving its cursor can repeat that unit. Redis persistence
and replication settings determine checkpoint durability across Redis failures.

## Recurring and cron jobs

Create a scheduler from a typed producer to manage durable recurring schedules:

```csharp
using System.Text;
using Respire;
using Respire.Streaming;

await using var client = await RespireClient.ConnectAsync("redis://localhost:6379");
var queue = new RespireJobQueue<string>(client, "email", Encoding.UTF8.GetBytes);
var scheduler = queue.CreateScheduler();
await scheduler.UpsertAsync("hourly-reminder", "customer@example.com", new RespireJobSchedule
{
    Interval = TimeSpan.FromHours(1),
    MissedTickPolicy = RespireJobMissedTickPolicy.CatchUp,
});
await scheduler.UpsertAsync("weekday-summary", "reports@example.com", new RespireJobSchedule
{
    Cron = "0 9 * * MON-FRI",
});

// Run periodically when managing the scheduler yourself.
var enqueued = await scheduler.RunDueAsync(maximumTicks: 64);
await scheduler.RemoveAsync("hourly-reminder");
```

Alternatively, call `services.AddRespireJobScheduler<string>("email")` after registering the
matching `AddRespireJobQueue<EmailHandler, string>("email", ...)`. This adds a hosted scheduler
with a one-second polling interval and a 64-candidate pass limit, plus a keyed
`RespireJobScheduler<string>` resolved with `GetRequiredKeyedService` using the queue name.
The optional `pollInterval` and `maximumTicksPerPass` arguments configure those bounds; the
maximum is 256. A failed pass is logged and retried after the interval. Register a `TimeProvider`
to control the hosted scheduler's UTC clock and timers, or pass one to `CreateScheduler` for
manual scheduling and deterministic tests. Queue consumers run independently of the scheduler.

Specify exactly one of `Interval` or `Cron`. Intervals are positive whole milliseconds and are
anchored at upsert time. Cron uses [Cronos](https://github.com/HangfireIO/Cronos)'s five-field
format (minute, hour, day of month, month, day of week), evaluated in UTC; local time zones and
six-field expressions are not accepted. The first occurrence is strictly after upsert time.
An expression with no future occurrence is rejected. If a schedule reaches its last representable
occurrence, its definition remains removable but no further ticks are due.

`Skip` (the default) coalesces missed occurrences into the latest tick at or before the pass's
clock snapshot. `CatchUp` enqueues each overdue tick in order, carrying remaining backlog to
later bounded passes. Passes visit schedule IDs in round-robin order using a shared Redis cursor,
so a sustained catch-up backlog cannot starve other schedules, even with a one-candidate pass.
The candidate budget includes schedules that are not yet due or have no future occurrences;
large schedule sets can require several passes to visit every schedule. The cursor survives
restarts and removal of its last visited schedule. Neither mode shifts an interval's cadence when a pass runs late. Scheduling
pauses when no scheduler is running and resumes from Redis state on restart. Polling, queue backlog,
and worker availability affect execution time; a tick is an enqueue opportunity, not an execution deadline.

Every upsert creates a new revision, replaces the stored payload and policy, and resets the next
occurrence. Manage schedules through one control plane; do not upsert them on every worker startup.
Removal prevents further commits for that schedule and returns whether a definition existed.
Replacement and removal do not retract jobs already enqueued. A snapshot read before an upsert or
removal cannot subsequently commit against the new state.

Many instances may run scheduling passes. A Redis script atomically appends a tick to the existing
ready stream and advances its persisted due time. Only one instance can commit a given revision
and tick, including when a successful reply is lost or a process restarts. There is no expiring
deduplication window for these transitions. Jobs use IDs of the form `recurring:<revision>:<unix-ms>`;
reserve that prefix for the scheduler. Stream entries also carry `_respire.job.schedule` and
`_respire.job.tick`. Existing typed handlers, acknowledgement, retries, and job events apply.
Each tick uses the existing `job enqueue` producer span and propagates its W3C context (or the
ambient context when no producer span is sampled) to the worker's processing span.
Recurring jobs are immediate ready jobs; they do not carry delayed-enqueue priority or TTL options.
One enqueue per tick does **not** make handler side effects exactly once: handlers must still tolerate redelivery.

Unlike delayed-job promotion, recurring scheduling uses the application's `TimeProvider` UTC time.
Synchronize clocks across instances. A fast clock can enqueue early, a slow clock can delay a pass,
and a forward jump applies the missed-tick policy. Backward jumps do not repeat committed ticks.
The guarantee depends on Redis retaining both the stream and schedule state through persistence
and failover; protect these keys from eviction, expiry, trimming, and independent modification.

Recurring scheduling adds `:recurring` (definition hash), `:recurring-due` (sorted set),
`:recurring-order` (schedule ID sorted set) and `:recurring-cursor` (string) to the
ready-stream key. They share its Cluster slot, including through prefixed clients. In addition to
script execution, allow `TYPE`, `HGET`, `HSET`, `HDEL`, `ZRANGEBYLEX`, `ZSCORE`, `ZADD`, `ZREM`, `GET`, `SET`,
and `XADD`/`XDEL` on the appropriate keys. Scripts check key types and write/cleanup permissions
before mutations and roll back an appended job if advancing the due index fails. Redis 7.0 or
later is required. No recurring-scheduler permissions are needed by immediate-only producers.

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

## Per-job logs

Inside a hosted handler, await `job.LogAsync("Started sending email", cancellationToken)`
to append a line. It returns `false` when this delivery is no longer current or the job's
explicit TTL has elapsed. Logging uses the same consumer and delivery-attempt fence as
progress; a recovered handler cannot append through an older delivery. Await all log calls
before returning from the handler. Logging is explicit: a job that never calls `LogAsync`
adds no Redis command. Each append runs one script (plus script loading on a cache miss).

Set `MaximumLogLines` (default 100, range 1–1000), `MaximumLogLineBytes` (default 4096,
range 1–16384), and `LogRetention` (default one day, positive and at most
`Int32.MaxValue` milliseconds) on `RespireJobQueueOptions`, passed as `queueOptions`
to `AddRespireJobQueue`. All workers for a queue must use the same policy. The overload
without queue options uses these defaults. Invalid Unicode and lines exceeding the UTF-8
byte limit throw before sending a command; lines are never silently truncated.

Logs are a capped Redis list per application job ID. Appends remove the oldest excess lines.
The first append sets a finite expiry, shortened to the job's expiry when it has a TTL;
subsequent appends never extend it. Completion does not extend retention. Once a list has
expired, a still-live delivery can begin a new list. This bounds each list's size and lifetime;
total storage depends on how many distinct jobs log within the retention window.

Call `await queue.GetLogsAsync(jobId, count: 100, cancellationToken: cancellationToken)`
to read the newest retained lines in append order. The count must be 1–1000; missing or
expired logs return an empty array. Logs share the application job ID across delivery
attempts and durable retries. Reusing an ID before expiry also shares its old history, so
use unique IDs for distinct logical jobs. A lost append reply can leave a duplicate line
when retried. Logs are diagnostic history, not an exactly-once audit trail.

The [dashboard](stream-dashboard.md#inspect-job-logs) displays these lines for configured
job queues. Log only information operators should see; exception messages and payloads
are not logged automatically.

Log keys append `:logs:<SHA256-of-job-ID>` to the ready stream key, preserving its Cluster
slot and client key prefix. Writers need `XPENDING`, `XRANGE`, `TIME`, `PEXPIRETIME`,
`RPUSH`, `LTRIM`, `PEXPIREAT`, and script execution. Write permissions are checked before
appending. Readers need `LRANGE` on the log keys. Reserve these keys for the queue.

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

## Bounded completion metrics

Set `MetricsRetentionMinutes` in `RespireJobQueueOptions` to retain one-minute counters:

<!-- doc-test-tail-declaration: split-before=public sealed class OrderHandler -->
```csharp
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Respire;
using Respire.DependencyInjection;
using Respire.Streaming;

var builder = Host.CreateApplicationBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddRespireJobQueue<OrderHandler, string>("orders",
    Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
    new RespireStreamWorkerOptions { DeliveryLimit = 5 },
    new RespireJobQueueOptions { MetricsRetentionMinutes = 60 });

using var host = builder.Build();
var client = host.Services.GetRequiredService<IRespireClient>();
var queue = host.Services.GetRequiredKeyedService<RespireJobQueue<string>>("orders");
var history = await RespireJobMetrics.ReadAsync(client, queue.StreamKey, minutes: 60);
await host.RunAsync();

public sealed class OrderHandler : IRespireStreamHandler<RespireJob<string>>
{
    public ValueTask<RespireStreamWorkerResult> HandleAsync(
        RespireJob<string> job, CancellationToken cancellationToken)
    {
        // Perform an idempotent application operation using job.Id before acknowledging.
        return ValueTask.FromResult(RespireStreamWorkerResult.Ack);
    }
}
```

The default, `0`, disables recording. Values from 1 to 1440 retain that many UTC minutes,
including the current partial minute, using the Redis server clock. No TimeSeries module is
needed. Each queue uses one hash at its source key plus `:metrics`, with at most one field per
retained minute plus a retention field. Slots are reused, and the whole hash expires when the
last recorded minute leaves the window, even when the queue is idle. Reads return oldest first,
are bounded to 1440 buckets, and filter out expired slots. A missing hash returns an empty list.

`Completed` counts acknowledged deliveries. `Failed` counts terminal dead letters, including
explicit rejection and exhausted retries. Intermediate handler exceptions and retry attempts,
expired jobs, cancelled batch members, and flow parents failed without being delivered are excluded. An operator retry
creates a new delivery and may contribute a later completion. Empty minutes show zero; this also
includes minutes before recording began and gaps while metrics were disabled. Retained history
is shared across all workers for the queue, not partitioned by application job ID or job group.

Counters commit in the same fenced Lua operation as the terminal event and acknowledgement.
Stale consumers and replay after a lost reply do not increment them again. Enabled and disabled
metrics add **zero extra per-job commands**; enabling metrics adds server-side work to completion.
Storage/type/ACL errors leave the delivery pending rather than silently omitting its counter.
Use the `run-job-metrics-benchmarks` PR label for the focused net10 performance comparison and
command-count audit.

All enabled workers must agree on retention. To change it, stop the queue's workers and delete
only its `:metrics` key, or wait for that key to expire, then restart with the new retention.
Disabling metrics leaves existing history to expire naturally. Enable recording on all workers
to avoid gaps; Redis persistence and eviction still govern whether history survives server loss.
Alongside existing worker permissions, allow `TIME`, `HGET`, `HSET`, `HDEL`, `PTTL`, `PEXPIREAT`,
`PERSIST` and `DEL` on the metrics hash (the last three support expiry and rollback).
History readers need script execution, `TIME` and `HGET`.

## Storage and delivery guarantees

Queue names must be nonblank and cannot contain braces. The generated ready stream is
`{respire-job:<name>}:ready`; its default dead-letter stream appends `:dead`, and deduplication
keys append `:dedup:<SHA256-of-job-ID>`. The staging stream appends `:scheduled`, and its due-time
sorted set appends `:due`. The event stream appends `:events`; the persistent control hash appends
`:control` (`queue.ControlKey`), and Replace lifecycle tracking appends `:dedup-deliveries`.
The shared hash tag keeps the atomic operations in one
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
All job workers need `HGET` on the control hash, even with no rate policy. Rate-limited workers
also need `TIME`, `HSET` and `HINCRBY`; policy initialization/updates need `HGET`/`HSET`.
Pause/resume needs `HGET`, `HSET`, `HDEL` on the control hash and `XADD` on the event stream;
failed event appends restore the previous pause state. State inspection needs `HMGET`.
Protect the control hash from eviction and do not expire it: losing it removes pause and rate
policy. Upgrade all workers before relying on controls; older versions do not check admission.
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
