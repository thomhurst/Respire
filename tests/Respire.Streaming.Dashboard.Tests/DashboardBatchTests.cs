using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardBatchTests(RedisTestContainer redis)
{
    [Test]
    public async Task ConfiguredBatchesUseRuntimeStatusAndRenderEachLifecycleState()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        const string openId = "<open>&\"{other}";
        await queue.CreateBatchAsync(batchId: openId);
        await queue.AddBatchJobAsync(openId, "member", "payload");
        await queue.CreateBatchAsync(batchId: "sealed");
        await queue.AddBatchJobAsync("sealed", "member", "payload");
        await queue.SealBatchAsync("sealed");
        await queue.CreateBatchAsync(batchId: "terminal");
        await queue.SealBatchAsync("terminal");
        await queue.CreateBatchAsync(batchId: "cancelled");
        await queue.CancelBatchAsync("cancelled");
        await queue.CreateBatchAsync(batchId: "unlisted-secret");
        var options = Options(queue, openId, "sealed", "terminal", "cancelled", "unknown");
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var batches = snapshot.Streams[0].Batches!;
        await Assert.That(batches.Count).IsEqualTo(5);
        await Assert.That(batches[0].Status!.Pending).IsEqualTo(1);
        await Assert.That(batches[0].Status!.IsSealed).IsFalse();
        await Assert.That(batches[1].Status!.IsSealed).IsTrue();
        await Assert.That(batches[1].Status!.IsCompleted).IsFalse();
        await Assert.That(batches[2].Status!.IsCompleted).IsTrue();
        await Assert.That(batches[3].Status!.CancellationRequested).IsTrue();
        await Assert.That(batches[4].Status).IsNull();
        var html = DashboardPage.Render(snapshot, "/tenant/admin/workers", "token", "value");
        await Assert.That(html).Contains("&lt;open&gt;&amp;&quot;{other}");
        await Assert.That(html).DoesNotContain("unlisted-secret");
        await Assert.That(html).Contains("<td>1</td><td>0</td><td>0</td><td>0</td><td>Open</td><td>No</td><td>No</td>");
        await Assert.That(html).Contains("<td>Sealed</td><td>No</td><td>No</td>");
        await Assert.That(html).Contains("<td>Sealed</td><td>Yes</td><td>No</td>");
        await Assert.That(html).Contains("<td>Sealed</td><td>Yes</td><td>Yes</td>");
        await Assert.That(html).Contains("No retained batch data");
        await Assert.That(snapshot.Streams[0].Error).IsNull();

        await queue.RemoveBatchAsync("terminal");
        snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Batches![2].Status).IsNull();
        if (Environment.GetEnvironmentVariable("RESPIRE_DASHBOARD_PREVIEW") is { Length: > 0 } preview)
            await File.WriteAllTextAsync(preview, html);
    }

    [Test]
    public async Task FailedBatchReadsAreIsolatedAndCountsKeepTheirMeaning()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await queue.CreateBatchAsync(batchId: "counts");
        // Seed a retained runtime snapshot with distinct counts to catch swapped rendering fields.
        var seed = RespireScript.Create("""
            local batch = cmsgpack.unpack(redis.call('GET', KEYS[1]))
            batch.pending = 2; batch.succeeded = 3; batch.failed = 4; batch.cancelled = 5
            batch.sealed = true; batch.cancel = true
            redis.call('SET', KEYS[1], cmsgpack.pack(batch))
            return 1
            """);
        await client.Scripts.ExecuteIntegerAsync(seed, [BatchKey(queue, "counts")], []);
        await client.Lists.RightPushAsync(BatchKey(queue, "broken"), ["secret-error-detail"]);
        var options = Options(queue, "broken", "counts");
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(snapshot.Streams[0].Batches![0].Unavailable).IsTrue();
        await Assert.That(snapshot.Streams[0].Batches![1].Unavailable).IsFalse();
        await Assert.That(snapshot.Streams[0].SourceUnavailable).IsFalse();
        var html = DashboardPage.Render(snapshot, "/", "token", "value");
        await Assert.That(html).Contains("Batch data unavailable");
        await Assert.That(html).DoesNotContain("secret-error-detail");
        await Assert.That(html).DoesNotContain("WRONGTYPE");
        await Assert.That(html).Contains("<td>2</td><td>3</td><td>4</td><td>5</td><td>Sealed</td><td>No</td><td>Yes</td>");
        await Assert.That(html).Contains("Consumer groups");
    }

    [Test]
    public async Task RegistrationFreezesBatchIdsAndRequestsCannotExpandTheAllowlist()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
        await queue.CreateBatchAsync(batchId: "allowed");
        await queue.CreateBatchAsync(batchId: "secret");
        RespireDashboardOptions? captured = null;
        await using var app = await DashboardSecurityTests.CreateApplication(client, nested: true, configure: options =>
        {
            options.EntryLimit = 1;
            options.Streams.Clear();
            options.Streams.Add(Options(queue, "allowed").Streams[0]);
            captured = options;
        });
        captured!.Streams[0].BatchIds.Clear();
        captured.Streams[0].BatchIds.Add("secret");
        using var http = app.GetTestClient();
        const string path = "/tenant/admin/workers/?batchId=secret&stream=other&limit=200";
        using var denied = await http.GetAsync(path);
        await Assert.That(denied.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Add("X-Test-Role", "reader");
        using var forbidden = await http.GetAsync(path);
        await Assert.That(forbidden.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Remove("X-Test-Role");
        http.DefaultRequestHeaders.Add("X-Test-Role", "operator");
        using var response = await http.GetAsync(path);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(response.Headers.Contains("Content-Security-Policy")).IsTrue();
        var html = await response.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("<td>allowed</td>");
        await Assert.That(html).DoesNotContain("<td>secret</td>");
        var state = app.Services.GetRequiredService<DashboardState>();
        var first = await state.ReadAsync(default);
        var second = await state.ReadAsync(default);
        await Assert.That(ReferenceEquals(first, second)).IsTrue();
        await Assert.That(second.Streams[0].Batches!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BatchConfigurationIsBoundedAndValidated()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        var queue = new RespireJobQueue<string>(client, "reports", Encoding.UTF8.GetBytes);
        var options = Options(queue, "one", "two");
        options.EntryLimit = 1;
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        options.EntryLimit = 2;
        _ = new DashboardState(client, options);
        options.Streams[0].IsJobQueue = false;
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        options.Streams[0].IsJobQueue = true;
        options.Streams[0].Stream = "{other}:ready";
        await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        foreach (var id in new[] { " ", new string('x', 4097), "\uD800" })
        {
            options = Options(queue, id);
            await Assert.That(() => new DashboardState(client, options)).Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task UnconfiguredJobQueuesAndPlainStreamsRemainUsable()
    {
        await using var client = await RespireClient.ConnectAsync(redis.ConnectionString);
        var options = DashboardSecurityTests.Options(jobs: true);
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(DashboardPage.Render(snapshot, "/", "", "")).Contains("No batch IDs configured");
        options.Streams[0].IsJobQueue = false;
        snapshot = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(DashboardPage.Render(snapshot, "/", "", "")).DoesNotContain("Batch progress");
    }

    private static RespireDashboardOptions Options(RespireJobQueue<string> queue, params string[] ids)
    {
        var options = new RespireDashboardOptions();
        var stream = new RespireDashboardStream
        {
            Name = "Reports", Stream = queue.StreamKey, Group = RespireJobQueue<string>.ConsumerGroup,
            IsJobQueue = true,
        };
        foreach (var id in ids) stream.BatchIds.Add(id);
        options.Streams.Add(stream);
        return options;
    }

    private static string BatchKey(RespireJobQueue<string> queue, string id)
        => queue.StreamKey + ":batch:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
}
