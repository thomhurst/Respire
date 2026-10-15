using System.Net;
using Microsoft.AspNetCore.TestHost;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardMetricsTests(RedisTestContainer redis)
{
    private static readonly RespireScript Seed = RespireScript.Create("""
        local minute = math.floor(tonumber(redis.call('TIME')[1]) / 60)
        redis.call('HSET', KEYS[1], 'retention', 60, tostring(minute % 60), cmsgpack.pack({minute, 7, 2}))
        redis.call('EXPIRE', KEYS[1], 3600)
        return 1
        """);

    [Test]
    public async Task HistoryIsAuthorizedAndCannotReadUnregisteredQueues()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        await client.Scripts.ExecuteIntegerAsync(Seed, ["{dash}:source:metrics"], []);
        await client.Strings.SetAsync("secret:metrics", "must-not-read");
        await using var app = await DashboardSecurityTests.CreateApplication(client, nested: true, jobs: true);
        using var http = app.GetTestClient();
        const string path = "/tenant/admin/workers/?stream=secret&minutes=999999";
        using var denied = await http.GetAsync(path);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using var forbidden = await http.GetAsync(path);
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Remove("X-Test-Role");
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var response = await http.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(html).Contains("Completed jobs / minute");
        await Assert.That(html).Contains("Failed jobs / minute");
        await Assert.That(html).Contains("</td><td>7</td><td>2</td>");
        await Assert.That(html).DoesNotContain("must-not-read");
        await Assert.That(html).DoesNotContain("Metrics history unavailable");
        if (Environment.GetEnvironmentVariable("RESPIRE_DASHBOARD_PREVIEW") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, html);
    }

    [Test]
    public async Task HistoryReadsAreBoundedAndFailuresDoNotHideTheRestOfTheSnapshot()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        await client.Scripts.ExecuteIntegerAsync(Seed, ["{dash}:source:metrics"], []);
        var options = DashboardSecurityTests.Options(jobs: true);
        options.MetricsHistoryMinutes = 2;
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Metrics!.Count).IsEqualTo(2);
        await Assert.That(snapshot.Streams[0].Metrics!.Sum(b => b.Completed)).IsEqualTo(7);
        await client.Keys.DeleteAsync("{dash}:source:metrics");
        await client.Strings.SetAsync("{dash}:source:metrics", "wrong-type");
        snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Metrics).IsNull();
        await Assert.That(snapshot.Streams[0].SourceUnavailable).IsFalse();
        await Assert.That(snapshot.Streams[0].Error!).Contains("Metrics history unavailable");
        options.Streams[0].IsJobQueue = false;
        snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Error).IsNull();
        await Assert.That(DashboardPage.Render(snapshot, "/", "token", "value")).DoesNotContain("<h3>Completion history</h3>");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1441)]
    public async Task HistoryLimitIsValidated(int limit)
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var options = DashboardSecurityTests.Options(jobs: true);
        options.MetricsHistoryMinutes = limit;
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentOutOfRangeException>();
    }
}
