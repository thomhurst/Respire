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
Use [generation-aware tagging](#generation-aware-tagging) when obsolete memberships
must not delete a replacement and Microsoft-store interoperability is unnecessary.
Keep participating application and Redis server clocks synchronized: tagged values and tag
scores share an application UTC deadline, while untagged values use relative Redis TTLs.

The store borrows its client. Disposing a service provider does not dispose an externally
registered client instance. An additional `WithKeyPrefix` on the client changes the physical
namespace; account for that prefix when sharing data with a Microsoft store.

## Buffers and cleanup

In MicrosoftCompatible mode, buffer reads copy directly from a pooled Respire lease into the supplied `PipeWriter`, then
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

## Generation-aware tagging

Set `TaggingMode = RespireOutputCacheTaggingMode.GenerationAware` to protect newer
values from obsolete tag memberships. This mode requires Redis 7+ and an `InstanceName`
containing a nonempty Redis hash tag, even on standalone Redis:

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

The store uses isolated suffixes under that instance name:

| Key suffix | Redis type | Contents |
| --- | --- | --- |
| `__RPOCV2_` + cache key | Hash with TTL | `payload`: unchanged bytes; `generation`: 32-character GUID |
| `__RPOCT2_` + tag | Sorted set | `generation:cache-key` members, scored by expiration |
| `__RPOCT2` | Sorted set | Tags scored by their longest recorded expiration |
| `__RPOCT2GC` | Expiring string | Isolated cleanup lock |

Each set creates a new generation, including a replacement with identical bytes or tags.
A script registers that generation's memberships, then publishes the generation and raw
payload together with one `HSET`, and applies their shared absolute expiry. Eviction
compares a scanned member's generation with the hash's current generation before deleting
the value, then removes only that member. This comparison and removal execute atomically.
Replacing a key with different tags or reusing an expired key therefore makes its obsolete
memberships harmless. Old references remain until tag eviction or periodic expiry cleanup;
there is no reverse tag index or global tag scan on writes.

Eviction is atomic per membership, not across all keys carrying a tag. A write whose
membership is added after the eviction scan can survive that scan and remains discoverable
by a later eviction. A concurrent replacement cannot lose its new membership when an old
member is removed, even if the replacement keeps the same tag. Cleanup uses one captured
cutoff and preserves later-deadline members and master scores. Time spent sending a set
counts toward its lifetime; all generation-aware values use absolute expiry, including
untagged values. Keep application and Redis clocks synchronized.

Lua scripts serialize their commands but do not roll back commands preceding an error.
The set script checks key types and publication/expiry permissions before writing, and
registers each tag in the master index before adding its member. A failure during indexing
can leave reclaimable metadata but cannot publish the new payload. Publishing payload and
generation in one command prevents a partial failure from pairing a new payload with an
old generation. Cancellation, disconnection, or a lost reply after dispatch can leave the
operation completed: do not infer nonpublication from an exception. Eviction failures can
leave a subset of matching generations deleted; retrying remains safe because every delete
checks the current generation. This is not a persistence or failover durability guarantee.

Microsoft stores and MicrosoftCompatible Respire stores use separate suffixes. Switching
modes starts a cold cache; there is no automatic fallback, payload conversion, or cross-mode
tag eviction. Both formats can coexist under one instance name, but they cannot read or
invalidate each other's entries. Wait for old entries to expire or explicitly clear the
old format before switching back if old responses must not reappear. Stronger guarantees
apply only to writers using the generation-aware API and format; direct writes that omit
or alter its generation/index metadata are unsupported. Default-mode raw `GET` reads and
Microsoft interoperability remain unchanged. Generation-aware payloads are read by `HGET`
or the store's array/buffer APIs, not by Microsoft-store readers.

In Cluster, all keys for an instance occupy one slot. The instance's hash tag must remain
the effective hash tag after any client key prefix is applied. This enables scripts to
declare every value and tag key explicitly, at the cost of concentrating an instance's
load on one primary. Different instances can use different hash tags. Core script routing
rejects keys in different slots; no cross-slot fallback weakens the contract.

Each set makes one script call (normally `EVALSHA`, with an initial `EVAL` fallback), rather
than two single-key script calls per tag followed by `SET`. Inside Redis it checks types,
reads/updates each tag's master score, adds each member, and writes the hash and expiry.
Work and key/argument arrays grow with the number of tags; large tag lists hold Redis's
script execution lock longer. Each eviction page scans up to the requested scan count and
pipelines at most 250 generation-check scripts. Metadata adds a GUID to each membership
and a generation field to each value; replacements temporarily retain old memberships.
Cleanup bounds retention by deadlines and must still be scheduled for direct construction.

Generation-aware writes copy the complete payload into an owned array before dispatch,
including segmented input. Buffer reads allocate the payload array before copying into
the destination, unlike the default mode's pooled lease. Measure these costs with your
payload sizes and tag counts. Required permissions include `TYPE`, `HGET`, `HSET`,
`PEXPIREAT`, `DEL`, sorted-set operations, and script execution, plus the cleanup-lock
permissions described above. Redis 7's `redis.acl_check_cmd` checks publication permissions.

### Measured tagging costs

`tools/OutputCacheTaggingProbe` measures serial sets/gets, one full-tag eviction, expired
reference cleanup, and Redis `MEMORY USAGE`. It uses unique namespaces, 500 values per
dataset, 1 KiB payloads, three rounds, and alternating mode order. Warmup loads the scripts
before timing. After deleting values, it advances only the cleanup clock to prune remaining
references without waiting ten minutes. It never scans or flushes an entire database.
Run it against a disposable Redis instance:

```powershell
& scripts/Invoke-AgentDotNet.ps1 -DotNetArguments @('run', '--project', 'tools/OutputCacheTaggingProbe', '-f', 'net10.0', '--', 'redis://localhost:6379')
```

2026-10-05 development measurement: Linux .NET 10.0.12 / SDK 10.0.401 and Redis 7.0.15
on Docker Desktop, with one CPU per client/server container, 2 GiB client memory and
256 MiB server memory. The host also runs other development work. These are indicative
end-to-end measurements, not isolated microbenchmarks or throughput guarantees. Each
cell is the median of three dataset results; set/get results are averages within a dataset.

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

The measured 1 KiB values use 1384 bytes in the default format and 1592 bytes in the
generation-aware hash. With one tag and 500 members, that tag index's median memory is
44960 versus 62560 bytes. Tag-index size varies with Redis's internal allocation and
skip-list layout. The extra generation metadata and eviction script work have visible
costs, even where fewer set requests reduce latency. The probe leaves its Redis database
empty on successful completion. Repeat it with representative payloads, tag counts,
overwrite frequency, concurrency, and network latency before choosing a mode.
