using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire;
using Respire.DependencyInjection;
using Respire.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRespire(_ => RespireOptions.Parse(
    builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required.")) with
{
    ClientSideCache = new() { CoalesceConcurrentMisses = true },
});
builder.Services.AddHealthChecks().AddRespire();
var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire"))
    .WithMetrics(metrics => metrics.AddMeter("Respire"));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

await using var app = builder.Build();

app.MapHealthChecks("/health/ready");
app.MapGet("/", () => "Try GET /values/hello, then repeat it to reuse the cached value.");
app.MapGet("/values/{key}", async (string key, IRespireClient redis, CancellationToken ct) =>
{
    var value = await redis.GetOrSetAsync<string>($"RespireApi:value:{key}",
        _ => ValueTask.FromResult<string?>(Guid.NewGuid().ToString("N")),
        expiry: TimeSpan.FromMinutes(5), cancellationToken: ct);
    return Results.Ok(new { key, value });
});

await app.RunAsync();
