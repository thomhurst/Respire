using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardLogTests(RedisTestContainer redis)
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LogPageBoundsReadsEscapesContentAndPreservesNestedPaths(bool nested)
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        const string job = "<job>{other}";
        var key = "{dash}:source:logs:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(job)));
        await client.Lists.RightPushAsync(key, ["older line", "<script>alert(1)</script>", "last line"]);
        await client.Streams.CreateGroupAsync("{dash}:source", "g", RespireStreamId.Beginning);
        await using var app = await DashboardSecurityTests.CreateApplication(client, nested: nested, jobs: true, entryLimit: 2);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        var path = nested ? "/tenant/admin/workers" : "/workers";
        using var home = await http.GetAsync(path + "/");
        await Assert.That(await home.Content.ReadAsStringAsync()).Contains("action=\"" + path + "/0/logs\"");
        using var page = await http.GetAsync(path + "/0/logs?jobId=" + Uri.EscapeDataString(job));
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(page.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(page.Headers.Contains("Content-Security-Policy")).IsTrue();
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).DoesNotContain("older line");
        await Assert.That(html).DoesNotContain("<script>");
        await Assert.That(html).Contains("&lt;script&gt;");
        await Assert.That(html).Contains("&lt;job&gt;");
        await Assert.That(html).Contains("last line");
        await Assert.That(html).Contains("href=\"" + path + "\"");
        using var missing = await http.GetAsync(path + "/0/logs?jobId=missing");
        await Assert.That(await missing.Content.ReadAsStringAsync()).Contains("No retained logs");
    }

    [Test]
    [Arguments("/workers/0/logs", true, 400)]
    [Arguments("/workers/0/logs?jobId=", true, 400)]
    [Arguments("/workers/0/logs?jobId=a&jobId=b", true, 400)]
    [Arguments("/workers/99/logs?jobId=job", true, 404)]
    [Arguments("/workers/-1/logs?jobId=job", true, 404)]
    [Arguments("/workers/0/logs?jobId=job", false, 404)]
    public async Task LogPageRejectsInvalidRequestsAndUnconfiguredQueues(string path, bool jobs, int status)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: jobs);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var page = await http.GetAsync(path);
        await Assert.That((int)page.StatusCode).IsEqualTo(status);
    }

    [Test]
    public async Task LogReadErrorsDoNotExposeRedisMessages()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var key = "{dash}:source:logs:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("job")));
        await client.Strings.SetAsync(key, "private wrong-type value");
        await using var app = await DashboardSecurityTests.CreateApplication(client, jobs: true);
        using var http = app.GetTestClient();
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var page = await http.GetAsync("/workers/0/logs?jobId=job");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("Job logs are unavailable");
        await Assert.That(html).DoesNotContain("WRONGTYPE");
        await Assert.That(html).DoesNotContain("private wrong-type value");
    }
}
