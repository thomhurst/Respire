using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class ListMoveManyIntegrationTests(ModernRedisTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Redis810WatchTracksListMoveMutationsAndRejections(int protocol)
    {
        // The parity scenarios use fixed key names, so each protocol owns database 2 or 3 of the shared server.
        await FakeTransactionParityTests.AssertWatchTracksMutationsAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol, Database = protocol }, useFake: false);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AllOrderingModesMatchRedisAcrossImmediateAndQueuedMoves(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var view = client.WithKeyPrefix($"moves:{Guid.NewGuid():N}:");
        (RespireListSide From, RespireListSide To, RespireListMoveOrder Order, string[] Values)[] cases =
        [
            (RespireListSide.Left, RespireListSide.Left, RespireListMoveOrder.OneByOne, ["b", "a"]),
            (RespireListSide.Left, RespireListSide.Right, RespireListMoveOrder.OneByOne, ["a", "b"]),
            (RespireListSide.Right, RespireListSide.Left, RespireListMoveOrder.OneByOne, ["c", "d"]),
            (RespireListSide.Right, RespireListSide.Right, RespireListMoveOrder.OneByOne, ["d", "c"]),
            (RespireListSide.Left, RespireListSide.Left, RespireListMoveOrder.Bulk, ["a", "b"]),
            (RespireListSide.Left, RespireListSide.Right, RespireListMoveOrder.Bulk, ["a", "b"]),
            (RespireListSide.Right, RespireListSide.Left, RespireListMoveOrder.Bulk, ["c", "d"]),
            (RespireListSide.Right, RespireListSide.Right, RespireListMoveOrder.Bulk, ["c", "d"]),
        ];
        foreach (var mode in new[] { "immediate", "blocking", "batch", "transaction" })
        foreach (var same in new[] { false, true })
        foreach (var test in cases)
        {
            await view.Keys.DeleteAsync("s", "d");
            await view.Lists.RightPushAsync("s", "a", "b", "c", "d");
            if (!same) await view.Lists.RightPushAsync("d", "x");
            var destination = same ? "s" : "d";
            var result = await Move(view, mode, destination, test.From, test.To, test.Order);
            result.Should().Equal(test.Values);
            string[] remainder = test.From == RespireListSide.Left ? ["c", "d"] : ["a", "b"];
            var originalDestination = same ? remainder : ["x"];
            var expected = test.To == RespireListSide.Left
                ? test.Values.Concat(originalDestination) : originalDestination.Concat(test.Values);
            (await view.Lists.RangeAsync(destination)).Should().Equal(expected);
            if (!same) (await view.Lists.RangeAsync("s")).Should().Equal(remainder);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MissingSourcesExactCountsTimeoutsAndWrongTypes(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespireProtocol)protocol });
        var view = client.WithKeyPrefix($"moves:{Guid.NewGuid():N}:");
        (await view.Lists.MoveManyAsync("s", "d")).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", countMode: RespireListMoveCountMode.Exactly)).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.FromMilliseconds(10))).Should().BeNull();
        await view.Lists.RightPushAsync("s", "a");
        (await view.Lists.MoveManyAsync("s", "d", count: 2, countMode: RespireListMoveCountMode.Exactly)).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", count: 2, countMode: RespireListMoveCountMode.Exactly, waitFor: TimeSpan.FromMilliseconds(10))).Should().BeNull();
        await view.SetAsync("d", "wrong");
        Func<Task> invalid = async () => await view.Lists.MoveManyAsync("s", "d");
        await invalid.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        (await view.Lists.RangeAsync("s")).Should().Equal("a");
        await view.Keys.DeleteAsync("d");
        (await view.Lists.MoveManyAsync("s", "d", 10)).Should().Equal("a");
        (await view.Keys.ExistsAsync("s")).Should().BeFalse();
        await using var transaction = view.CreateTransaction();
        var empty = transaction.Lists.MoveMany("s", "d", count: 2, countMode: RespireListMoveCountMode.Exactly);
        await transaction.CommitAsync();
        empty.Result.Should().BeNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingExactlyDoesNotStallMultiplexerAndCancellationReleasesLease(int protocol)
    {
        var name = $"moves-{Guid.NewGuid():N}";
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with
        { Protocol = (RespireProtocol)protocol, Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var view = client.WithKeyPrefix(name + ":");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var move = view.Lists.MoveManyAsync("s", "d", count: 2, countMode: RespireListMoveCountMode.Exactly,
            waitFor: null, cancellationToken: cancel.Token).AsTask();
        await WaitForBlocked(observer, name, deadline.Token);
        (await view.SetAsync("ordinary", "responsive", cancellationToken: deadline.Token)).Should().BeTrue();
        await view.Lists.RightPushAsync("s", ["a"], deadline.Token);
        await WaitForBlocked(observer, name, deadline.Token);
        move.IsCompleted.Should().BeFalse();
        await view.Lists.RightPushAsync("s", ["b"], deadline.Token);
        (await move.WaitAsync(deadline.Token)).Should().Equal("a", "b");
        var blocked = view.Lists.MoveManyAsync("s", "d", waitFor: null, cancellationToken: cancel.Token).AsTask();
        var blockedId = await WaitForBlocked(observer, name, deadline.Token);
        cancel.Cancel();
        Func<Task> canceled = async () => await blocked.WaitAsync(deadline.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        // Local socket disposal does not acknowledge Redis processing the disconnect. Wait for
        // that exact blocked client to disappear before a write could satisfy its old command.
        while ((await observer.Server.ClientsAsync(deadline.Token)).Any(client => client.Id == blockedId))
            await Task.Delay(10, deadline.Token);
        await view.Lists.RightPushAsync("s", "next");
        (await view.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.FromSeconds(1), cancellationToken: deadline.Token)).Should().Equal("next");
    }

    private static async Task<long> WaitForBlocked(IRespireClient observer, string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            var blocked = (await observer.Server.ClientsAsync(cancellationToken))
                .FirstOrDefault(x => x.Name == name && x.Flags.Contains('b') && x.Command == "blmovem");
            if (blocked is not null) return blocked.Id;
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task<string[]?> Move(IRespireClient client, string mode, string destination,
        RespireListSide from, RespireListSide to, RespireListMoveOrder order)
    {
        if (mode is "immediate" or "blocking")
            return await (mode == "blocking"
                ? client.Lists.MoveManyAsync("s", destination, TimeSpan.FromSeconds(1), 2, from, to, order: order)
                : client.Lists.MoveManyAsync("s", destination, 2, from, to, order: order));
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Lists.MoveMany("s", destination, 2, from, to, order: order);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
