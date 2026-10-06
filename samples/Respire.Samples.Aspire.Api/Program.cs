using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using OpenTelemetry;
using Respire;

var builder = WebApplication.CreateBuilder(args);
RespireMetrics.Configure(new RespireMetricsOptions { Groups = RespireMetricGroups.Default | RespireMetricGroups.Command });
builder.AddRespireClientBuilder("cache")
    .AddHybridCache(options => options.InstanceName = "respire:aspire:cache:")
    .AddOutputCache(options => options.InstanceName = "respire:aspire:output:");
builder.AddKeyedRespireClient("valkey");
builder.Services.AddOpenTelemetry().UseOtlpExporter();
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);

var app = builder.Build();
// Health checks reuse existing connections. Establish both clients before serving readiness.
using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
{
    await app.Services.GetRequiredService<IRespireClient>().PingAsync(startup.Token);
    await app.Services.GetRequiredKeyedService<IRespireClient>("valkey").PingAsync(startup.Token);
}
app.UseOutputCache();
app.MapHealthChecks("/health");
app.MapGet("/ping", async (IRespireClient client, CancellationToken cancellationToken) =>
    new { server = "redis", latency = await client.PingAsync(cancellationToken) });
app.MapGet("/valkey", async (IServiceProvider services, CancellationToken cancellationToken) =>
    new { server = "valkey", latency = await services.GetRequiredKeyedService<IRespireClient>("valkey").PingAsync(cancellationToken) });
app.MapGet("/distributed/{key}", async (string key, IDistributedCache cache, CancellationToken cancellationToken) =>
{
    var value = Guid.NewGuid().ToString();
    await cache.SetStringAsync(key, value, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, cancellationToken);
    return new { value, stored = await cache.GetStringAsync(key, cancellationToken) };
});
app.MapGet("/hybrid/{key}", async (string key, HybridCache cache, CancellationToken cancellationToken) =>
    await cache.GetOrCreateAsync(key, _ => ValueTask.FromResult(Guid.NewGuid().ToString()), cancellationToken: cancellationToken));
// Bypass L1 and fail on a miss so the acceptance test proves the Redis-backed L2 read.
app.MapGet("/hybrid/{key}/distributed", async (string key, HybridCache cache, CancellationToken cancellationToken) =>
    await cache.GetOrCreateAsync<string>(key, _ => throw new InvalidOperationException("The Redis cache entry is missing."),
        new HybridCacheEntryOptions { Flags = HybridCacheEntryFlags.DisableLocalCache }, cancellationToken: cancellationToken));
app.MapGet("/output", () => Guid.NewGuid().ToString()).CacheOutput();
app.Run();
