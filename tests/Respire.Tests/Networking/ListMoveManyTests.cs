using Respire.Commands;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ListMoveManyTests
{
    private static readonly byte[] Moved = "*2\r\n$1\r\nb\r\n$1\r\na\r\n"u8.ToArray();

    [Test]
    [Arguments("immediate")]
    [Arguments("blocking")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task WireAndOwnedResult(string mode)
    {
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(Moved).ToArray()]
            : [Moved];
        await using var server = new FakeRespServer(2, replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        string[]? result;
        if (mode is "immediate" or "blocking")
            result = await view.Lists.MoveManyAsync("source", "destination", 2, ListSide.Right, ListSide.Left,
                ListMoveCountMode.Exactly, ListMoveOrder.Bulk, mode == "blocking" ? TimeSpan.FromMilliseconds(250) : null);
        else
        {
            using var batch = mode == "batch" ? view.CreateBatch() : null;
            await using var transaction = mode == "transaction" ? view.CreateTransaction() : null;
            IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
            var pending = queue.Lists.MoveMany("source", "destination", 2, ListSide.Right, ListSide.Left,
                ListMoveCountMode.Exactly, ListMoveOrder.Bulk);
            if (transaction is not null) await transaction.CommitAsync();
            else await batch!.ExecuteAsync();
            result = pending.Result;
        }
        await client.DisposeAsync();
        await Assert.That(result!).IsEquivalentTo(["b", "a"], CollectionOrdering.Matching);
        var expected = mode == "blocking"
            ? "BLMOVEM tenant:source tenant:destination RIGHT LEFT 0.25 EXACTLY 2 BULK"
            : "LMOVEM tenant:source tenant:destination RIGHT LEFT EXACTLY 2 BULK";
        await Assert.That(server.ReceivedCommands.Where(x => x is not "MULTI" and not "EXEC")).IsEquivalentTo([expected]);
    }

    [Test]
    public async Task InvalidOptionsAndCancellationDoNotSendOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        Func<ValueTask<string[]?>>[] invalid =
        [
            () => client.Lists.MoveManyAsync("s", "d", 0),
            () => client.Lists.MoveManyAsync("s", "d", -1),
            () => client.Lists.MoveManyAsync("s", "d", from: (ListSide)99),
            () => client.Lists.MoveManyAsync("s", "d", to: (ListSide)99),
            () => client.Lists.MoveManyAsync("s", "d", countMode: (ListMoveCountMode)99),
            () => client.Lists.MoveManyAsync("s", "d", order: (ListMoveOrder)99),
            () => client.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.FromSeconds(-2)),
        ];
        foreach (var call in invalid)
            await Assert.That(() => { _ = call(); }).Throws<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (var lists in new[] { batch.Lists, transaction.Lists })
        {
            await Assert.That(() => lists.MoveMany("s", "d", 0)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.MoveMany("s", "d", from: (ListSide)99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.MoveMany("s", "d", to: (ListSide)99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.MoveMany("s", "d", countMode: (ListMoveCountMode)99)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => lists.MoveMany("s", "d", order: (ListMoveOrder)99)).Throws<ArgumentOutOfRangeException>();
        }
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        foreach (var wait in new TimeSpan?[] { null, Timeout.InfiniteTimeSpan })
            await Assert.That(async () => await client.Lists.MoveManyAsync("s", "d", waitFor: wait, cancellationToken: cancel.Token))
                .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task BlockingCancellationLeavesMultiplexerUsable()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.PongReply)
        {
            SuppressReply = command =>
            {
                if (!command.StartsWith("BLMOVEM ", StringComparison.Ordinal)) return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancel = new CancellationTokenSource();
        var pending = client.Lists.MoveManyAsync("s", "d", waitFor: Timeout.InfiniteTimeSpan, cancellationToken: cancel.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var pong = await client.ExecuteAsync("PING").AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        cancel.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(10))).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).Contains("BLMOVEM s d LEFT RIGHT 0 COUNT 1 OBO");
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task NullRepliesAndZeroWaitPreserveSemantics()
    {
        await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Lists.MoveManyAsync("s", "d")).IsNull();
        await Assert.That(await client.Lists.MoveManyAsync("s", "d", waitFor: TimeSpan.Zero)).IsNull();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["LMOVEM s d LEFT RIGHT COUNT 1 OBO", "BLMOVEM s d LEFT RIGHT 0.001 COUNT 1 OBO"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CrossSlotValidationDoesNotPoisonQueues()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        foreach (var wait in new TimeSpan?[] { null, TimeSpan.Zero })
            await Assert.That(() => { _ = view.Lists.MoveManyAsync("{a}:s", "{b}:d", waitFor: wait); })
                .Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        foreach (var lists in new[] { batch.Lists, transaction.Lists })
        {
            await Assert.That(() => lists.MoveMany("{a}:s", "{b}:d")).Throws<RespireServerException>();
            _ = lists.MoveMany("{a}:s", "{a}:d");
        }
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task MutationMetadataIncludesBothPrefixedKeys()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        foreach (var wait in new TimeSpan?[] { null, TimeSpan.Zero })
        {
            var (operation, command) = ListCommands.MoveManyCommand((RespireClient)client.WithKeyPrefix("p:"), "s", "d", 1,
                ListSide.Left, ListSide.Right, ListMoveCountMode.UpTo, ListMoveOrder.OneByOne, wait);
            await Assert.That(command.TryGetClientCacheKey(operation, out var arguments)).IsTrue();
            await Assert.That(RawCommandKeyLayouts.TryGetMutationLayout(operation, in arguments, out var layout)).IsTrue();
            await Assert.That(layout.Count).IsEqualTo(2);
            await Assert.That(arguments.GetArgument(0).AsKey()).IsEqualTo((RespireKey)"p:s");
            await Assert.That(arguments.GetArgument(1).AsKey()).IsEqualTo((RespireKey)"p:d");
        }
    }
}
