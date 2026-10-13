---
title: Output caching
description: Store ASP.NET Core output-cache responses in Redis, with Microsoft store interoperability or generation-aware tag eviction.
---

# Output caching

`Respire.OutputCaching` stores ASP.NET Core output-cache responses in Redis, so several
application instances share cached responses and evict them by tag. It implements
`IOutputCacheStore` and `IOutputCacheBufferStore` on .NET 8 and .NET 10.

The default format matches Microsoft's `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`
store, so you can switch without flushing. An optional generation-aware mode protects
replacement values from obsolete tag memberships.

## Install

```bash
dotnet add package Respire.OutputCaching
dotnet add package Respire.DependencyInjection
```

## Setup

Register an `IRespireClient`, then call `AddRespireOutputCache` before building the
application:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.OutputCaching;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("localhost:6379");
builder.Services.AddRespireOutputCache(options => options.InstanceName = "my-app:");
var app = builder.Build();
app.UseOutputCache();
app.MapGet("/products", () => "Cached response")
    .CacheOutput(policy => policy.Expire(TimeSpan.FromMinutes(1)).Tag("products"));
app.Run();
```

- Registration also adds the standard output-cache services and a hosted service that removes
  expired tag references.
- For a keyed or other specific client, pass a factory:
  `AddRespireOutputCache(configure, provider => provider.GetRequiredKeyedService<IRespireClient>("cache"))`.
- With .NET Aspire, use the [`Aspire.Respire.OutputCaching`](aspire.md#cache-companions)
  companion instead.
- To invalidate a tag, resolve `IOutputCacheStore` and call
  `EvictByTagAsync("products", cancellationToken)`.

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `InstanceName` | `null` | Key prefix. Matches the Microsoft store's `InstanceName`. |
| `TaggingMode` | `MicrosoftCompatible` | `GenerationAware` uses a separate format. See [generation-aware tagging](#generation-aware-tagging). |
| `CleanupInterval` | 5 minutes | Hosted cleanup period. Must be at least 1 ms and fit a timer interval. |
| `TimeProvider` | `TimeProvider.System` | Clock for tag scores, absolute expiry, and the cleanup timer. |

Invalid options fail host startup validation, as does `GenerationAware` without a nonempty
Redis hash tag in `InstanceName`.

## Entry behavior

- Values have Redis TTLs. Reads do not extend them.
- Lifetimes under one millisecond throw `ArgumentOutOfRangeException`.
- Empty payloads are hits, distinct from missing entries.
- The store borrows its client. Disposing the service provider does not dispose an externally
  registered client instance.
- A client `WithKeyPrefix` changes the physical namespace. Account for it when sharing data
  with a Microsoft store.

## Switching from Microsoft's Redis store

Keep the same Redis database, `InstanceName`, and output-cache policies. No flush or
conversion is needed. Either store can read and evict the other's entries. The format matches
[`Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Middleware/Microsoft.AspNetCore.OutputCaching.StackExchangeRedis/src/RedisOutputCacheStore.cs):

| Key suffix after `InstanceName` | Redis type | Contents |
| --- | --- | --- |
| `__MSOCV_` + cache key | String with TTL | Unchanged output-cache payload |
| `__MSOCT_` + tag | Sorted set | Cache keys scored by Unix-millisecond expiration |
| `__MSOCT` | Sorted set | Tags scored by their latest expiration |
| `__MSOCTGC` | Expiring string | Shared cleanup lock |

### Tag consistency in the default mode

Tag changes and eviction are not transactional, as in the Microsoft store.

- All tags are registered before the value is published. A failed or cancelled registration
  is reported and does not publish the response. Partial metadata expires through cleanup.
