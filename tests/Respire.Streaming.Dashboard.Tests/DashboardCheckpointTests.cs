using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Dashboard.Tests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class DashboardCheckpointTests(RedisTestContainer redis)
{
    [Test]
    public async Task PendingCheckpointIsBoundedEncodedAndDoesNotExposeState()
    {
        await using var root = await RespireClient.ConnectAsync(redis.ConnectionString);
        var client = root.WithKeyPrefix("tenant:");
        const string stream = "{respire-job:checkpoint}:ready";
        const string group = RespireJobQueue<string>.ConsumerGroup;
        var id = Guid.NewGuid().ToString("N");
        await client.Streams.CreateGroupAsync(stream, group, RespireStreamId.Beginning);
        await client.Streams.AddAsync(stream, ("_respire.job.id", "job"), ("_respire.job.payload", "secret-payload"),
            ("_respire.job.checkpoint", id));
        await client.Streams.ReadGroupOnceAsync(stream, group, "worker");
        await client.Scripts.ExecuteIntegerAsync(RespireScript.Create("""
            redis.call('SET', KEYS[1], cmsgpack.pack({'<cursor>&', 'secret-state', 75}))
            return 1
            """), [stream + ":checkpoint:" + id], []);
        var options = new RespireDashboardOptions { EntryLimit = 1 };
        options.Streams.Add(new() { Name = "checkpoint", Stream = stream, Group = group, IsJobQueue = true });
        var snapshot = await new DashboardState(client, options).ReadAsync(default);
        var html = DashboardPage.Render(snapshot, "/workers", "token", "value");
        await Assert.That(html).Contains("Checkpoint");
        await Assert.That(html).Contains("&lt;cursor&gt;&amp;");
        await Assert.That(html).Contains("75%");
        await Assert.That(html).DoesNotContain("secret-state");
        await Assert.That(html).DoesNotContain("secret-payload");
        await Assert.That(snapshot.Streams[0].Checkpoints!.Count).IsEqualTo(1);
        await Assert.That(snapshot.Streams[0].Error).IsNull();
    }
}
