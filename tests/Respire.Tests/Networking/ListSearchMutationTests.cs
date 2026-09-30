using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ListSearchMutationTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredCommands_SnapshotValuesAndPreserveWireOptions(bool transactional)
    {
        byte[][] results = [":2\r\n"u8.ToArray(), "*2\r\n:3\r\n:2\r\n"u8.ToArray(), ":4\r\n"u8.ToArray(), ":5\r\n"u8.ToArray(),
            FakeRespServer.OkReply, ":7\r\n"u8.ToArray(), ":9\r\n"u8.ToArray()];
        var replies = transactional
            ? new[] { FakeRespServer.OkReply }.Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length))
                .Append("*7\r\n"u8.ToArray().Concat(results.SelectMany(result => result)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        RespireValue[] values = ["a", "b"];
        var position = queue.Lists.Position("list", "a", rank: 2, maxLength: 9);
        var positions = queue.Lists.Positions("list", "a", count: 2, rank: -1, maxLength: 4);
        var before = queue.Lists.InsertBefore("list", "a", "before");
        var after = queue.Lists.InsertAfter("list", "a", "after");
        var set = queue.Lists.Set("list", -1, "tail");
        var left = queue.Lists.LeftPushIfExists("list", values);
        var right = queue.Lists.RightPushIfExists("list", values);
        values[0] = "changed";
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await batch!.ExecuteAsync();
        await Assert.That(position.Result).IsEqualTo(2);
        await Assert.That(positions.Result).IsEquivalentTo([3L, 2L], CollectionOrdering.Matching);
        await Assert.That(before.Result).IsEqualTo(4);
        await Assert.That(after.Result).IsEqualTo(5);
        await Assert.That(set.Result).IsTrue();
        await Assert.That(left.Result).IsEqualTo(7);
        await Assert.That(right.Result).IsEqualTo(9);
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(new[]
            {
                "LPOS tenant:list a RANK 2 MAXLEN 9", "LPOS tenant:list a RANK -1 COUNT 2 MAXLEN 4",
                "LINSERT tenant:list BEFORE a before", "LINSERT tenant:list AFTER a after", "LSET tenant:list -1 tail",
                "LPUSHX tenant:list a b", "RPUSHX tenant:list a b",
            }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task InvalidOptions_FailBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        Func<Task>[] immediate =
        [
            async () => { await client.Lists.PositionAsync("list", "x", rank: 0); },
            async () => { await client.Lists.PositionAsync("list", "x", rank: long.MinValue); },
            async () => { await client.Lists.PositionAsync("list", "x", maxLength: -1); },
            async () => { await client.Lists.PositionsAsync("list", "x", count: -1); },
            async () => { await client.Lists.PositionsAsync("list", "x", rank: 0); },
            async () => { await client.Lists.PositionsAsync("list", "x", maxLength: -1); },
            async () => { await client.Lists.LeftPushIfExistsAsync("list"); },
            async () => { await client.Lists.RightPushIfExistsAsync("list"); },
        ];
        foreach (var command in immediate)
            await Assert.That(command).Throws<ArgumentException>();
        foreach (var lists in new[] { batch.Lists, transaction.Lists })
        {
            await Assert.That(() => lists.Position("list", "x", rank: 0)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.Position("list", "x", rank: long.MinValue)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.Position("list", "x", maxLength: -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.Positions("list", "x", count: -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.Positions("list", "x", rank: 0)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.Positions("list", "x", maxLength: -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.LeftPushIfExists("list")).Throws<ArgumentException>();
            await Assert.That(() => lists.RightPushIfExists("list")).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    public async Task ImmediateCommands_ForwardCancellation(int command)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = command switch
        {
            0 => client.Lists.PositionAsync("list", "x", cancellationToken: cancellation.Token).AsTask(),
            1 => client.Lists.PositionsAsync("list", "x", cancellationToken: cancellation.Token).AsTask(),
            2 => client.Lists.InsertBeforeAsync("list", "x", "y", cancellation.Token).AsTask(),
            3 => client.Lists.InsertAfterAsync("list", "x", "y", cancellation.Token).AsTask(),
            4 => client.Lists.SetAsync("list", -1, "x", cancellation.Token).AsTask(),
            5 => client.Lists.LeftPushIfExistsAsync("list", ["x", "y"], cancellation.Token).AsTask(),
            _ => client.Lists.RightPushIfExistsAsync("list", ["x", "y"], cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }
}
