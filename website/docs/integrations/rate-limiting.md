---
title: ASP.NET Core rate limiting
description: Share named and partitioned HTTP request limits across application instances with Redis.
---

# ASP.NET Core rate limiting

`Respire.RateLimiting` connects [Respire's distributed rate limiters](../guides/coordination.md#redis-backed-rate-limits)
to ASP.NET Core's `AddRateLimiter` middleware on .NET 8 and .NET 10. It lives in a separate package
so applications using `Respire.Coordination` outside ASP.NET Core need no web framework reference.

## Minimal setup

```bash
dotnet add package Respire.RateLimiting
```

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Coordination;
using Respire.RateLimiting;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var limits = new RespireCoordination(redis).RateLimiters;
var builder = WebApplication.CreateBuilder();
builder.Services.AddRateLimiter(options =>
{
    options.AddRespireFixedWindowLimiter("api", limits, "my-app:limits:api:v1",
        permitLimit: 100, window: TimeSpan.FromMinutes(1));
});

var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
app.MapGet("/api", () => "OK").RequireRateLimiting("api");
await app.RunAsync();
```

Use the same Redis endpoint, database, client key prefix, Redis key, and algorithm settings on every
application instance that shares a budget. Policy names select endpoints locally; the **Redis key**
identifies the distributed budget. Give unrelated policies different Redis keys. Redis 7+ and
RESP2/RESP3 are supported. Redis errors propagate through the middleware; they do not grant requests.

The named helpers are `AddRespireFixedWindowLimiter`, `AddRespireSlidingWindowLimiter` (with `segments`),
and `AddRespireTokenBucketLimiter` (with `tokensPerPeriod` and `replenishmentPeriod`). Each takes a policy
name, `RespireRateLimiters`, Redis key, and algorithm settings. Optional `queueLimit` and
`queueProcessingOrder` have the same meaning as the underlying limiters. The default queue limit is zero.
Limiters are created lazily on the first request; algorithm settings are validated when a limiter is created.
Keep binary Redis key storage unchanged for the policy's lifetime.

## Partition by user, IP, or API key

`AddRespirePartitionedLimiter` accepts a partition selector and a limiter factory. The middleware caches
one local limiter per partition and disposes idle limiters. Respire also disposes remaining limiters when the host stops.
Return a **new limiter** from each factory call, including calls after idle eviction; do not share one
limiter object between policies or partitions. The application owns the Redis client and must keep it alive
until its HTTP hosts stop. Eviction and application restarts retain the budget stored in Redis.

This example assumes authentication has already established a trusted name-identifier claim:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Respire;
using Respire.Coordination;
using Respire.RateLimiting;

await using var redis = await RespireClient.ConnectAsync("redis://localhost:6379");
var limits = new RespireCoordination(redis).RateLimiters;
var builder = WebApplication.CreateBuilder();
builder.Services.AddRateLimiter(options => options.AddRespirePartitionedLimiter(
    "per-user",
    context => context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous",
    user => limits.SlidingWindow($"my-app:limits:users:v1:{user}",
        permitLimit: 20, window: TimeSpan.FromMinutes(1), segments: 6)));

var app = builder.Build();
app.UseRouting();
// In an authenticated application, put UseAuthentication() before UseRateLimiter().
app.UseRateLimiter();
app.MapGet("/api", () => "OK").RequireRateLimiting("per-user");
await app.RunAsync();
```

All anonymous requests in this example share one budget. For multi-tenant applications, include a trusted
tenant identifier in the partition and Redis key using an unambiguous encoding. Partition selectors must
be stable for the duration of a request; ASP.NET Core can call them for both synchronous and asynchronous
acquisition. Selectors must not return null.

For IP partitions, select `context.Connection.RemoteIpAddress?.ToString() ?? "unknown"`. Behind a proxy,
configure trusted forwarded headers before rate limiting. For API-key partitions, select a validated,
bounded credential identifier supplied by authentication. Do not put raw secrets in Redis keys. Bound
partition cardinality through validated identities; arbitrary attacker-controlled values can create many
cached limiters and Redis keys. The factory also supports GCRA, composite limits, and deny lists from
`Respire.Coordination`.

## Rejections and queues

Respire policies return **429 Too Many Requests**. When a rejected lease supplies `MetadataName.RetryAfter`,
the policy writes `Retry-After` as an invariant whole-second delay, rounded up with a minimum of one second.
Rejections without retry metadata, such as deny-list rejections, do not get a fabricated retry estimate.

The policy then invokes `RateLimiterOptions.OnRejected`, if configured. That callback can write a response
body or override the status and header. It is read when rejection occurs, so configuring it before or after
adding a Respire policy has the same effect. These defaults apply only to Respire endpoint policies;
unrelated policies and a separate global limiter keep their own rejection behavior.

Queued requests wait asynchronously and observe `HttpContext.RequestAborted`. Queue capacity is **local
to each application instance**, while admission uses the shared Redis budget. A queue-full rejection may
have no retry metadata. Use bounded queues and normal request timeouts. The existing Redis-backed limiter
handles the middleware's synchronous probe without consuming permits, then checks Redis in `AcquireAsync`.
