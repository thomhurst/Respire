---
title: Output caching
description: Store ASP.NET Core output-cache responses in Redis, with Microsoft store interoperability or generation-aware tag eviction.
---

# Output caching

`Respire.OutputCaching` stores ASP.NET Core output-cache responses in Redis. It implements
`IOutputCacheStore` and `IOutputCacheBufferStore` on .NET 8 and .NET 10. Use it when several
application instances should share cached responses and evict them by tag.

The default format matches Microsoft's `Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`
store, so you can switch without flushing the cache. A second, generation-aware mode protects
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

Registration also adds the standard output-cache services and a hosted service that
removes expired tag references. To use a keyed or other specific client, pass a factory:
`AddRespireOutputCache(configure, provider => provider.GetRequiredKeyedService<IRespireClient>("cache"))`.
With .NET Aspire, use the [`Aspire.Respire.OutputCaching`](aspire.md#cache-companions) companion instead.

To invalidate a tag, resolve `IOutputCacheStore` and call
`EvictByTagAsync("products", cancellationToken)`.

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `InstanceName` | `null` | Key prefix. Matches the Microsoft store's `InstanceName`. |
| `TaggingMode` | `MicrosoftCompatible` | `GenerationAware` uses a separate format. See [generation-aware tagging](#generation-aware-tagging). |
| `CleanupInterval` | 5 minutes | Hosted cleanup period. Must be at least 1 ms and fit a timer interval. |
| `TimeProvider` | `TimeProvider.System` | Clock for tag scores, absolute expiry, and the cleanup timer. |

Invalid options fail host startup validation. `GenerationAware` without a nonempty Redis
hash tag in `InstanceName` also fails validation.

## Entry behavior

- Cache values have Redis TTLs. Reads do not extend them.
- A lifetime must be at least one millisecond; shorter lifetimes throw
  `ArgumentOutOfRangeException`.
- Empty payloads are hits, distinct from missing entries.
- The store borrows its client. Disposing the service provider does not dispose an
  externally registered client instance.
- A `WithKeyPrefix` on the client changes the physical namespace. Account for that prefix
  when sharing data with a Microsoft store.

## Switching from Microsoft's Redis store

Keep the same Redis database, `InstanceName`, and ASP.NET Core output-cache policies. No
flush or payload conversion is needed. The format matches
[`Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Middleware/Microsoft.AspNetCore.OutputCaching.StackExchangeRedis/src/RedisOutputCacheStore.cs):

| Key suffix after `InstanceName` | Redis type | Contents |
| --- | --- | --- |
| `__MSOCV_` + cache key | String with TTL | Unchanged output-cache payload |
| `__MSOCT_` + tag | Sorted set | Cache keys scored by Unix-millisecond expiration |
| `__MSOCT` | Sorted set | Tags scored by their latest expiration |
| `__MSOCTGC` | Expiring string | Shared cleanup lock |

Either store can read and evict entries written by the other.

### Tag consistency in the default mode

Concurrent tag changes and eviction are not transactional. This matches the Microsoft
store's concurrency boundary.

- Respire registers all tags before it publishes a value, and reports registration
  failures. A failed or cancelled registration does not publish the new response. Partial
  metadata is harmless and expires through cleanup.
- Tagged writes use Redis 6.2+ [`SET PXAT`](https://redis.io/docs/latest/commands/set/)
  with the same absolute deadline as their tag scores. A delayed value write cannot outlive
  its references. Time spent registering tags counts toward the requested lifetime.
  Untagged writes keep relative TTLs.
- Respire keeps the longest recorded deadline for each tag and for each cache key within a
  tag. A shorter concurrent write cannot make cleanup remove the last-published value's
  membership early. Shorter overwrites can therefore keep metadata beyond the replacement
  value's lifetime. Microsoft writers can still shorten member scores, so this guarantee
  holds only when all writers use Respire.
- Set and eviction are separate operations, and a concurrent eviction can race
  registration and publication. A replacement written after eviction's `DEL` but before its
  `ZREM` can survive while losing that tag membership. A later eviction by that tag can then
  miss it.
- As in the Microsoft store, overwriting a key with different tags does not remove its old
  tag memberships. Evicting an old tag can remove the replacement value. Use stable tags for
  a cache key, or include the policy or tag generation in the key when tags change. The
  shared layout has no reverse key-to-tags index, and Microsoft writers would not maintain one.

Use [generation-aware tagging](#generation-aware-tagging) when obsolete memberships must
not delete a replacement and you do not need Microsoft store interoperability.

Keep application and Redis server clocks synchronized. Tagged values and tag scores share
an application UTC deadline, while untagged values use relative Redis TTLs.

## Buffers and cleanup

In `MicrosoftCompatible` mode, buffer reads copy directly from a pooled Respire lease into
the supplied `PipeWriter`, then flush without completing the caller's writer.
Single-segment writes use the ordinary `SET` path. Multi-segment writes use Respire's
streamed `SET` path without flattening the sequence. Keep the sequence memory unchanged
until `SetAsync` completes.

Cleanup removes expired sorted-set references, not cached values. In `MicrosoftCompatible`
mode it shares the Microsoft store's cleanup lock. Cleanup errors are logged and retried on
the next period. If the cleanup lock is lost, the pass logs at debug level and stops.

Tag eviction works in groups of up to 250 members. Value deletions are pipelined as separate
`DEL` commands, so values in different Cluster slots still work. One bulk `ZREM` follows
after every deletion in the group succeeds. Tag registration pipelines groups of up to 125
tags before it publishes the value.

If you construct `RespireOutputCacheStore` directly without a host, schedule
`CollectExpiredTagsAsync` yourself. The constructor accepts an optional logger. Redis still
expires values, but without tag cleanup, expired metadata accumulates.

### Redis permissions

The store needs string reads, writes and deletes, sorted-set operations, and scripting for
tag updates and lock fallback operations. Cancellable cleanup-lock renewal also needs
`CLIENT ID` and `CLIENT KILL`. Hosted cleanup uses a cancellable shutdown token and renews
the lock after every 250 scanned tags. Grant these permissions even when a small dataset
never reaches that threshold.

## Generation-aware tagging

Set `TaggingMode = RespireOutputCacheTaggingMode.GenerationAware` to protect newer values
from obsolete tag memberships. This mode requires Redis 7+ and an `InstanceName` that
contains a nonempty Redis hash tag, even on standalone Redis:

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

The store uses separate suffixes under that instance name:

| Key suffix | Redis type | Contents |
| --- | --- | --- |
| `__RPOCV2_` + cache key | Hash with TTL | `payload`: unchanged bytes; `generation`: 32-character GUID |
| `__RPOCT2_` + tag | Sorted set | `generation:cache-key` members, scored by expiration |
| `__RPOCT2` | Sorted set | Tags scored by their longest recorded expiration |
| `__RPOCT2GC` | Expiring string | Separate cleanup lock |

### How it works

Each set creates a new generation, even for a replacement with identical bytes or tags. One
script registers that generation's memberships, then publishes the generation and payload
together with one `HSET` and applies their shared absolute expiry. Eviction compares a
member's generation with the hash's current generation before it deletes the value, then
removes only that member. The comparison and removal run atomically. Replacing a key with
different tags, or reusing an expired key, therefore leaves its obsolete memberships
harmless. Old references stay until tag eviction or periodic cleanup. Writes do not keep a
reverse tag index or scan all tags.

Eviction is atomic per membership, not across all keys that carry a tag. A write whose
membership is added after the eviction scan can survive that scan; a later eviction still
finds it. A concurrent replacement cannot lose its new membership when an old member is
removed, even if it keeps the same tag. Cleanup uses one captured cutoff and keeps
later-deadline members and master scores. Malformed members are removed from the tag index
without deleting a value, so they cannot block eviction of valid entries.

All generation-aware values use absolute expiry, including untagged values. Time spent
sending a set counts toward its lifetime. Keep application and Redis clocks synchronized.

### Failure behavior

Lua scripts serialize their commands but do not roll back commands that ran before an
error. The set script checks key types and publication and expiry permissions before it
writes. It registers each tag in the master index before it adds the member. A failure
during indexing can leave reclaimable metadata but cannot publish the new payload.

Cancellation, disconnection, or a lost reply after dispatch can leave the operation
completed. Do not infer that a value was not published from an exception. An eviction
failure can leave a subset of matching generations deleted. Retrying is safe because every
delete checks the current generation. This is not a persistence or failover durability
guarantee.

### Switching modes

The two modes use separate suffixes. Switching starts a cold cache. There is no automatic
fallback, payload conversion, or cross-mode tag eviction. Both formats can coexist under one
instance name, but neither can read or invalidate the other's entries. If old responses
must not reappear after switching back, wait for old entries to expire or clear the old
format first.

The stronger guarantees apply only to writers that use the generation-aware format. Direct
writes that omit or alter its generation or index metadata are unsupported. Generation-aware
payloads are read with `HGET` or the store's array and buffer APIs, not by Microsoft store
readers.

### Cluster

All keys for an instance occupy one slot. The instance's hash tag must remain the effective
hash tag after any client key prefix is applied. Scripts can then declare every value and
tag key explicitly, at the cost of concentrating an instance's load on one primary.
Different instances can use different hash tags. Script routing rejects keys in different
slots; there is no cross-slot fallback.

### Costs

- Each set makes one script call (normally `EVALSHA`, with an initial `EVAL` fallback),
  rather than two single-key script calls per tag followed by `SET`. Script work grows with
  the number of tags, and large tag lists hold Redis's script lock longer.
- Each eviction page pipelines at most 250 generation-check scripts.
- Metadata adds a GUID to each membership and a generation field to each value.
  Replacements keep old memberships until cleanup or eviction.
- Size each tag index for roughly its tagged writes per second multiplied by the value TTL
  in seconds, plus expired members awaiting cleanup. Repeated overwrites of one key count as
  separate writes. Longer TTLs and cleanup intervals keep more metadata.
- Writes copy the complete payload into an owned array before dispatch, including segmented
  input. Each nonempty buffer read copies the payload into an owned `byte[]` before it writes
  to the destination, unlike the default mode's pooled lease.

Required permissions include `TYPE`, `HGET`, `HSET`, `PEXPIREAT`, `DEL`, sorted-set
operations, and script execution, plus the cleanup-lock permissions above. Redis 7's
`redis.acl_check_cmd` checks publication permissions.

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
Generation-aware sets need fewer round trips for tagged writes, but its metadata and
eviction scripts cost memory and eviction time. Repeat the measurement with representative
payloads, tag counts, overwrite rates, concurrency, and network latency before you choose a
mode. The [tagging probe](https://github.com/thomhurst/Respire/blob/main/docs/OUTPUT_CACHING.md)
describes how to run it.
