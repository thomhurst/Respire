using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Mvc;
using OpenTelemetry;
using Respire;

var builder = WebApplication.CreateBuilder(args);
// Aspire injects ConnectionStrings:cache, including its generated credentials.
builder.AddRespireClient("cache");
builder.Logging.AddOpenTelemetry(logging => logging.IncludeFormattedMessage = true);
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry().UseOtlpExporter();
}

await using var app = builder.Build();
// Health checks probe existing connections; establish one before serving readiness.
using (var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
{
    await app.Services.GetRequiredService<IRespireClient>().PingAsync(startup.Token);
}

app.MapHealthChecks("/health/ready");
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapGet("/", () => "PUT /values/hello with a JSON string, then GET /values/hello.");
app.MapPut("/values/{key}", async (string key, [FromBody] string value, IRespireClient redis, CancellationToken ct) =>
{
    await redis.SetAsync($"Respire.Aspire-Starter:value:{key}", (RespireValue)value,
        expiry: TimeSpan.FromMinutes(5), cancellationToken: ct);
    return Results.NoContent();
});
app.MapGet("/values/{key}", async (string key, IRespireClient redis, CancellationToken ct) =>
{
    var value = await redis.GetStringAsync($"Respire.Aspire-Starter:value:{key}", ct);
    return value is null ? Results.NotFound() : Results.Ok(new { key, value });
});

await app.RunAsync();
