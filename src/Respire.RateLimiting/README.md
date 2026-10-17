# Respire.RateLimiting

Redis-backed named and partitioned policies for ASP.NET Core's rate limiting middleware.
Targets .NET 8 and .NET 10.

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
    options.AddRespireFixedWindowLimiter("api", limits, "limits:api",
        permitLimit: 100, window: TimeSpan.FromMinutes(1)));

var app = builder.Build();
app.UseRouting();
app.UseRateLimiter();
app.MapGet("/api", () => "OK").RequireRateLimiting("api");
await app.RunAsync();
```

All application instances using the same Redis key share the limit. Rejections return HTTP 429
and a `Retry-After` delay rounded up to whole seconds when the lease supplies retry metadata.
The middleware owns limiter lifetimes; your application owns the Redis client.

See the [ASP.NET Core rate limiting guide](https://thomhurst.github.io/Respire/docs/integrations/rate-limiting)
for partitioned policies, rejection callbacks, queues, and deployment guidance.
