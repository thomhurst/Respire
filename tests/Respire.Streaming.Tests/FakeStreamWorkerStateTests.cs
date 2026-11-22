using Respire.Internal;
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
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });

        for (var i = 1; i <= 3; i++)
        {
            await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = $"{i}-0" }, ("payload", i));
        }

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
        var filtered = await client.Streams.PendingAsync(new RespireStreamPendingOptions
        {
            MinIdle = TimeSpan.FromMilliseconds(300),
            Consumer = "a",
        }, "s", "g");
        await Assert.That(filtered.Single().Id).IsEqualTo((RespireStreamId)"2-0");
        var exclusive = await client.Streams.PendingAsync(new RespireStreamPendingOptions { Start = "(1-0", End = "2-0" }, "s", "g");
        await Assert.That(exclusive.Single().Id).IsEqualTo((RespireStreamId)"2-0");
        await Assert.That(await client.Streams.PendingAsync(new RespireStreamPendingOptions { End = "(1-0" }, "s", "g")).IsEmpty();
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
    public async Task AutoClaimScansAfterAnExclusiveStartAndReturnsAnInclusiveCursor(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1234-0" }, ("f", "first"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1234-1" }, ("f", "second"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1235-0" }, ("f", "third"));
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("s", "g", "original", new() { Count = 3 });

        var page = await client.Streams.ClaimPendingAsync("s", "g", "next", TimeSpan.Zero, start: "(1234-0", count: 1);
        await Assert.That(page.Entries.Single().Id).IsEqualTo((RespireStreamId)"1234-1");
        await Assert.That(page.NextStart).IsEqualTo((RespireStreamId)"1235-0");

        var ids = await client.Streams.ClaimPendingIdsAsync("s", "g", "next", TimeSpan.Zero, start: "(1234", count: 1);
        await Assert.That(ids.Ids.Single()).IsEqualTo((RespireStreamId)"1234-1");
        await Assert.That(ids.NextStart).IsEqualTo(page.NextStart);

        var resumed = await client.Streams.ClaimPendingIdsAsync("s", "g", "next", TimeSpan.Zero, start: ids.NextStart, count: 1);
        await Assert.That(resumed.Ids.Single()).IsEqualTo((RespireStreamId)"1235-0");
        await Assert.That(resumed.NextStart).IsEqualTo((RespireStreamId)"0-0");

        var exhausted = await client.Streams.ClaimPendingAsync("s", "g", "next", TimeSpan.Zero, start: "(1235-0", count: 1);
        await Assert.That(exhausted.Entries).IsEmpty();
        await Assert.That(exhausted.NextStart).IsEqualTo((RespireStreamId)"0-0");
    }

    [Test]
    public async Task AutoClaimRejectsAnExclusiveStartAtTheLargestNumericId()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning, createStream: true);

        await Assert.That(async () => await client.Streams.ClaimPendingAsync("s", "g", "next", TimeSpan.Zero,
                start: "(18446744073709551615-18446744073709551615"))
            .Throws<RespireServerException>()
            .WithMessage("ERR invalid start ID for the interval");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GroupInfoRetainsConsumerCountAfterAcknowledgement(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1-0" }, ("f", "value"));
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

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task GroupLagRecoversAfterReadingPastAnUnreadDeletion(int protocol, bool nativeClaim)
    {
        await using var server = new RespireFakeServer(new RespireFakeClock(), true, true, new Version(8, 10));
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-0" }, ("f", "first"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-1" }, ("f", "middle"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-2" }, ("f", "last"));
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 1 });

        var before = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(before.EntriesRead).IsEqualTo(1L);
        await Assert.That(before.Lag).IsEqualTo(2L);

        await Assert.That(await client.Streams.RemoveAsync("s", "1000-1")).IsEqualTo(1L);
        var fragmented = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(fragmented.EntriesRead).IsEqualTo(1L);
        await Assert.That(fragmented.Lag).IsNull();

        if (nativeClaim)
        {
            // The clock has not advanced, so the pending entry is too young to reclaim.
            using var reply = await client.Scripts.ExecuteAsync(StreamWorkerScripts.CapabilityClaim, ["s"],
                ["g", "a", 1, "0-0", 1]);
        }
        else
        {
            await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 1 });
        }

        var caughtUp = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(caughtUp.LastDeliveredId).IsEqualTo((RespireStreamId)"1000-2");
        await Assert.That(caughtUp.EntriesRead).IsEqualTo(3L);
        await Assert.That(caughtUp.Lag).IsEqualTo(0L);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GroupLagCountsRemainingEntriesAfterPrefixDeletion(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespireProtocol)protocol });
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-0" }, ("f", "first"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-1" }, ("f", "second"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-2" }, ("f", "third"));
        await client.Streams.AddAsync("s", new RespireStreamAddOptions { Id = "1000-3" }, ("f", "last"));
        await client.Streams.CreateGroupAsync("s", "g", RespireStreamId.Beginning);
        await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 1 });

        await Assert.That(await client.Streams.RemoveAsync("s", "1000-0", "1000-1")).IsEqualTo(2L);
        var afterPrefix = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(afterPrefix.EntriesRead).IsEqualTo(1L);
        await Assert.That(afterPrefix.Lag).IsEqualTo(2L);

        await client.Streams.ReadGroupOnceAsync("s", "g", "a", new() { Count = 1 });
        var afterRead = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(afterRead.EntriesRead).IsEqualTo(3L);
        await Assert.That(afterRead.Lag).IsEqualTo(1L);

        await Assert.That(await client.Streams.RemoveAsync("s", "1000-2", "1000-3")).IsEqualTo(2L);
        var empty = (await client.Streams.GroupInfoAsync("s")).Single();
        await Assert.That(empty.EntriesRead).IsEqualTo(3L);
        await Assert.That(empty.Lag).IsEqualTo(0L);
    }
}
