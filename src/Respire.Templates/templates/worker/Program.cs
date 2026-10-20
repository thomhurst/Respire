using System.Text.Json;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Respire;
using Respire.DependencyInjection;
using Respire.HealthChecks;
using Respire.Streaming;
using Respire.Streaming.Dashboard;
using RespireWorker;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["Dashboard:Password"]))
{
    throw new InvalidOperationException("Set Dashboard__Password before starting the worker (see README.md).");
}
builder.Services.AddAuthentication(OperatorAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, OperatorAuthenticationHandler>(
        OperatorAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("QueueOperators",
    policy => policy.RequireAuthenticatedUser().RequireRole("QueueOperator")));
builder.Services.AddRespire(builder.Configuration.GetConnectionString("Redis")
    ?? throw new InvalidOperationException("ConnectionStrings:Redis is required."));
builder.Services.AddHealthChecks().AddRespire();
builder.Services.AddRespireJobQueue<SampleJobHandler, SampleJob>("RespireWorker",
    job => JsonSerializer.SerializeToUtf8Bytes(job, JobJson.Default.SampleJob),
    bytes => JsonSerializer.Deserialize(bytes, JobJson.Default.SampleJob)
        ?? throw new JsonException("Missing job payload."),
    new() { ConsumerCount = 2, DeliveryLimit = 5, TelemetryName = "sample-jobs" },
    new() { MetricsRetentionMinutes = 60 });
builder.Services.AddRespireStreamingDashboard(options => options.Streams.Add(new()
{
    Name = "Sample jobs",
    Stream = "{respire-job:RespireWorker}:ready",
    Group = RespireJobQueue<SampleJob>.ConsumerGroup,
    DeadLetterStream = "{respire-job:RespireWorker}:ready:dead",
    IsJobQueue = true,
}));
var telemetry = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("Respire", RespireStreamWorkerTelemetry.ActivitySourceName))
    .WithMetrics(metrics => metrics.AddMeter("Respire", RespireStreamWorkerTelemetry.MeterName));
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    telemetry.UseOtlpExporter();
}

await using var app = builder.Build();
using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
{
    await app.Services.GetRequiredService<IRespireClient>().PingAsync(startup.Token);
}
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health/ready");
app.MapGet("/", () => "See README.md to enqueue a job. Operator dashboard: /workers/");
app.MapRespireStreamingDashboard("/workers", "QueueOperators");
var jobs = app.MapGroup("/jobs").RequireAuthorization("QueueOperators");
jobs.MapPost("/{id}", async (string id, SampleJob job, IServiceProvider services, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(id) || id.Length > 128
        || string.IsNullOrWhiteSpace(job.Message) || job.Message.Length > 1000)
        return Results.BadRequest("Supply an ID of 1–128 characters and a message of 1–1000 characters.");

    var queue = services.GetRequiredKeyedService<RespireJobQueue<SampleJob>>("RespireWorker");
    var submitted = await queue.EnqueueAsync(id, job, cancellationToken: ct);
    return Results.Accepted($"/jobs/{Uri.EscapeDataString(id)}", new
    {
        id, submitted.Enqueued, entryId = submitted.EntryId.ToString(),
    });
});
jobs.MapGet("/{id}", async (string id, IRespireClient redis, CancellationToken ct) =>
{
    var result = await redis.GetAsync<string>($"RespireWorker:result:{id}", ct);
    return result is null ? Results.NotFound() : Results.Ok(new { id, result });
});
await app.RunAsync();
