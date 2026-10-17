using System.Text;
using Respire.Testing.Containers;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class ReliableTopicClusterTests
{
    [Test]
    [NotInParallel]
    public async Task FanoutRetryDeadLettersAndReplayUseOneResolvedClusterSlot()
    {
        await using var fixture = await RespireContainerFixture.StartAsync(new()
        {
            Topology = RespireContainerTopology.Cluster, Image = "redis:7.4-alpine",
        });
        await using var root = await RespireClient.ConnectAsync(fixture.CreateOptions());
        foreach (var prefix in new[] { "tenant:", "{tenant}:" })
        {
            var client = root.WithKeyPrefix(prefix);
            var topic = new RespireReliableTopic<string>(client, "cluster", Encoding.UTF8.GetBytes, Encoding.UTF8.GetString,
                deliveryLimit: 1, maximumLength: 10);
            var a = await topic.SubscribeAsync("a:{foreign}:東京");
            var b = await topic.SubscribeAsync("b");
            var id = await topic.PublishAsync("payload");
            var message = (await a.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
            await Assert.That(await a.NackAsync(message)).IsTrue();
            await Assert.That((await a.ReceiveAsync(TimeSpan.FromMinutes(1))).Length).IsEqualTo(0);
            await Assert.That((await client.Streams.ReadAsync(a.DeadLetterStreamKey)).Single().GetString("source_id"))
                .IsEqualTo(id.ToString());
            var independent = (await b.ReceiveAsync(TimeSpan.FromMinutes(1))).Single();
            await Assert.That(independent.Id).IsEqualTo(id);
            await Assert.That(await b.AckAsync(independent)).IsTrue();
            await b.SeekAsync(RespireStreamId.Beginning);
            await Assert.That((await b.ReceiveAsync(TimeSpan.FromMinutes(1))).Single().Payload).IsEqualTo("payload");
        }
    }
}