- Tagged writes use Redis 6.2+ [`SET PXAT`](https://redis.io/docs/latest/commands/set/) with
  the same absolute deadline as their tag scores, so a value cannot outlive its references.
  Tag registration time counts toward the lifetime. Untagged writes use relative TTLs.
- Each tag and each key within a tag keeps its longest recorded deadline, so a shorter
  concurrent write cannot get the latest value's membership cleaned up early. Metadata can
  therefore outlive a shorter replacement. Microsoft writers can still shorten scores, so
  this holds only when all writers use Respire.
- A concurrent eviction can race a set. A replacement written between eviction's `DEL` and
  `ZREM` survives but loses that tag membership, so a later eviction by that tag misses it.
- Overwriting a key with different tags keeps its old memberships, so evicting an old tag can
  remove the replacement. Use stable tags per cache key, or put the policy or tag generation
  in the key. The shared layout has no key-to-tags index.

Use [generation-aware tagging](#generation-aware-tagging) when obsolete memberships must not
delete a replacement and you do not need Microsoft store interoperability.

Keep application and Redis clocks synchronized: tagged values and tag scores share an
application UTC deadline.

## Buffers and cleanup

- In `MicrosoftCompatible` mode, buffer reads copy from a pooled lease into the supplied
  `PipeWriter` and flush without completing it.
- Multi-segment writes stream without flattening the sequence. Keep the sequence memory
  unchanged until `SetAsync` completes.
- Cleanup removes expired sorted-set references, not values. In `MicrosoftCompatible` mode it
  shares the Microsoft store's cleanup lock. Errors are logged and retried next period. If the
  lock is lost, the pass logs at debug level and stops.
- Tag eviction works in groups of up to 250 members: separate pipelined `DEL` commands (so
  values in different Cluster slots work), then one `ZREM` after every deletion succeeds.
- Tag registration pipelines groups of up to 125 tags before publishing the value.

If you construct `RespireOutputCacheStore` directly without a host, schedule
`CollectExpiredTagsAsync` yourself, or expired metadata accumulates. The constructor accepts an
optional logger.

### Redis permissions

The store needs string reads, writes, and deletes, sorted-set operations, and scripting.
Cleanup-lock renewal also needs `CLIENT ID` and `CLIENT KILL`. Hosted cleanup renews the lock
after every 250 scanned tags; grant these permissions even if a small dataset never reaches
that.

## Generation-aware tagging

Set `TaggingMode = RespireOutputCacheTaggingMode.GenerationAware` to protect newer values from
obsolete tag memberships. Requires Redis 7+ and an `InstanceName` containing a nonempty Redis
hash tag, even on standalone Redis:

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire.DependencyInjection;
using Respire.OutputCaching;

var builder = WebApplication.CreateBuilder();
builder.Services.AddRespire("localhost:6379");
builder.Services.AddRespireOutputCache(options =>
{
    options.InstanceName = "{my-app-output}:";
    options.TaggingMode = RespireOutputCacheTaggingMode.GenerationAware;
});
```

| Key suffix | Redis type | Contents |
| --- | --- | --- |
| `__RPOCV2_` + cache key | Hash with TTL | `payload`: unchanged bytes; `generation`: 32-character GUID |
| `__RPOCT2_` + tag | Sorted set | `generation:cache-key` members, scored by expiration |
| `__RPOCT2` | Sorted set | Tags scored by their longest recorded expiration |
| `__RPOCT2GC` | Expiring string | Separate cleanup lock |

### How it works

- Each set creates a new generation. One script registers its memberships, then writes the
  generation and payload with one `HSET` and a shared absolute expiry.
- Eviction atomically checks that a member's generation matches the value's current
  generation before deleting the value, then removes that member. Obsolete memberships from
  changed tags or reused keys are harmless; they stay until eviction or cleanup.
- Eviction is atomic per membership, not per tag. A membership added after the eviction scan
  can survive it; a later eviction finds it. A replacement never loses its new membership.
- Cleanup keeps later-deadline members and scores. Malformed members are removed without
  deleting a value.
- All values use absolute expiry, including untagged ones. Send time counts toward the
  lifetime. Keep application and Redis clocks synchronized.

### Failure behavior

- The set script checks key types and permissions before writing. A failure during indexing
  can leave reclaimable metadata but never publishes the payload. Lua does not roll back
  earlier commands.
- Cancellation, disconnection, or a lost reply after dispatch can still complete the
  operation. Do not assume an exception means nothing was published.
- A failed eviction can delete some matching generations. Retrying is safe.
- This is not a persistence or failover durability guarantee.

### Switching modes

- The modes use separate keys, so switching starts a cold cache. There is no fallback,
  conversion, or cross-mode eviction.
- Both formats can coexist under one instance name, but neither reads or invalidates the
  other. If old responses must not reappear after switching back, let them expire or clear
  the old format first.
- Guarantees hold only for generation-aware writers. Direct writes that change its generation
  or index metadata are unsupported.
- Microsoft store readers cannot read generation-aware payloads; use `HGET` or the store's
  APIs.

### Cluster

All keys for an instance occupy one slot, which concentrates its load on one primary. The
instance's hash tag must remain the effective hash tag after any client key prefix. Different
instances can use different hash tags. Cross-slot keys are rejected; there is no fallback.

### Costs

- One script call per set (normally `EVALSHA`, `EVAL` first time), instead of two scripts
  per tag plus `SET`. Script work grows with tag count; large tag lists hold Redis's script
  lock longer.
- Each eviction page pipelines at most 250 generation-check scripts.
- Each membership adds a GUID and each value a generation field.
- Size each tag index for about (tagged writes per second) × (TTL in seconds), plus expired
  members awaiting cleanup. Each overwrite counts as a write.
- Writes copy the whole payload into an owned array before dispatch. Each nonempty buffer read
  copies into a `byte[]`, unlike the default mode's pooled lease.

Required permissions: `TYPE`, `HGET`, `HSET`, `PEXPIREAT`, `DEL`, sorted-set operations, and
script execution, plus the cleanup-lock permissions above. Redis 7's `redis.acl_check_cmd`
checks publication permissions.

## Performance

A development measurement on Linux with .NET 10 and Redis 7.0.15 used 500 values of 1 KiB
each, serial requests, and one CPU per container. Each cell is the median of three runs.
These are indicative end-to-end numbers, not throughput guarantees.

| Tags | Mode | Set µs/value | Get µs/value | Evict 500 values, ms | Cleanup, ms |
| --- | --- | ---: | ---: | ---: | ---: |
| 0 | MicrosoftCompatible | 709.78 | 696.93 | n/a | 3.02 |
| 0 | GenerationAware | 772.33 | 673.59 | n/a | 2.73 |
| 1 | MicrosoftCompatible | 1456.67 | 720.89 | 6.52 | 3.77 |
| 1 | GenerationAware | 751.27 | 632.36 | 8.49 | 3.57 |
| 5 | MicrosoftCompatible | 1532.56 | 651.02 | 6.53 | 3.55 |
| 5 | GenerationAware | 780.74 | 658.04 | 8.43 | 3.56 |
| 25 | MicrosoftCompatible | 1955.83 | 628.10 | 7.05 | 4.46 |
| 25 | GenerationAware | 958.80 | 668.64 | 8.67 | 4.68 |

A 1 KiB value used 1384 bytes of Redis memory in the default format and 1592 bytes in the
generation-aware hash. A one-tag index with 500 members used 44960 versus 62560 bytes.
Generation-aware tagged sets need fewer round trips, but cost more memory and eviction time.
Measure with representative payloads, tag counts, overwrite rates, concurrency, and latency
before choosing. The [tagging probe](https://github.com/thomhurst/Respire/blob/main/docs/OUTPUT_CACHING.md)
describes how to run it.
