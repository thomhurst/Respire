using System.Net;
using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
await using var builder = await DistributedApplicationTestingBuilder
    .CreateAsync<Projects.Renamed_Aspire_Store_AppHost>(deadline.Token);
await using var app = await builder.BuildAsync(deadline.Token);
await app.StartAsync(deadline.Token);
await app.ResourceNotifications.WaitForResourceHealthyAsync("cache", deadline.Token);
await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
using var http = app.CreateHttpClient("api", "http");
foreach (var endpoint in new[] { "/health/ready", "/health/live" })
{
    if (await http.GetStringAsync(endpoint, deadline.Token) != "Healthy")
        throw new InvalidOperationException($"Unhealthy endpoint: {endpoint}");
}

var key = Guid.NewGuid().ToString("N");
using (var missing = await http.GetAsync($"/values/{key}", deadline.Token))
{
    if (missing.StatusCode != HttpStatusCode.NotFound)
        throw new InvalidOperationException("A missing key must return 404.");
}
// Repeat with a new value to prove the API reads Redis, not a previously cached response.
foreach (var expected in new[] { "hello from Aspire", "updated value: café ☕" })
{
    using var write = await http.PutAsJsonAsync($"/values/{key}", expected, deadline.Token);
    if (write.StatusCode != HttpStatusCode.NoContent)
        throw new InvalidOperationException($"PUT failed: {write.StatusCode}");
    var actual = await http.GetFromJsonAsync<ValueResult>($"/values/{key}", deadline.Token);
    if (actual?.Key != key || actual.Value != expected)
        throw new InvalidOperationException("Redis value did not round-trip through the generated API.");
}
Console.WriteLine("PASS: Aspire Redis readiness, API health, missing key, write/read and overwrite.");

internal sealed record ValueResult(string Key, string Value);
