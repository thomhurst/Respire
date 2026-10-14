using System.Text;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class RecurringJobClusterTests
{
    [Test]
    [NotInParallel]
    public async Task PrefixedSchedulesCommitAndRemoveInARealCluster()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireJobQueue<string>(client, "recurring-cluster", Encoding.UTF8.GetBytes);
            var clock = new ManualClock();
            var scheduler = queue.CreateScheduler(clock);
            await scheduler.UpsertAsync("id{untrusted}", "payload", new() { Interval = TimeSpan.FromMinutes(1) });
            clock.UtcNow += TimeSpan.FromMinutes(1);
            await Assert.That(await scheduler.RunDueAsync()).IsEqualTo(1);
            await Assert.That(await queue.CreateScheduler(clock).RunDueAsync()).IsEqualTo(0);
            await Assert.That(await scheduler.RemoveAsync("id{untrusted}")).IsTrue();
            await Assert.That((await client.Streams.ReadAsync(queue.StreamKey)).Single()
                .GetString("_respire.job.payload")).IsEqualTo("payload");
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
