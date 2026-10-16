using System.Text;
using Respire.Internal;
using System.Reflection;
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
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(JobQueueIntegrationTests.JobScript("CompleteWithMetrics"),
                [queue.StreamKey, queue.EventsStreamKey, queue.StreamKey + ":metrics"],
                [group, "owner", second.Id.Value, 1, "completed", "", "", 0, Array.Empty<byte>(), 5])).IsEqualTo(1);
            await Assert.That((await RespireJobMetrics.ReadAsync(client, queue.StreamKey)).Sum(bucket => bucket.Completed)).IsEqualTo(1);
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

            // Application-group controls use the same explicit keys even when a client prefix
            // supplies the effective Cluster hash tag instead of the queue's own tag.
            await queue.SetRateLimitAsync(null);
            const string applicationGroup = "limited:{foreign}:東京";
            await queue.SetGroupOptionsAsync(applicationGroup, new() { MaxConcurrency = 1 });
            await queue.PauseGroupAsync(applicationGroup);
            await queue.EnqueueScheduledAsync("limited-1", "p", new() { Group = applicationGroup });
            await queue.EnqueueScheduledAsync("limited-2", "p", new() { Group = applicationGroup });
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Length).IsEqualTo(0);
            await queue.ResumeGroupAsync(applicationGroup);
            var leased = (await JobGroupIntegrationTests.Read(client, queue, "limited")).Single();
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Length).IsEqualTo(0);
            var release = (RespireScript)typeof(RespireJobQueue<string>).Assembly.GetType("Respire.Streaming.JobGroupControls")!
                .GetField("Release", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            await Assert.That(await client.Scripts.ExecuteIntegerAsync(release, keys, ["limited", leased.Id.Value, 1])).IsEqualTo(1);
            await Assert.That((await JobGroupIntegrationTests.Read(client, queue, "limited")).Single()
                .GetString("_respire.job.id")).IsEqualTo("limited-2");
        }
    }
}
