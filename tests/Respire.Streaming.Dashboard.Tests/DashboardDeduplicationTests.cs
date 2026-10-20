using System.Text;
using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardDeduplicationTests(RedisTestContainer redis)
{
    [Test]
    public async Task PendingPoliciesAreBoundedEncodedAndDoNotExposePayloads()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        var queue = new RespireJobQueue<string>(client, "deduplication", Encoding.UTF8.GetBytes);
        await queue.EnqueueAsync(RespireJobDeduplicationMode.Extend, "<identity>&", "secret-payload");
        await queue.EnqueueAsync("default", "other-secret");
        await client.Streams.CreateGroupAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync(queue.StreamKey, RespireJobQueue<string>.ConsumerGroup, "worker", new() { Count = 2 });
        var options = new RespireDashboardOptions { EntryLimit = 2 };
        options.Streams.Add(new() { Name = "deduplication", Stream = queue.StreamKey,
            Group = RespireJobQueue<string>.ConsumerGroup, IsJobQueue = true });
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var policies = snapshot.Streams[0].Deduplication!;
        await Assert.That(policies.Count).IsEqualTo(2);
        await Assert.That(policies.Values.Any(policy => policy.Identity == "<identity>&" && policy.Mode == "Extend")).IsTrue();
        await Assert.That(policies.Values.Any(policy => policy.Identity == "default" && policy.Mode == "FirstSubmissionWins")).IsTrue();
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Deduplication identity");
        await Assert.That(html).Contains("&lt;identity&gt;&amp;");
        await Assert.That(html).Contains("Extend");
        await Assert.That(html).DoesNotContain("secret-payload");
        await Assert.That(html).DoesNotContain("other-secret");
        await Assert.That(snapshot.Streams[0].Error).IsNull();
        options.EntryLimit = 1;
        var bounded = await new DashboardState(client, options).ReadAsync(default);
        await Assert.That(bounded.Streams[0].Deduplication!.Count).IsEqualTo(1);
    }
}
