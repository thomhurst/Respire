using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class CrossSurfaceTypeConsistencyTests
{
    [Test]
    [Arguments(typeof(IKeyCommands), "RenameAsync", typeof(IBatchKeyCommands), "Rename")]
    [Arguments(typeof(IListCommands), "TrimAsync", typeof(IBatchListCommands), "Trim")]
    [Arguments(typeof(IHyperLogLogCommands), "MergeAsync", typeof(IBatchHyperLogLogCommands), "Merge")]
    [Arguments(typeof(IStringCommands), "SetManyAsync", typeof(IBatchStringCommands), "SetMany")]
    [Arguments(typeof(IStringCommands), "SetManyIfNotExistsAsync", typeof(IBatchStringCommands), "SetManyIfNotExists")]
    public async Task Writes_MatchDeferredBooleanResults(
        Type immediate, string immediateName, Type deferred, string deferredName)
    {
        var methods = immediate.GetMethods().Where(method => method.Name == immediateName).ToArray();
        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods.All(method => method.ReturnType == typeof(ValueTask<bool>))).IsTrue();
        await Assert.That(deferred.GetMethod(deferredName)!.ReturnType).IsEqualTo(typeof(RespirePending<bool>));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OkConfirmations_MatchImmediateAndDeferredWrites(bool useTransaction)
    {
        byte[][] deferredReplies = useTransaction
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
               "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), "*4\r\n+OK\r\n+OK\r\n+OK\r\n+OK\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply];
        await using var server = new FakeRespServer(
            [FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply, .. deferredReplies]);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        foreach (var call in ImmediateWrites(client))
        {
            await Assert.That(await call()).IsTrue();
        }

        var expected = server.ReceivedCommands.ToArray();
        RespirePending<bool>[] pending;
        if (useTransaction)
        {
            await using var transaction = client.CreateTransaction();
            pending = QueueWrites(transaction);
            await transaction.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            pending = QueueWrites(batch);
            await batch.ExecuteAsync();
        }

        foreach (var result in pending)
        {
            await Assert.That(result.Result).IsTrue();
        }
        var actual = server.ReceivedCommands.Skip(4).Where(command => command != "MULTI" && command != "EXEC");
        await Assert.That(actual).IsEquivalentTo(expected);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WriteConfirmations_RejectUnexpectedRepliesAndServerErrors(bool serverError)
    {
        var reply = serverError ? "-ERR rejected\r\n"u8.ToArray() : FakeRespServer.PongReply;
        await using var server = new FakeRespServer(reply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        foreach (var call in ImmediateWrites(client))
        {
            if (serverError)
            {
                await Assert.That(async () => await call()).ThrowsExactly<RespireServerException>();
            }
            else
            {
                await Assert.That(async () => await call()).ThrowsExactly<RespireException>();
            }
        }

        using var batch = client.CreateBatch();
        var pending = QueueWrites(batch);
        var execution = await batch.TryExecuteAsync();
        await Assert.That(execution.FailureCount).IsEqualTo(4);
        foreach (var result in pending)
        {
            await Assert.That(result.Error!.GetType())
                .IsEqualTo(serverError ? typeof(RespireServerException) : typeof(RespireException));
        }
    }

    [Test]
    public async Task StreamEntries_AreValueTypesLikeOtherResultEntries()
        => await Assert.That(typeof(RespireStreamEntry).IsValueType).IsTrue();

    private static Func<ValueTask<bool>>[] ImmediateWrites(RespireClient client) =>
    [
        () => client.Keys.RenameAsync("old", "new"),
        () => client.Lists.TrimAsync("list", 1, 2),
        () => client.HyperLogLog.MergeAsync("all", "first", "second"),
        () => client.Strings.SetManyAsync(("key", "value")),
    ];

    private static RespirePending<bool>[] QueueWrites(IRespireCommandQueue queue) =>
    [
        queue.Keys.Rename("old", "new"),
        queue.Lists.Trim("list", 1, 2),
        queue.HyperLogLog.Merge("all", "first", "second"),
        queue.Strings.SetMany(("key", "value")),
    ];
}
