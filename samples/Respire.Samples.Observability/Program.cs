using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenTelemetry.Metrics;
using Respire;

// Run through Smoke.ps1. Every scrape comes from the real OpenTelemetry HTTP exporter.
var endpoint = int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture);
var dedicatedPoolLabel = $"db_client_connection_pool_name=\"127.0.0.1:{endpoint}/0/dedicated\"";
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
RespireMetrics.Configure(new() { Groups = RespireMetricGroups.Default });
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics
    .AddMeter("Respire")
    .AddView(instrument => instrument.Unit == "s" ? new ExplicitBucketHistogramConfiguration
    {
        Boundaries = [0, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10],
    } : null)
    .AddPrometheusExporter(options => options.ScrapeResponseCacheDurationMilliseconds = 0));
await using var app = builder.Build();
app.MapPrometheusScrapingEndpoint();
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
using var http = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(10) };
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));

async Task<string> Scrape(string name)
{
    var text = await http.GetStringAsync("/metrics", deadline.Token);
    await File.WriteAllTextAsync(Path.Combine(output, name + ".prom"), text, deadline.Token);
    return text;
}

RespireOptions Options() => new()
{
    Endpoints = { new RespireEndpoint("127.0.0.1", endpoint) },
    ClientSideCache = new() { MaxEntries = 1 },
};

async Task Exercise(RespireClient client, string prefix)
{
    await client.SetAsync(prefix + ":first", "value", cancellationToken: deadline.Token);
    await client.SetAsync(prefix + ":second", "value", cancellationToken: deadline.Token);
    if (await client.GetStringAsync(prefix + ":first", deadline.Token) != "value"
        || await client.GetStringAsync(prefix + ":first", deadline.Token) != "value"
        || await client.GetStringAsync(prefix + ":second", deadline.Token) != "value")
        throw new InvalidOperationException("Cache workload did not round-trip values.");
    try
    {
        await client.Lists.LeftPopAsync(prefix + ":first", cancellationToken: deadline.Token);
        throw new InvalidOperationException("Expected Redis WRONGTYPE.");
    }
    catch (RespireServerException error) when (error.Message.Contains("WRONGTYPE", StringComparison.Ordinal)) { }
    await using var subscription = await client.SubscribeAsync(prefix + ":channel", cancellationToken: deadline.Token);
    await client.PublishAsync(prefix + ":channel", "message", deadline.Token);
    await using var reader = subscription.GetAsyncEnumerator(deadline.Token);
    if (!await reader.MoveNextAsync()) throw new InvalidOperationException("No published message received.");
    await client.Streams.AddAsync(prefix + ":stream", ("type", "smoke"));
    var entries = await client.Streams.ReadAsync(prefix + ":stream", cancellationToken: deadline.Token);
    if (entries.Length != 1) throw new InvalidOperationException("Expected one stream entry.");
    entries[0].RecordProcessingStart();
}

try
{
    await using (var defaults = await RespireClient.ConnectAsync(Options(), deadline.Token))
    {
        await Exercise(defaults, "default");
        await Scrape("default");
    }
    RespireMetrics.Configure(new() { Groups = RespireMetricGroups.All });
    await using (var optional = await RespireClient.ConnectAsync(Options(), deadline.Token))
    {
        await Exercise(optional, "optional");
        // Hold a real dedicated socket busy until a scrape proves the pending/used state.
        var blocked = optional.Lists.LeftPopAsync("optional:queue", Timeout.InfiniteTimeSpan, deadline.Token).AsTask();
        while (true)
        {
            var text = await Scrape("busy");
            if (PrometheusCheck.HasPositive(text, "db_client_connection_pending_requests", dedicatedPoolLabel)
                && PrometheusCheck.HasPositive(text, "db_client_connection_count", dedicatedPoolLabel, "db_client_connection_state=\"used\"")) break;
            await Task.Delay(20, deadline.Token);
        }
        await optional.Lists.RightPushAsync("optional:queue", "released");
        if (await blocked != "released") throw new InvalidOperationException("Blocking pop did not complete.");
        await Scrape("optional");
    }
    // Lifecycle delivery is asynchronous. Wait for the actual application close observation.
    while (!PrometheusCheck.HasPositive(await Scrape("closed"), "redis_client_connection_closed_total"))
        await Task.Delay(20, deadline.Token);
    PrometheusCheck.Verify(output, args[2], dedicatedPoolLabel);
    Console.WriteLine("PASS: real Redis workload, default/optional exports, lifecycle, and pinned dashboard contract.");
}
finally
{
    await app.StopAsync();
}
