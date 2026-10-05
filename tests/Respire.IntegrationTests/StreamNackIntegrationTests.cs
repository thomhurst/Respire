using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// XNACK needs Redis 8.8; the keyspace-notification fixture already runs it with per-test databases.
[ClassDataSource<KeyNotificationRedisContainer>(Shared = SharedType.PerTestSession)]
public class StreamNackIntegrationTests(KeyNotificationRedisContainer fixture)
{
    [Test]
    [MatrixDataSource]
    public async Task ModesReleaseOwnershipAndPreservePerIdPelState([Matrix(2, 3)] int protocol,
        [Matrix(0, 1, 2)] int surface, [Matrix(StreamNackMode.Silent, StreamNackMode.Fail, StreamNackMode.Fatal)] StreamNackMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"nack:{Guid.NewGuid():N}:");
        try
        {
            await SeedAsync(view);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var delivered = 0;
            await foreach (var entry in view.Streams.ReadGroupAsync("events", "g", "c", batchSize: 2, cancellationToken: timeout.Token))
                if (++delivered == 2) break;
            RespireStreamId[] ids = ["1-0", "1-0", "2-0", "3-0", "99-0"];
            long count;
            if (surface == 0) count = await view.Streams.NegativeAcknowledgeAsync("events", "g", mode, ids);
            else
            {
                using var batch = surface == 1 ? view.CreateBatch() : null;
                await using var transaction = surface == 2 ? view.CreateTransaction() : null;
                IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
                var result = queue.Streams.NegativeAcknowledge("events", "g", mode, ids);
                if (transaction is not null) await transaction.CommitAsync(); else await batch!.ExecuteAsync();
                count = result.Result;
            }
            count.Should().Be(3, "Redis counts repeated existing IDs, while skipping absent PEL IDs without FORCE");
            var pending = await view.Streams.PendingAsync("events", "g");
            pending.Select(x => x.Id.Value).Should().Equal("1-0", "2-0");
            pending.Should().OnlyContain(x => x.Consumer == "" && x.IdleTime == TimeSpan.FromMilliseconds(-1));
            var expected = mode switch { StreamNackMode.Silent => 0, StreamNackMode.Fail => 1, _ => long.MaxValue };
            pending.Should().OnlyContain(x => x.DeliveryCount == expected);
            (await view.Streams.CountAsync("events")).Should().Be(3);
            (await view.Streams.PendingSummaryAsync("events", "g")).Count.Should().Be(2);
            if (mode != StreamNackMode.Fatal)
            {
                var claimed = await view.Streams.ClaimAsync("events", "g", "next", TimeSpan.FromDays(365), "1-0", "2-0");
                claimed.Should().HaveCount(2);
                (await view.Streams.PendingAsync("events", "g")).Should().OnlyContain(x => x.Consumer == "next" && x.DeliveryCount == expected + 1);
            }
        }
        finally { await view.Keys.DeleteAsync("events"); }
    }

    [Test]
    [MatrixDataSource]
    public async Task ForceAndRetryCountOverrideEachMode([Matrix(2, 3)] int protocol,
        [Matrix(StreamNackMode.Silent, StreamNackMode.Fail, StreamNackMode.Fatal)] StreamNackMode mode)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"nack-force:{Guid.NewGuid():N}:");
        try
        {
            await SeedAsync(view);
            (await view.Streams.NegativeAcknowledgeAsync("events", "g", mode, new() { Force = true }, ["1-0", "99-0"])).Should().Be(1);
            var first = (await view.Streams.PendingAsync("events", "g")).Single();
            first.Consumer.Should().BeEmpty();
            first.DeliveryCount.Should().Be(mode == StreamNackMode.Fatal ? long.MaxValue : 0);
            (await view.Streams.NegativeAcknowledgeAsync("events", "g", mode, new() { Force = true, RetryCount = 7 }, ["1-0", "2-0", "99-0"])).Should().Be(2);
            var pending = await view.Streams.PendingAsync("events", "g");
            pending.Should().HaveCount(2).And.OnlyContain(x => x.DeliveryCount == 7 && x.Consumer == "");
            (await view.Streams.NegativeAcknowledgeAsync("events", "g", mode, new() { RetryCount = 0 }, ["1-0"])).Should().Be(1);
            (await view.Streams.PendingAsync("events", "g")).Single(x => x.Id == (RespireStreamId)"1-0").DeliveryCount.Should().Be(0);
            Func<Task> missing = async () => await view.Streams.NegativeAcknowledgeAsync("events", "absent", mode, "1-0");
            await missing.Should().ThrowAsync<RespireServerException>();
        }
        finally { await view.Keys.DeleteAsync("events"); }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BinaryGroupNamesRemainUnmodified(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var key = $"binary-nack:{Guid.NewGuid():N}";
        byte[] group = [255, 0, 32];
        try
        {
            await client.Streams.AddAsync(key, new StreamAddOptions { Id = "1-0" }, ("f", "v"));
            using var created = await client.ExecuteAsync(RespireCommands.Stream.XGROUP_CREATE, key, group, "0");
            (await client.Streams.NegativeAcknowledgeAsync(key, group, StreamNackMode.Fail, new() { Force = true }, ["1-0"])).Should().Be(1);
        }
        finally { await client.Keys.DeleteAsync(key); }
    }

    private static async Task SeedAsync(IRespireClient client)
    {
        for (var id = 1; id <= 3; id++) await client.Streams.AddAsync("events", new StreamAddOptions { Id = $"{id}-0" }, ("f", "v"));
        await client.Streams.CreateGroupAsync("events", "g", RespireStreamId.Beginning);
    }
}
