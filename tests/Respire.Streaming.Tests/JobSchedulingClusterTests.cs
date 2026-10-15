using System.Text;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class JobSchedulingClusterTests
{
    [Test]
    [NotInParallel]
    public async Task PrefixedEnqueuePromotionAndRetryExecuteInOneRealClusterSlot()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireJobQueue<string>(client, "cluster", Encoding.UTF8.GetBytes);
            string[] keys = [queue.StreamKey, queue.StreamKey + ":scheduled", queue.StreamKey + ":due"];
            await queue.EnqueueScheduledAsync("id{untrusted}", "payload", new() { Group = "tenant:{other}:東京" });
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Promote"),
                [.. keys], [64])).IsEqualTo(1);
            const string group = RespireJobQueue<string>.ConsumerGroup;
            await client.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
            var first = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner")).Single();
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Retry"),
                [.. keys], [group, "owner", first.Id.Value, 1, 0])).IsEqualTo(1);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Promote"),
                [.. keys], [64])).IsEqualTo(1);
            var second = (await client.Streams.ReadGroupOnceAsync(queue.StreamKey, group, "owner")).Single();
            await Assert.That(second.GetString("_respire.job.attempts")).IsEqualTo("1");
            await Assert.That(second.GetString("_respire.job.id")).IsEqualTo("id{untrusted}");
            await Assert.That(second.GetString("_respire.job.group")).IsEqualTo("tenant:{other}:東京");
            await queue.EnqueueAsync("controlled", "payload");
            await queue.SetRateLimitAsync(new() { PermitLimit = 1, Window = TimeSpan.FromHours(1) });
            await queue.PauseAsync();
            using (var paused = await JobQueueControlTests.Admit(client, queue, "Read"))
                await Assert.That(paused[2].AsInteger()).IsEqualTo(1);
            await queue.ResumeAsync();
            using (var admitted = await JobQueueControlTests.Admit(client, queue, "Read"))
                await Assert.That(admitted[1].Count).IsEqualTo(1);
            using (var denied = await JobQueueControlTests.Admit(client, queue, "Read"))
                await Assert.That(denied[3].AsInteger()).IsGreaterThan(0);
            await Assert.That((await queue.GetStateAsync()).RateLimitHits).IsEqualTo(1);
        }
    }
}
