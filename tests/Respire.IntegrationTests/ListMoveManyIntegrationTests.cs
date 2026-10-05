using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

public sealed class Redis810ListMoveTestContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");

[ClassDataSource<Redis810ListMoveTestContainer>(Shared = SharedType.PerTestSession)]
public class ListMoveManyIntegrationTests(Redis810ListMoveTestContainer fixture)
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Redis810WatchTracksListMoveMutationsAndRejections(int protocol)
    {
        await FakeTransactionParityTests.AssertWatchTracksMutationsAsync(
            RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol, Database = protocol }, useFake: false);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AllOrderingModesMatchRedisAcrossImmediateAndQueuedMoves(int protocol)
    {
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"moves:{Guid.NewGuid():N}:");
        (ListSide From, ListSide To, ListMoveOrder Order, string[] Values)[] cases =
        [
            (ListSide.Left, ListSide.Left, ListMoveOrder.OneByOne, ["b", "a"]),
            (ListSide.Left, ListSide.Right, ListMoveOrder.OneByOne, ["a", "b"]),
            (ListSide.Right, ListSide.Left, ListMoveOrder.OneByOne, ["c", "d"]),
            (ListSide.Right, ListSide.Right, ListMoveOrder.OneByOne, ["d", "c"]),
            (ListSide.Left, ListSide.Left, ListMoveOrder.Bulk, ["a", "b"]),
            (ListSide.Left, ListSide.Right, ListMoveOrder.Bulk, ["a", "b"]),
            (ListSide.Right, ListSide.Left, ListMoveOrder.Bulk, ["c", "d"]),
            (ListSide.Right, ListSide.Right, ListMoveOrder.Bulk, ["c", "d"]),
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
            string[] remainder = test.From == ListSide.Left ? ["c", "d"] : ["a", "b"];
            var originalDestination = same ? remainder : ["x"];
            var expected = test.To == ListSide.Left
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
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });
        var view = client.WithKeyPrefix($"moves:{Guid.NewGuid():N}:");
        (await view.Lists.MoveManyAsync("s", "d")).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", countMode: ListMoveCountMode.Exactly)).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.FromMilliseconds(10))).Should().BeNull();
        await view.Lists.RightPushAsync("s", "a");
        (await view.Lists.MoveManyAsync("s", "d", 2, countMode: ListMoveCountMode.Exactly)).Should().BeNull();
        (await view.Lists.MoveManyAsync("s", "d", 2, countMode: ListMoveCountMode.Exactly, waitFor: TimeSpan.FromMilliseconds(10))).Should().BeNull();
        await view.SetAsync("d", "wrong");
        Func<Task> invalid = async () => await view.Lists.MoveManyAsync("s", "d");
        await invalid.Should().ThrowAsync<RespireServerException>().WithMessage("*WRONGTYPE*");
        (await view.Lists.RangeAsync("s")).Should().Equal("a");
        await view.Keys.DeleteAsync("d");
        (await view.Lists.MoveManyAsync("s", "d", 10)).Should().Equal("a");
        (await view.Keys.ExistsAsync("s")).Should().BeFalse();
        await using var transaction = view.CreateTransaction();
        var empty = transaction.Lists.MoveMany("s", "d", 2, countMode: ListMoveCountMode.Exactly);
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
        { Protocol = (RespProtocol)protocol, Connections = 1, ClientName = name });
        await using var observer = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var view = client.WithKeyPrefix(name + ":");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var move = view.Lists.MoveManyAsync("s", "d", 2, countMode: ListMoveCountMode.Exactly,
            waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await WaitForBlocked(observer, name, deadline.Token);
        (await view.SetAsync("ordinary", "responsive", cancellationToken: deadline.Token)).Should().BeTrue();
        await view.Lists.RightPushAsync("s", ["a"], deadline.Token);
        await WaitForBlocked(observer, name, deadline.Token);
        move.IsCompleted.Should().BeFalse();
        await view.Lists.RightPushAsync("s", ["b"], deadline.Token);
        (await move.WaitAsync(deadline.Token)).Should().Equal("a", "b");
        var blocked = view.Lists.MoveManyAsync("s", "d", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await WaitForBlocked(observer, name, deadline.Token);
        cancel.Cancel();
        Func<Task> canceled = async () => await blocked.WaitAsync(deadline.Token);
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        await view.Lists.RightPushAsync("s", "next");
        (await view.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.FromSeconds(1), cancellationToken: deadline.Token)).Should().Equal("next");
    }

    private static async Task WaitForBlocked(IRespireClient observer, string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            if ((await observer.Server.ClientsAsync(cancellationToken)).Any(x => x.Name == name && x.Flags.Contains('b') && x.Command == "blmovem")) return;
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task<string[]?> Move(IRespireClient client, string mode, string destination,
        ListSide from, ListSide to, ListMoveOrder order)
    {
        if (mode is "immediate" or "blocking")
            return await client.Lists.MoveManyAsync("s", destination, 2, from, to, order: order,
                waitFor: mode == "blocking" ? TimeSpan.FromSeconds(1) : null);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Lists.MoveMany("s", destination, 2, from, to, order: order);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
