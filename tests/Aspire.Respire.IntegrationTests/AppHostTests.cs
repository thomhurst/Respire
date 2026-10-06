using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Aspire;

public class AppHostTests
{
    /// <summary>Starts real Aspire resources and verifies client injection, readiness, and all cache backends.</summary>
    [Test]
    public async Task RedisAndValkeyReferencesConnectWithoutManualClientConfiguration()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await using var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.Respire_Samples_Aspire_AppHost>(deadline.Token);
        await using var app = await builder.BuildAsync(deadline.Token);
        await app.StartAsync(deadline.Token);
        await app.ResourceNotifications.WaitForResourceHealthyAsync("api", deadline.Token);
        using var http = app.CreateHttpClient("api", "http");
        await Assert.That(await http.GetStringAsync("/health", deadline.Token)).IsEqualTo("Healthy");
        using var redis = await http.GetAsync("/ping", deadline.Token);
        using var valkey = await http.GetAsync("/valkey", deadline.Token);
        await Assert.That(redis.IsSuccessStatusCode).IsTrue();
        await Assert.That(valkey.IsSuccessStatusCode).IsTrue();
        var key = Guid.NewGuid().ToString("N");
        var cache = await http.GetFromJsonAsync<CacheResult>($"/distributed/{key}", deadline.Token);
        await Assert.That(cache!.Stored).IsEqualTo(cache.Value);
        var hybrid = await http.GetStringAsync($"/hybrid/{key}", deadline.Token);
        await Assert.That(await http.GetStringAsync($"/hybrid/{key}", deadline.Token)).IsEqualTo(hybrid);
        await Assert.That(await http.GetStringAsync($"/hybrid/{key}/distributed", deadline.Token)).IsEqualTo(hybrid);
        var output = await http.GetStringAsync("/output", deadline.Token);
        await Assert.That(await http.GetStringAsync("/output", deadline.Token)).IsEqualTo(output);
    }

    private sealed record CacheResult(string Value, string Stored);
}
