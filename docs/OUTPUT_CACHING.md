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
Respire awaits tag updates and reports failures instead of sending them fire-and-forget.
Keep participating application clocks synchronized: tag scores use application UTC time,
while value expiration uses Redis TTLs.

The store borrows its client. Disposing a service provider does not dispose an externally
registered client instance. An additional `WithKeyPrefix` on the client changes the physical
namespace; account for that prefix when sharing data with a Microsoft store.

## Buffers and cleanup

Buffer reads copy directly from a pooled Respire lease into the supplied `PipeWriter`, then
flush without completing the caller's writer. Single-segment writes use the ordinary SET
path; multi-segment writes use Respire's streamed SET path without flattening the sequence.
Keep sequence memory unchanged until `SetAsync` completes.

`CleanupInterval` controls the hosted cleanup period. `TimeProvider` supplies the clock and
timer. Cleanup errors are logged and retried on the next period. Cleanup removes expired
sorted-set references, not cached values, and shares the Microsoft store's cleanup lock.
The required Redis permissions include string reads/writes/deletion, sorted-set operations,
and scripting for tag updates and managed-lock fallback operations.

If constructing `RespireOutputCacheStore` directly without a host, schedule
`CollectExpiredTagsAsync` yourself. Redis still expires values automatically; without tag
cleanup, expired metadata can accumulate.

Validation scenarios live in `tests/Respire.Caching.Tests/OutputCacheTests.cs`, including
buffer reads, segmented writes, master-tag score monotonicity, eviction, expired-tag cleanup,
cleanup-lock contention, cancellation, registration, and bidirectional Microsoft-store use.
