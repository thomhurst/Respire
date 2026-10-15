---
title: Queue and stream dashboard
description: Inspect stream workers and reliable job queues with an authorized ASP.NET Core dashboard.
---

# Queue and stream dashboard

`Respire.Streaming.Dashboard` provides an ASP.NET Core operator page for
[stream workers](stream-workers.md) and [typed job queues](job-queues.md). It shows configured
streams, consumer groups, pending deliveries, dead letters, and a sampled delivery rate.
Operators can retry or permanently delete individual dead letters.

Install `Respire.Streaming.Dashboard` and `Respire.DependencyInjection` in an ASP.NET Core app.
The dashboard supports .NET 8 and .NET 10 and requires Redis 7 or later for retry permission
checks. Upgrade it together with the matching `Respire` package version.

## Map an authorized dashboard

Use the application's existing authentication scheme and a named policy that restricts
access to operators. The example uses cookie authentication; connect its login flow to your
identity provider. The dashboard does not provide sign-in or assign roles.

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.Streaming.Dashboard;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("redis://localhost:6379");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();
builder.Services.AddAuthorization(options => options.AddPolicy("QueueOperators",
    policy => policy.RequireAuthenticatedUser().RequireRole("QueueOperator")));
builder.Services.AddRespireStreamingDashboard(options =>
{
    options.EntryLimit = 50;
    options.Streams.Add(new()
    {
        Name = "Order fulfillment",
        Stream = "{orders}:ready",
        Group = "fulfillment",
        DeadLetterStream = "{orders}:dead",
    });
    options.Streams.Add(new()
    {
        Name = "Email jobs",
        Stream = "{respire-job:email}:ready",
        Group = "respire-jobs",
        DeadLetterStream = "{respire-job:email}:ready:dead",
        IsJobQueue = true,
    });
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapRespireStreamingDashboard("/workers", "QueueOperators");
app.Run();
```

Open `/workers` after signing in as an operator. A nonblank authorization policy is required
when mapping the dashboard, and every page and action uses it. Register that policy in
ASP.NET Core authorization; a missing policy does not grant access. Keep authentication and
authorization middleware in the pipeline as shown. Do not apply `AllowAnonymous` to the
dashboard's endpoints or a parent route group.

`AddRespireStreamingDashboard` registers antiforgery services. The page issues tokens and
each POST validates them before any mutation. GET requests never retry or delete work.
The page uses no external scripts, fonts, or styles, escapes displayed values, prevents
framing, and sends `Cache-Control: no-store`. Serve the application over HTTPS.

## Configure the streams you operate

The dashboard takes an explicit allowlist of 1–100 stream/group registrations. It does not
scan the database, accept Redis keys from requests, or automatically register hosted workers.
This allows a separate operator application to inspect workers running in other processes.
It uses the application's `IRespireClient`, including any key prefix on that client.

For a reliable queue, use its `StreamKey`, `DeadLetterStreamKey`, and
`RespireJobQueue<T>.ConsumerGroup` values. The example uses the default keys for queue `email`.
If worker options override the dead-letter destination, configure that same destination here.
Set `IsJobQueue = true` to enable the job-specific retry semantics below. Leave it false for
plain streams, including typed stream handlers.

The source and dead-letter keys must resolve to distinct keys in the same Cluster slot.
Reserve each dead-letter stream for exactly one source stream and consumer group. The worker
envelope records a source entry ID and group, but not the source key; sharing a dead-letter
destination across sources cannot be safely distinguished. Configuration rejects duplicate
dead-letter destinations, source/dead-letter overlap, and group names exceeding the worker's
256-byte dead-letter metadata limit. Omit `DeadLetterStream` for inspection without actions.

## Read the snapshot

- **Retained entries** is the source stream length, including acknowledged bodies that have
  not been trimmed. It is not the count of unfinished jobs.
- **Consumer groups** lists each group's consumers, pending count, lag, and last delivered ID.
  Redis may omit lag and entries-read information; unavailable values appear as a dash.
- **Pending deliveries** shows entry IDs, consumers, idle time, and delivery counts for the
  configured group. Viewing the dashboard does not claim or acknowledge entries.
- **Dead letters** shows retained failed entries with expandable field previews. Previews
  decode UTF-8, showing at most 32 fields and 512 bytes per value; binary data may display
  replacement characters. Retry uses the original Redis bytes, not the preview.
- **Deliveries / second** measures the configured group's `entries-read` delta between page
  requests on this application instance, with at least five seconds between samples. Refresh
  after five seconds to obtain a sample; quicker requests retain the last rate. It measures new
  deliveries, not successful processing, retry attempts, or historical throughput. Counter
  decreases, missing metadata, and the first request produce no rate. For completion rates and
  historical charts, export the [worker metrics](stream-workers.md).

Pending and dead-letter lists show the oldest entries first. `EntryLimit` bounds each list
to 1–200 entries per stream (default 50). There is no historical paging; retrying or deleting
old dead letters reveals the next entries. Read snapshots are shared for two seconds across
viewers, with up to four streams read concurrently. Dashboard mutations invalidate the cached
snapshot. Counts are separate reads, not a transactional snapshot. If a source stream is
unavailable, its retained dead letters can still be inspected and deleted; retry requires the
source consumer group. Large field values still have to be retrieved from Redis before preview truncation.
Delayed jobs in scheduling storage and retained completion events are outside this view.

## Retry and delete

**Retry entry** atomically appends the original fields to the source stream with a new entry
ID and removes that dead letter. It validates the worker envelope and configured group,
checks the target group exists, and checks write permissions before appending. Concurrent
retries of the same dead letter append at most once. Repeating a request after the original
action committed finds no entry; refresh the page to see the current state.

For plain streams, retry removes only the five leading dead-letter metadata fields and
preserves all original field names and values, including binary data and duplicate names.
For job queues, it also resets `_respire.job.attempts` to zero and clears
`_respire.job.expires`. This explicitly gives the job fresh delivery attempts without its old
expiry. The job ID, payload, priority, and trace fields are preserved. Retry enters the ready
stream immediately, bypassing enqueue deduplication and delayed scheduling. Handlers must
remain idempotent. Every consumer group on the source can receive the new entry.

**Delete entry…** reveals a confirmation before permanent deletion. It removes only the
dead-letter entry, leaving source history, enqueue deduplication, and completion events
unchanged. Both actions reject entries with an invalid envelope or a different group.
No action trims unrelated entries or acknowledges a pending delivery.

The Redis identity needs `XLEN`, `XINFO GROUPS`, `XPENDING`, and `XRANGE` for inspection.
Actions additionally need script execution (`EVAL`/`EVALSHA` and script loading), `XDEL` on
the dead-letter stream, and, for retry, `XADD` and `XDEL` on the source stream. Source `XDEL`
allows rollback if removal fails. A denied write leaves the dead letter available for a
later attempt. A Redis failure returns HTTP 503; invalid/stale entries return 409/404.
The dashboard never exposes raw Redis error messages in the page.
