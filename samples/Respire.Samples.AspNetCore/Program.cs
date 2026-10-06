using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Respire;
using Respire.Caching.Hybrid;
using Respire.DependencyInjection;
using Respire.HealthChecks;
using Respire.OutputCaching;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("Set ConnectionStrings__Redis to your Redis connection string.");
var prefix = builder.Configuration["Sample:KeyPrefix"] ?? "respire:aspnet-sample:";
ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

// Selection is process-wide. Configure once, before clients and exporters start.
RespireMetrics.Configure(new RespireMetricsOptions
{
    Groups = RespireMetricGroups.Default | RespireMetricGroups.Command,
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("Respire.Samples.AspNetCore"))
    .WithTracing(tracing => tracing.AddSource("Respire").AddConsoleExporter())
    .WithMetrics(metrics => metrics.AddMeter("Respire").AddConsoleExporter((_, reader) =>
        reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 1000));

// DI owns one lazy client. Both cache integrations reuse that registration.
builder.Services.AddRespire(connectionString);
builder.Services.AddRespireHybridCache(
    configureCache: options => options.InstanceName = prefix + "cache:",
    configureHybridCache: options => options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromMinutes(1),
        LocalCacheExpiration = TimeSpan.FromSeconds(5),
    });
builder.Services.AddRespireOutputCache(options => options.InstanceName = prefix + "output:");
builder.Services.AddHealthChecks().AddRespire();

await using var app = builder.Build();
app.UseOutputCache();
app.MapGet("/", () => new
{
    endpoints = new[] { "/health", "/redis", "/distributed", "/hybrid", "/output" },
    writes = "POST JSON {\"value\":\"hello\"} to /redis or /distributed; GET reads it back.",
});
app.MapHealthChecks("/health");

app.MapPost("/redis", async (WriteValue body, IRespireClient redis, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrEmpty(body.Value) || body.Value.Length > 1024) return Results.BadRequest("Use 1–1024 characters.");
    await redis.SetAsync(prefix + "direct:value", (RespireValue)body.Value,
        expiry: TimeSpan.FromMinutes(1), cancellationToken: cancellationToken);
    return Results.NoContent();
});
app.MapGet("/redis", async (IRespireClient redis, CancellationToken cancellationToken) =>
    Results.Ok(new { value = await redis.GetStringAsync(prefix + "direct:value", cancellationToken) }));

app.MapPost("/distributed", async (WriteValue body, IDistributedCache cache, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrEmpty(body.Value) || body.Value.Length > 1024) return Results.BadRequest("Use 1–1024 characters.");
    await cache.SetStringAsync("distributed", body.Value,
        new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1) }, cancellationToken);
    return Results.NoContent();
});
app.MapGet("/distributed", async (IDistributedCache cache, CancellationToken cancellationToken) =>
    Results.Ok(new { value = await cache.GetStringAsync("distributed", cancellationToken) }));

app.MapGet("/hybrid", async (Guid? entry, HybridCache cache, CancellationToken cancellationToken) =>
    await cache.GetOrCreateAsync(entry is { } id ? $"hybrid-time:{id:N}" : "hybrid-time",
        _ => ValueTask.FromResult(new CachedTime(Guid.NewGuid(), DateTimeOffset.UtcNow)),
        cancellationToken: cancellationToken));
app.MapGet("/output", () => new CachedTime(Guid.NewGuid(), DateTimeOffset.UtcNow))
    .CacheOutput(policy => policy.Expire(TimeSpan.FromSeconds(30)));

// Health probes deliberately reuse existing connections instead of opening them.
// Establish one before serving readiness checks; fail startup if Redis is unavailable.
using (var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    await app.Services.GetRequiredService<IRespireClient>().PingAsync(startupTimeout.Token);
await app.RunAsync();

public sealed record WriteValue(string Value);
public sealed record CachedTime(Guid Version, DateTimeOffset GeneratedAt);
