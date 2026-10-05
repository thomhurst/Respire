using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerFlushCommandTests
{
    [Test]
    [Arguments(false, ServerFlushMode.Default, "")]
    [Arguments(false, ServerFlushMode.Sync, " SYNC")]
    [Arguments(false, ServerFlushMode.Async, " ASYNC")]
    [Arguments(true, ServerFlushMode.Default, "")]
    [Arguments(true, ServerFlushMode.Sync, " SYNC")]
    [Arguments(true, ServerFlushMode.Async, " ASYNC")]
    public async Task ClusterQueuesFlushOnlyTheirExecutionNode(bool transaction, ServerFlushMode mode, string suffix)
    {
        byte[][] replies = transaction
            ? [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 3), "*3\r\n+OK\r\n+OK\r\n+OK\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply, FakeRespServer.OkReply];
        await using var first = new FakeRespServer(replies);
        await using var second = new FakeRespServer(replies);
        var topology = System.Text.Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(topology)
        {
            // A keyless batch can use the seed connection; keyed transactions select a slot owner.
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : FakeRespServer.OkReply,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, AllowAdmin = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
        });
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        IRespireCommandQueue queue = transaction ? tx : batch;
        var database = queue.Server.FlushDatabase(mode);
        var all = queue.Server.FlushAll(mode);
        if (transaction)
        {
            var key = Enumerable.Range(0, 100).Select(index => $"key:{index}")
                .First(candidate => Respire.Internal.ClusterHash.GetSlot(candidate) >= 8192);
            _ = queue.Set(key, (RespireValue)"value");
        }
        if (transaction) await tx.CommitAsync(); else await batch.ExecuteAsync();
        await Assert.That(database.Result).IsTrue();
        await Assert.That(all.Result).IsTrue();
        var participants = new[] { first, second, seed };
        await Assert.That(participants.Count(server => server.ReceivedCommands.Any(command => command.StartsWith("FLUSH", StringComparison.Ordinal))))
            .IsEqualTo(1);
        if (transaction) await Assert.That(first.ReceivedCommands).IsEmpty();
        await Assert.That(participants.SelectMany(server => server.ReceivedCommands)
            .Where(command => command.StartsWith("FLUSH", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(new[] { "FLUSHDB" + suffix, "FLUSHALL" + suffix });
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task EveryModePreservesWireForm(int execution)
    {
        byte[][] replies = Enumerable.Repeat(FakeRespServer.OkReply, 6).ToArray();
        if (execution == 2)
            replies = [FakeRespServer.OkReply, .. Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), 6),
                [.. "*6\r\n"u8, .. replies.SelectMany(reply => reply)]];
        await using var server = new FakeRespServer(replies);
        await using var owner = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, AllowAdmin = true, Connections = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        var client = owner.WithKeyPrefix("tenant:");
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        IRespireCommandQueue queue = execution == 2 ? tx : batch;
        var pending = new List<RespirePending<bool>>();
        foreach (var mode in new[] { ServerFlushMode.Default, ServerFlushMode.Sync, ServerFlushMode.Async })
        {
            if (execution == 0)
            {
                if (mode == ServerFlushMode.Default)
                {
                    await client.Server.FlushDatabaseAsync();
                    await client.Server.FlushAllAsync();
                }
                else
                {
                    await client.Server.FlushDatabaseAsync(mode);
                    await client.Server.FlushAllAsync(mode);
                }
            }
            else
            {
                pending.Add(queue.Server.FlushDatabase(mode));
                pending.Add(queue.Server.FlushAll(mode));
            }
        }
        if (execution == 1) await batch.ExecuteAsync();
        if (execution == 2) await tx.CommitAsync();
        foreach (var result in pending) await Assert.That(result.Result).IsTrue();
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC").ToArray())
            .IsEquivalentTo(new[] { "FLUSHDB", "FLUSHALL", "FLUSHDB SYNC", "FLUSHALL SYNC", "FLUSHDB ASYNC", "FLUSHALL ASYNC" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ValidationRejectsBeforeSendingOrQueueing(bool allowAdmin)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, AllowAdmin = allowAdmin, Connections = 1,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
        });
        using var batch = client.CreateBatch();
        await using var tx = client.CreateTransaction();
        if (allowAdmin)
        {
            await Assert.That(async () => await client.Server.FlushDatabaseAsync((ServerFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
            await Assert.That(async () => await client.Server.FlushAllAsync((ServerFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
            foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
            {
                await Assert.That(() => queue.Server.FlushDatabase((ServerFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
                await Assert.That(() => queue.Server.FlushAll((ServerFlushMode)99)).ThrowsExactly<ArgumentOutOfRangeException>();
            }
        }
        else
        {
            foreach (var mode in Enum.GetValues<ServerFlushMode>())
            {
                await Assert.That(async () => await client.Server.FlushDatabaseAsync(mode)).ThrowsExactly<NotSupportedException>();
                await Assert.That(async () => await client.Server.FlushAllAsync(mode)).ThrowsExactly<NotSupportedException>();
                foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, tx })
                {
                    await Assert.That(() => queue.Server.FlushDatabase(mode)).ThrowsExactly<NotSupportedException>();
                    await Assert.That(() => queue.Server.FlushAll(mode)).ThrowsExactly<NotSupportedException>();
                }
            }
        }
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(tx.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
