using System.Text;
using Respire.Internal;
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
            var keys = JobGroupIntegrationTests.Keys(queue);
            await queue.EnqueueScheduledAsync("id{untrusted}", "payload", new() { Group = "tenant:{other}:東京" });
            const string group = RespireJobQueue<string>.ConsumerGroup;
            await client.Streams.CreateGroupAsync(queue.StreamKey, group, RespireStreamId.Beginning);
            var first = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
            var logs = JobLogKeys.ForJob(queue.StreamKey, "id{untrusted}");
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", first.Id.Value, 1, "before retry", 10, 60000])).IsEqualTo(1);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobSchedulingIntegrationTests.Script("Retry"),
                [.. keys], [group, "owner", first.Id.Value, 1, 0])).IsEqualTo(1);
            var second = (await JobGroupIntegrationTests.Read(client, queue, "owner")).Single();
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", first.Id.Value, 1, "stale", 10, 60000])).IsEqualTo(0);
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("Log"),
                [queue.StreamKey, logs], [group, "owner", second.Id.Value, 1, "after retry", 10, 60000])).IsEqualTo(1);
            await Assert.That(string.Join("|", await queue.GetLogsAsync("id{untrusted}")))
                .IsEqualTo("before retry|after retry");
            await Assert.That(second.GetString("_respire.job.attempts")).IsEqualTo("1");
            await Assert.That(second.GetString("_respire.job.id")).IsEqualTo("id{untrusted}");
            await Assert.That(second.GetString("_respire.job.group")).IsEqualTo("tenant:{other}:東京");
            await queue.EnqueueScheduledAsync("controlled", "payload", new() { Group = "controlled" });
            await queue.SetRateLimitAsync(new() { PermitLimit = 1, Window = TimeSpan.FromHours(1) });
            await queue.PauseAsync();
            using (var paused = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(paused[2].AsInteger()).IsEqualTo(1);
            await queue.ResumeAsync();
            using (var admitted = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(admitted[1].Count).IsEqualTo(1);
            using (var denied = await JobQueueControlTests.Admit(client, queue, "ReadGrouped"))
                await Assert.That(denied[3].AsInteger()).IsGreaterThan(0);
            await Assert.That((await queue.GetStateAsync()).RateLimitHits).IsEqualTo(1);
        }
    }
}
