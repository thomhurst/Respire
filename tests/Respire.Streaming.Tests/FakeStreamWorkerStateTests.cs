using Respire.Testing;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Streaming.Tests;

public class FakeStreamWorkerStateTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PendingQueriesReportCountsOwnersIdleAndHistoryRedelivery(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        for (var i = 1; i <= 3; i++)
            await client.Streams.AddAsync("s", new StreamAddOptions { Id = $"{i}-0" }, ("payload", i));
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 2 });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 1 }, RespireStreamId.Beginning);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        var summary = await client.Streams.PendingSummaryAsync("s", "g");
        await Assert.That(summary.Count).IsEqualTo(2);
        await Assert.That(summary.SmallestId).IsEqualTo((RespireStreamId?)"1-0");
        await Assert.That(summary.GreatestId).IsEqualTo((RespireStreamId?)"2-0");
        await Assert.That(summary.Consumers.Single().Pending).IsEqualTo(2);
        var all = await client.Streams.PendingAsync("s", "g");
        await Assert.That(all[0].DeliveryCount).IsEqualTo(2);
        await Assert.That(all[0].IdleTime).IsEqualTo(TimeSpan.FromMilliseconds(250));
        await Assert.That(all[1].DeliveryCount).IsEqualTo(1);
        await Assert.That(all[1].IdleTime).IsEqualTo(TimeSpan.FromMilliseconds(350));
        var filtered = await client.Streams.PendingAsync(new StreamPendingOptions
        {
            MinIdle = TimeSpan.FromMilliseconds(300), Consumer = "a",
        }, "s", "g");
        await Assert.That(filtered.Single().Id).IsEqualTo((RespireStreamId)"2-0");
        var exclusive = await client.Streams.PendingAsync(new StreamPendingOptions { Start = "(1-0", End = "2-0" }, "s", "g");
        await Assert.That(exclusive.Single().Id).IsEqualTo((RespireStreamId)"2-0");
        await Assert.That(await client.Streams.PendingAsync(new StreamPendingOptions { End = "(1-0" }, "s", "g")).IsEmpty();
        await Assert.That(await client.Streams.PendingAsync("s", "g", consumer: "other")).IsEmpty();
        await client.Streams.AcknowledgeAsync("s", "g", ["1-0", "2-0"]);
        var empty = await client.Streams.PendingSummaryAsync("s", "g");
        await Assert.That(empty.Count).IsEqualTo(0);
        await Assert.That(empty.SmallestId).IsNull();
        await Assert.That(empty.Consumers.Length).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GroupInfoRetainsConsumerCountAfterAcknowledgement(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Streams.AddAsync("s", new StreamAddOptions { Id = "1-0" }, ("f", "value"));
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        var entries = await client.Streams.ReadGroupOnceAsync("s", "g", "a");
        await entries.Single().AckAsync();
        var group = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(group.Pending).IsEqualTo(0);
        await Assert.That(group.Consumers).IsEqualTo(1);
        await Assert.That(group.EntriesRead).IsEqualTo(1L);
        await Assert.That(group.Lag).IsEqualTo(0L);
        await Assert.That(group.LastDeliveredId).IsEqualTo((RespireStreamId)"1-0");
    }
}
