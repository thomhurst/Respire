using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<VersionedServerFixture>(Shared = SharedType.PerTestSession)]
public class StreamConsumerOptionIntegrationTests(VersionedServerFixture fixture)
{
    [Test]
    [Arguments(2, 0)]
    [Arguments(3, 0)]
    [Arguments(2, 1)]
    [Arguments(3, 1)]
    [Arguments(2, 2)]
    [Arguments(3, 2)]
    public async Task NoAckSkipsNewPendingEntriesAcrossStreamsAndSurfaces(int protocol, int surface)
    {
        var lease = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var owner = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var client = owner.WithKeyPrefix("tenant:");
        foreach (var key in new[] { "{s}:one", "{s}:two" })
        {
            await client.Streams.CreateGroupAsync(key, "g", "0");
            await client.Streams.AddAsync(key, new StreamAddOptions { Id = "1-0" }, ("f", "value"));
        }
        (RespireKey Key, RespireStreamId After)[] streams = [("{s}:one", ">"), ("{s}:two", ">")];
        var options = new StreamReadOptions { NoAck = true };
        RespireStreamReadResult[] page;
        if (surface == 0) page = await client.Streams.ReadGroupAsync(streams, "g", "c", options);
        else
        {
            using var batch = surface == 1 ? client.CreateBatch() : null;
            await using var transaction = surface == 2 ? client.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Streams.ReadGroup(streams, "g", "c", options);
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            page = pending.Result;
        }
        page.Select(row => row.Key.ToString()).Should().Equal("{s}:one", "{s}:two");
        foreach (var row in page)
        {
            row.Entries.Should().ContainSingle();
            (await client.Streams.PendingSummaryAsync(row.Key, "g")).Count.Should().Be(0);
            await client.Streams.AddAsync(row.Key, new StreamAddOptions { Id = "2-0" }, ("f", "pending"));
            (await client.Streams.ReadGroupOnceAsync(row.Key, "g", "c")).Should().ContainSingle();
            // Redis ignores NOACK and CLAIM for a numeric pending-history cursor.
            var history = await client.Streams.ReadGroupOnceAsync(row.Key, "g", "c",
                options with { ClaimMinIdle = TimeSpan.FromDays(1) }, "0");
            history.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"2-0");
            (await client.Streams.PendingSummaryAsync(row.Key, "g")).Count.Should().Be(1);
        }
    }

    [Test]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task ReadGroupClaimPreservesMetadataAndNoAckOnlyAffectsNewEntries(int protocol, bool noAck)
    {
        // CLAIM is deliberately tested on 8.4; the older-server recovery cases below never send it.
        var lease = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        foreach (var key in new[] { "one", "two" })
        {
            await client.Streams.CreateGroupAsync(key, "g", "0");
            await client.Streams.AddAsync(key, new StreamAddOptions { Id = "1-0" }, ("f", "old"));
            await client.Streams.ReadGroupOnceAsync(key, "g", "original");
            await client.Streams.ClaimAsync(new StreamClaimOptions { IdleTime = TimeSpan.FromSeconds(10), RetryCount = 7 },
                key, "g", "original", TimeSpan.Zero, "1-0");
            await client.Streams.AddAsync(key, new StreamAddOptions { Id = "2-0" }, ("f", "new"));
        }
        var results = await client.Streams.ReadGroupAsync([("one", ">"), ("two", ">")], "g", "replacement",
            new StreamReadOptions { ClaimMinIdle = TimeSpan.FromSeconds(5), NoAck = noAck });
        results.Should().HaveCount(2);
        foreach (var row in results)
        {
            row.Entries.Select(entry => entry.Id).Should().Equal((RespireStreamId)"1-0", (RespireStreamId)"2-0");
            row.Entries[0].PreviousIdleTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(10));
            row.Entries[0].PreviousDeliveryCount.Should().Be(7);
            // Redis 8.4 includes a four-element reply for new entries too, with zero prior metadata.
            row.Entries[1].PreviousIdleTime.Should().Be(TimeSpan.Zero);
            row.Entries[1].PreviousDeliveryCount.Should().Be(0);
            var pending = await client.Streams.PendingAsync(row.Key, "g");
            pending.Should().HaveCount(noAck ? 1 : 2);
            pending[0].Consumer.Should().Be("replacement");
            pending[0].DeliveryCount.Should().Be(8);
            (await row.Entries[0].AckAsync()).Should().BeTrue();
        }
    }

    [Test]
    [Arguments("redis:6.2-alpine", 2)]
    [Arguments("redis:6.2-alpine", 3)]
    [Arguments("redis:8.4-alpine", 2)]
    [Arguments("redis:8.4-alpine", 3)]
    public async Task PendingFiltersAndClaimMetadataMatchServer(string image, int protocol)
    {
        var lease = await fixture.LeaseAsync(image);
        await using var owner = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        var client = owner.WithKeyPrefix("tenant:");
        await client.Streams.CreateGroupAsync("events", "g", "0");
        foreach (var id in new[] { "1-0", "2-0", "3-0" })
            await client.Streams.AddAsync("events", new StreamAddOptions { Id = id }, ("f", id));
        await client.Streams.ReadGroupOnceAsync("events", "g", "original", new StreamReadOptions { Count = 1 });
        var claimed = await client.Streams.ClaimAsync(new StreamClaimOptions
        { Force = true, IdleTime = TimeSpan.FromSeconds(10), RetryCount = 7, LastId = "2-0" },
            "events", "g", "replacement", TimeSpan.Zero, "1-0", "2-0", "99-0");
        claimed.Select(entry => entry.Id).Should().Equal((RespireStreamId)"1-0", (RespireStreamId)"2-0");
        var filter = new StreamPendingOptions { MinIdle = TimeSpan.FromSeconds(5), Consumer = "replacement", Start = "(1-0" };
        var pending = await client.Streams.PendingAsync(filter, "events", "g");
        pending.Should().ContainSingle().Which.Id.Should().Be((RespireStreamId)"2-0");
        pending[0].DeliveryCount.Should().Be(7);
        (await client.Streams.PendingAsync(filter with { MinIdle = TimeSpan.FromDays(1) }, "events", "g")).Should().BeEmpty();
        // LASTID advances delivery past the forced pending entry.
        (await client.Streams.ReadGroupOnceAsync("events", "g", "original")).Should().ContainSingle()
            .Which.Id.Should().Be((RespireStreamId)"3-0");
        var timestamp = (await client.Server.TimeAsync()).AddSeconds(-20);
        var ids = await client.Streams.ClaimIdsAsync(new StreamClaimOptions { DeliveryTime = timestamp },
            "events", "g", "ids-only", TimeSpan.Zero, "1-0");
        ids.Should().Equal((RespireStreamId)"1-0");
        var timed = (await client.Streams.PendingAsync("events", "g", consumer: "ids-only")).Single();
        timed.IdleTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(19));
        timed.DeliveryCount.Should().Be(7);
        var auto = await client.Streams.ClaimPendingIdsAsync("events", "g", "auto", TimeSpan.Zero, count: 1);
        auto.Ids.Should().Equal((RespireStreamId)"1-0");
        auto.DeletedIds.Should().BeEmpty();
        auto.NextStart.Should().NotBe((RespireStreamId)"0-0");
        (await client.Streams.PendingAsync("events", "g", consumer: "auto")).Single().DeliveryCount.Should().Be(7);
        var next = await client.Streams.ClaimPendingIdsAsync("events", "g", "auto", TimeSpan.Zero, auto.NextStart);
        next.Ids.Should().Equal((RespireStreamId)"2-0", (RespireStreamId)"3-0");
        next.NextStart.Should().Be((RespireStreamId)"0-0");
        (await claimed[1].AckAsync()).Should().BeTrue();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AutoClaimIdsReportsDeletedPendingEntries(int protocol)
    {
        var lease = await fixture.LeaseAsync("redis:8.4-alpine");
        await using var client = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        await client.Streams.CreateGroupAsync("events", "g", "0");
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "value"));
        await client.Streams.ReadGroupOnceAsync("events", "g", "original");
        await client.Streams.RemoveAsync("events", "1-0");
        var result = await client.Streams.ClaimPendingIdsAsync("events", "g", "replacement", TimeSpan.Zero);
        result.Ids.Should().BeEmpty();
        result.DeletedIds.Should().Equal((RespireStreamId)"1-0");
        result.NextStart.Should().Be((RespireStreamId)"0-0");
        (await client.Streams.PendingSummaryAsync("events", "g")).Count.Should().Be(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingClaimWakesWhenPendingEntryBecomesEligibleAndRemainsCancellable(int protocol)
    {
        var lease = await fixture.LeaseAsync("redis:8.4-alpine");
        var name = $"claim-block-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(lease.ConnectionString(protocol))
            with { Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(lease.ConnectionString(protocol));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.Streams.CreateGroupAsync("events", "g", "0");
        await client.Streams.AddAsync("events", new StreamAddOptions { Id = "1-0" }, ("f", "value"));
        await client.Streams.ReadGroupOnceAsync("events", "g", "original");
        var options = new StreamReadOptions { ClaimMinIdle = TimeSpan.FromSeconds(2), WaitFor = Timeout.InfiniteTimeSpan };
        var read = client.Streams.ReadGroupOnceAsync("events", "g", "replacement", options, cancellationToken: timeout.Token).AsTask();
        await WaitForBlockedAsync();
        (await client.SetAsync("ordinary", "responsive", cancellationToken: timeout.Token)).Should().BeTrue();
        var claimed = (await read).Single();
        claimed.Id.Should().Be((RespireStreamId)"1-0");
        claimed.PreviousDeliveryCount.Should().Be(1);
        claimed.PreviousIdleTime.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        (await claimed.AckAsync()).Should().BeTrue();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var next = client.Streams.ReadGroupOnceAsync("events", "g", "replacement", options, cancellationToken: cancel.Token).AsTask();
        await WaitForBlockedAsync();
        cancel.Cancel();
        Func<Task> cancelled = async () => await next;
        await cancelled.Should().ThrowAsync<OperationCanceledException>();

        async Task WaitForBlockedAsync()
        {
            while (true)
            {
                var clients = await observer.Server.ClientsAsync(timeout.Token);
                if (clients.Any(connection => connection.Name == name && connection.Flags.Contains('b'))) return;
                await Task.Delay(10, timeout.Token);
            }
        }
    }
}
