using Respire.Testing;
using Respire.TestSupport;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class FakeListMoveManyTests
{
    [Test]
    public async Task UnrelatedCommandsDoNotReexecuteBlockedMoves()
    {
        var clock = new CountingClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var observed = server.InjectFault("BLMOVEM", RespireFakeFault.Delay(TimeSpan.Zero));
        var move = client.Lists.MoveManyAsync("source", "destination", waitFor: Timeout.InfiniteTimeSpan,
            cancellationToken: deadline.Token).AsTask();
        await observed.Matched.WaitAsync(deadline.Token);
        while (observed.ExecutionCount == 0) await Task.Delay(1, deadline.Token);
        // The clock is sampled once per execution under the server gate. Taking the same
        // gate ensures the blocked command has registered its wait before the baseline.
        var gate = typeof(RespireFakeServer).GetField("_gate",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        long before;
        lock (gate) before = clock.Count;
        await client.GetStringAsync("source");
        await client.SetAsync("unrelated", "value");
        await client.PingAsync();
        await Task.Delay(100, deadline.Token);
        long after;
        lock (gate) after = clock.Count;
        await Assert.That(after - before).IsEqualTo(3);
        await Assert.That(move.IsCompleted).IsFalse();
        await client.Lists.RightPushAsync("source", "ready");
        await Assert.That((await move.WaitAsync(deadline.Token))!).IsEquivalentTo(["ready"]);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CancellationAndTimeoutRemoveOnlyTheirOwnWaiters(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancelled = client.Lists.MoveManyAsync("same", "destination", waitFor: Timeout.InfiniteTimeSpan,
            cancellationToken: cancel.Token).AsTask();
        var survivor = client.Lists.MoveManyAsync("same", "destination", waitFor: Timeout.InfiniteTimeSpan,
            cancellationToken: deadline.Token).AsTask();
        await WaitForWaiterCount(server, 2, deadline.Token);
        cancel.Cancel();
        await Assert.That(async () => await cancelled).Throws<OperationCanceledException>();
        await WaitForWaiterCount(server, 1, deadline.Token);
        await Assert.That(await client.Lists.MoveManyAsync("same", "destination", waitFor: TimeSpan.Zero,
            cancellationToken: deadline.Token)).IsNull();
        await Assert.That(await client.Lists.MoveManyAsync("other", "destination", waitFor: TimeSpan.Zero,
            cancellationToken: deadline.Token)).IsNull();
        await WaitForWaiterCount(server, 1, deadline.Token);
        await client.Lists.RightPushAsync("same", "value");
        await Assert.That((await survivor.WaitAsync(deadline.Token))!).IsEquivalentTo(["value"]);
        await WaitForWaiterCount(server, 0, deadline.Token);
    }

    [Test]
    public async Task CompetingWaitersKeepTheNextGenerationRegisteredForBinaryKeys()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] key = [0, 255, 128];
        var first = client.Lists.MoveManyAsync(key, "destination", waitFor: Timeout.InfiniteTimeSpan,
            cancellationToken: deadline.Token).AsTask();
        var second = client.Lists.MoveManyAsync(key.ToArray(), "destination", waitFor: Timeout.InfiniteTimeSpan,
            cancellationToken: deadline.Token).AsTask();
        await WaitForWaiterCount(server, 2, deadline.Token);
        await client.Lists.RightPushAsync(key.ToArray(), "first");
        var completed = await Task.WhenAny(first, second).WaitAsync(deadline.Token);
        await Assert.That((await completed)!).IsEquivalentTo(["first"]);
        await WaitForWaiterCount(server, 1, deadline.Token);
        await client.Lists.RightPushAsync(key.ToArray(), "second");
        var remaining = ReferenceEquals(completed, first) ? second : first;
        await Assert.That((await remaining.WaitAsync(deadline.Token))!).IsEquivalentTo(["second"]);
        await WaitForWaiterCount(server, 0, deadline.Token);
    }

    private static async Task WaitForWaiterCount(RespireFakeServer server, int expected, CancellationToken cancellationToken)
    {
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var gate = typeof(RespireFakeServer).GetField("_gate", flags)!.GetValue(server)!;
        var groups = (System.Collections.IDictionary)typeof(RespireFakeServer).GetField("_listMoveWaiters", flags)!.GetValue(server)!;
        while (true)
        {
            lock (gate)
            {
                var count = groups.Values.Cast<object>().Sum(group => (int)group.GetType().GetField("Count", flags)!.GetValue(group)!);
                if (count == expected && (expected != 0 || groups.Count == 0)) return;
            }
            await Task.Delay(1, cancellationToken);
        }
    }

    private sealed class CountingClock : TimeProvider
    {
        private long _count;
        public long Count => Interlocked.Read(ref _count);
        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _count);
            return DateTimeOffset.UtcNow;
        }
    }

    [Test]
    public async Task MovesInvalidateBothWatchesAndBlockingInsideExecIsImmediate()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        foreach (var watched in new[] { "s", "d" })
        {
            await client.Lists.RightPushAsync("s", "a");
            await using var transaction = await client.CreateTransactionAsync([watched]);
            _ = transaction.Set("should-not-execute", "value");
            await client.Lists.MoveManyAsync("s", "d");
            await Assert.That(await transaction.CommitAsync()).IsFalse();
        }
        await using var session = await TestRespSession.ConnectAsync(server.CreateOptions());
        using (var multi = await session.CommandAsync("MULTI")) { }
        using (var queued = await session.CommandAsync("BLMOVEM", "missing", "d", "LEFT", "RIGHT", "0", "EXACTLY", "2", "BULK"))
            await Assert.That(queued.AsString()).IsEqualTo("QUEUED");
        using var reply = await session.CommandAsync("EXEC").WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(reply.AsArray()[0].IsNull).IsTrue();
    }

    [Test]
    public async Task InvalidRawOptionsDoNotMutateLists()
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions());
        await client.Lists.RightPushAsync("s", "a", "b");
        RespireValue[][] invalid =
        [
            ["s", "d", "LEFT", "RIGHT", "COUNT", 0, "OBO"],
            ["s", "d", "LEFT", "RIGHT", "EXACTLY", -1, "BULK"],
            ["s", "d", "LEFT", "RIGHT", "COUNT", 2],
            ["s", "d", "LEFT", "RIGHT", "COUNT", 2, "invalid"],
            ["s", "d", "invalid", "RIGHT"],
        ];
        foreach (var arguments in invalid)
            await Assert.That(async () => { using var reply = await client.ExecuteAsync("LMOVEM", arguments); })
                .Throws<RespireServerException>();
        await Assert.That(await client.Lists.RangeAsync("s")).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        using var defaultMove = await client.ExecuteAsync("LMOVEM", "s", "d", "RIGHT", "LEFT");
        await Assert.That(defaultMove.Count).IsEqualTo(1);
        await Assert.That(defaultMove[0].AsString()).IsEqualTo("b");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ExactCountWrongTypesAndSameListPreserveState(int protocol)
    {
        var clock = new RespireFakeClock();
        await using var server = new RespireFakeServer(clock);
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Lists.RightPushAsync("s", "a", "b", "c");
        using (var expiry = await client.ExecuteAsync("PEXPIRE", "s", 1000)) { }
        await client.SetAsync("wrong", "text");
        await Assert.That(await client.Lists.MoveManyAsync("s", "wrong", 4, countMode: ListMoveCountMode.Exactly)).IsNull();
        await Assert.That(async () => await client.Lists.MoveManyAsync("s", "wrong", 2)).Throws<RespireServerException>();
        await Assert.That(await client.Lists.RangeAsync("s")).IsEquivalentTo(["a", "b", "c"], CollectionOrdering.Matching);
        await Assert.That(await client.Lists.MoveManyAsync("s", "s", 2, to: ListSide.Left))
            .IsEquivalentTo(["b", "a"], CollectionOrdering.Matching);
        await Assert.That(await client.Lists.RangeAsync("s")).IsEquivalentTo(["b", "a", "c"], CollectionOrdering.Matching);
        await Assert.That(await client.Lists.MoveManyAsync("s", "s", 99, order: ListMoveOrder.Bulk))
            .IsEquivalentTo(["b", "a", "c"], CollectionOrdering.Matching);
        clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(await client.Lists.CountAsync("s")).IsEqualTo(0);
        await Assert.That(await client.Lists.MoveManyAsync("s", "wrong")).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task BlockingExactlyWakesOnGrowthAndCancellationReleasesWait(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol, Connections = 1 });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        using var observed = server.InjectFault("BLMOVEM", RespireFakeFault.Delay(TimeSpan.Zero));
        var move = client.Lists.MoveManyAsync("s", "d", 2, countMode: ListMoveCountMode.Exactly,
            waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await observed.Matched.WaitAsync(deadline.Token);
        await client.Lists.RightPushAsync("s", ["a"], deadline.Token);
        await Assert.That(move.IsCompleted).IsFalse();
        await client.Lists.RightPushAsync("s", ["b"], deadline.Token);
        await Assert.That((await move.WaitAsync(deadline.Token))!).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        await Assert.That(observed.ExecutionCount).IsEqualTo(1);
        var blocked = client.Lists.MoveManyAsync("missing", "d", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        cancel.Cancel();
        await Assert.That(async () => await blocked.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(await client.Lists.MoveManyAsync("missing", "d", waitFor: TimeSpan.Zero, cancellationToken: deadline.Token)).IsNull();
        await client.Lists.RightPushAsync("s", "next");
        using var batch = client.CreateBatch();
        var pending = batch.Lists.MoveMany("s", "d");
        await batch.ExecuteAsync(deadline.Token);
        await Assert.That(pending.Result!).IsEquivalentTo(["next"]);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task MoveUsesDestinationOrderAndPreservesUnmovedElements(int protocol)
    {
        await using var server = new RespireFakeServer();
        await using var client = await RespireClient.ConnectAsync(server.CreateOptions() with { Protocol = (RespProtocol)protocol });
        await client.Lists.RightPushAsync("source", "a", "b", "c");
        await client.Lists.RightPushAsync("destination", "x");
        using var moved = await client.ExecuteAsync("LMOVEM", "source", "destination", "LEFT", "LEFT", "COUNT", 2, "OBO");
        await Assert.That(moved[0].AsString()).IsEqualTo("b");
        await Assert.That(moved[1].AsString()).IsEqualTo("a");
        await Assert.That(await client.Lists.RangeAsync("source")).IsEquivalentTo(["c"], CollectionOrdering.Matching);
        await Assert.That(await client.Lists.RangeAsync("destination")).IsEquivalentTo(["b", "a", "x"], CollectionOrdering.Matching);
    }
}
