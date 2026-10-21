using System.Text;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class ReliableQueueSchedulingClusterTests
{
    [Test]
    [NotInParallel]
    public async Task SchedulingCancellationAndPromotionUseOneResolvedClusterSlot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var cancellationToken = timeout.Token;
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        }, cancellationToken);
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions(), cancellationToken);
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var queue = new RespireReliableQueue<string>(client, "scheduled-cluster",
                Encoding.UTF8.GetBytes, Encoding.UTF8.GetString);
            var payloadKey = queue.StreamKey + ":scheduled";
            var deadlineKey = queue.StreamKey + ":scheduled-due";

            // A due schedule is still cancellable until a receiver promotes it.
            var cancelled = await queue.ScheduleAsync("cancelled", TimeSpan.Zero, cancellationToken);
            var scheduled = await queue.ScheduleAsync("payload-東京", TimeSpan.FromMinutes(5), cancellationToken);
            await AssertPendingCountAsync(client, queue, 2, cancellationToken);
            await Assert.That(await queue.CancelScheduledAsync(cancelled, cancellationToken)).IsTrue();
            await Assert.That(await queue.CancelScheduledAsync(cancelled, cancellationToken)).IsFalse();
            await AssertPendingCountAsync(client, queue, 1, cancellationToken);
            await Assert.That(await client.Hashes.GetAsync<string>(payloadKey, scheduled, cancellationToken))
                .IsEqualTo("payload-東京");
            await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 10,
                cancellationToken: cancellationToken)).Length).IsEqualTo(0);
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey, cancellationToken)).IsEqualTo(0);
            await AssertNoUnprefixedStateAsync(root, queue, cancellationToken);

            // Make the surviving schedule due without a wall-clock wait. Only ReceiveAsync promotes it.
            await client.SortedSets.AddAsync(deadlineKey, scheduled, 0, cancellationToken);
            var message = (await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 10,
                cancellationToken: cancellationToken)).Single();
            await Assert.That(message.Payload).IsEqualTo("payload-東京");
            await Assert.That(message.DeliveryCount).IsEqualTo(1);
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey, cancellationToken)).IsEqualTo(1);
            await Assert.That((await client.Streams.ReadAsync(queue.StreamKey,
                cancellationToken: cancellationToken)).Single().Id).IsEqualTo(message.Id);
            await AssertPendingCountAsync(client, queue, 0, cancellationToken);
            await Assert.That(await queue.CancelScheduledAsync(scheduled, cancellationToken)).IsFalse();
            await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 10,
                cancellationToken: cancellationToken)).Length).IsEqualTo(0);
            await AssertNoUnprefixedStateAsync(root, queue, cancellationToken);

            await Assert.That(await queue.AckAsync(message, cancellationToken)).IsTrue();
            await Assert.That(await client.Streams.CountAsync(queue.StreamKey, cancellationToken)).IsEqualTo(0);
            await Assert.That((await queue.ReceiveAsync(TimeSpan.FromMinutes(1), max: 10,
                cancellationToken: cancellationToken)).Length).IsEqualTo(0);
            await Assert.That(await client.Keys.ExistsAsync(payloadKey, cancellationToken)).IsFalse();
            await Assert.That(await client.Keys.ExistsAsync(deadlineKey, cancellationToken)).IsFalse();
            await AssertNoUnprefixedStateAsync(root, queue, cancellationToken);
        }
    }

    private static async Task AssertPendingCountAsync(IRespireClient client, RespireReliableQueue<string> queue,
        int expected, CancellationToken cancellationToken)
    {
        await Assert.That(await client.Hashes.CountAsync(queue.StreamKey + ":scheduled", cancellationToken))
            .IsEqualTo(expected);
        await Assert.That(await client.SortedSets.CountAsync(queue.StreamKey + ":scheduled-due", cancellationToken))
            .IsEqualTo(expected);
    }

    private static async Task AssertNoUnprefixedStateAsync(IRespireClient root, RespireReliableQueue<string> queue,
        CancellationToken cancellationToken)
    {
        foreach (var suffix in new[] { "", ":due", ":receipts", ":attempts", ":dead", ":scheduled", ":scheduled-due" })
            await Assert.That(await root.Keys.ExistsAsync(queue.StreamKey + suffix, cancellationToken)).IsFalse();
    }
}
