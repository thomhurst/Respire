# ASP.NET Core output caching

`Respire.OutputCaching` implements `IOutputCacheStore` and `IOutputCacheBufferStore` on
.NET 8 and .NET 10. Register an `IRespireClient`, then call `AddRespireOutputCache` before
building the application. Registration also adds the standard output-cache services and
a hosted service that removes expired tag references every five minutes.

```shell
dotnet add package Respire.OutputCaching
dotnet add package Respire.DependencyInjection
```

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

Resolve `IOutputCacheStore` and call `EvictByTagAsync("products", cancellationToken)` to
invalidate that tag. Cache values have Redis TTLs; reads do not extend them. A lifetime
must be at least one millisecond. Empty payloads are hits, distinct from missing entries.

## Switching from Microsoft's Redis store

Keep the same Redis database, `InstanceName`, and ASP.NET Core output-cache policies. No
flush or payload conversion is needed. The format matches
[`Microsoft.AspNetCore.OutputCaching.StackExchangeRedis`](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Middleware/Microsoft.AspNetCore.OutputCaching.StackExchangeRedis/src/RedisOutputCacheStore.cs):

| Key suffix after InstanceName | Redis type | Contents |
| --- | --- | --- |
| `__MSOCV_` + cache key | String with TTL | Unchanged output-cache payload |
| `__MSOCT_` + tag | Sorted set | Cache keys scored by Unix-millisecond expiration |
| `__MSOCT` | Sorted set | Tags scored by their latest expiration |
| `__MSOCTGC` | Expiring string | Shared cleanup lock |

Either store can read and evict entries written by the other. Concurrent tag changes and
eviction are not transactional, matching the Microsoft store's concurrency boundary.
Respire registers all tags before publishing a value and reports registration failures.
A failed or cancelled registration therefore does not publish the new response; partial
metadata is harmless and expires through cleanup. Tagged writes use Redis 6.2+
[`SET PXAT`](https://redis.io/docs/latest/commands/set/) with the same absolute deadline
as their tag scores, so a delayed value write cannot outlive its references. Time spent
registering tags counts toward the requested lifetime. Untagged writes retain relative TTLs.
Respire preserves the longest recorded deadline for both each tag and each cache key within
that tag. A shorter concurrent write cannot make cleanup remove the last-published value's
membership early. Shorter overwrites can consequently retain metadata beyond the replacement
value's lifetime. Microsoft writers can still shorten individual member scores, so this
concurrent-write guarantee applies only when all participating writers use Respire.
Set and eviction are still separate operations: a concurrent eviction can race registration
and publication. This mode does not promise generation-aware or atomic invalidation.
In particular, a replacement written after eviction's `DEL` but before its `ZREM` can survive
while losing that tag membership, so later eviction by that tag may miss it.
As in the Microsoft store, overwriting a key with different tags does not remove its old
tag memberships. Evicting an old tag can therefore remove the replacement value. Use stable
tags for a cache key, or include the policy/tag generation in the key when changing tags.
The shared layout has no reverse key-to-tags index or value generation to identify obsolete
memberships, and Microsoft writers would not maintain an added index.
[Generation-aware tag invalidation](https://github.com/thomhurst/Respire/issues/917) tracks
the stronger opt-in design separately from this interoperable mode.
Keep participating application and Redis server clocks synchronized: tagged values and tag
scores share an application UTC deadline, while untagged values use relative Redis TTLs.

The store borrows its client. Disposing a service provider does not dispose an externally
registered client instance. An additional `WithKeyPrefix` on the client changes the physical
namespace; account for that prefix when sharing data with a Microsoft store.

## Buffers and cleanup

Buffer reads copy directly from a pooled Respire lease into the supplied `PipeWriter`, then
flush without completing the caller's writer. Single-segment writes use the ordinary SET
path; multi-segment writes use Respire's streamed SET path without flattening the sequence.
Keep sequence memory unchanged until `SetAsync` completes.

`CleanupInterval` controls the hosted cleanup period and must be at least one millisecond
and fit a timer interval. `TimeProvider` supplies the clock and timer. Invalid options fail
host startup validation. Cleanup errors are logged and retried on the next period. Cleanup removes expired
sorted-set references, not cached values, and shares the Microsoft store's cleanup lock.
The required Redis permissions include string reads/writes/deletion, sorted-set operations,
and scripting for tag updates and managed-lock fallback operations. Cancellable cleanup-lock
renewals also require `CLIENT ID` and `CLIENT KILL`; hosted cleanup uses a cancellable
shutdown token and renews after every 250 scanned tags. These permissions are required even
when small test datasets never reach that threshold.

Tag eviction works in bounded groups: value deletions are pipelined, followed by one bulk
`ZREM` after every deletion in the group succeeds. Separate pipelined `DEL` commands preserve
support for values in different Cluster slots; one multi-key `DEL` would reject those keys.
Tag registration pipelines groups of up to 125 tags (250 single-key script calls) before
publishing the value. Deferred scripts use `EVAL`, avoiding script-cache misses within a
pipeline. Cleanup pipelines up to 250 per-tag removals before renewing its lock; it preserves
the captured cutoff for every group. Lock loss emits a debug log and skips the remaining pass
and master purge. Direct construction accepts an optional logger; DI supplies one automatically.

If constructing `RespireOutputCacheStore` directly without a host, schedule
`CollectExpiredTagsAsync` yourself. Redis still expires values automatically; without tag
cleanup, expired metadata can accumulate.

Validation scenarios live in `tests/Respire.Caching.Tests/OutputCacheTests.cs`, including
buffer reads, segmented writes, master-tag score monotonicity, eviction, expired-tag cleanup,
cleanup-lock contention, cancellation, registration, and bidirectional Microsoft-store use.
