using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CopyDatabaseTests
{
    [Test]
    [Arguments("immediate", false, 0)]
    [Arguments("immediate", true, 7)]
    [Arguments("batch", false, 7)]
    [Arguments("batch", true, 0)]
    [Arguments("transaction", false, 0)]
    [Arguments("transaction", true, 7)]
    public async Task DatabaseAndReplacePreserveBothPrefixedKeys(string mode, bool replace, int database)
    {
        await using var server = new FakeRespServer(Replies(mode));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await Copy(client.WithKeyPrefix("tenant:"), mode, "source", "target", database, replace);
        await Assert.That(result).IsTrue();
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo([$"COPY tenant:source tenant:target DB {database}" + (replace ? " REPLACE" : "")]);
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ServerDatabaseErrorsSurfaceInEveryMode(string mode)
    {
        byte[] error = "-ERR DB index is out of range\r\n"u8.ToArray();
        byte[][] replies = mode == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), [.. "*1\r\n"u8, .. error]]
            : [error];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var failure = await Assert.That(async () => await Copy(client, mode, "source", "target", int.MaxValue, false))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(failure!.CommandName).IsEqualTo("COPY");
        await Assert.That(failure.Message).Contains("DB index is out of range");
    }

    [Test]
    public async Task NegativeDatabaseFailsBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Keys.CopyAsync("source", "target", -1))
            .ThrowsExactly<ArgumentOutOfRangeException>();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => batch.Keys.Copy("source", "target", -1)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(() => transaction.Keys.Copy("source", "target", -1, true)).ThrowsExactly<ArgumentOutOfRangeException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ClusterRoutesBothKeysAndRetainsDatabase(string mode)
    {
        await using var owner = new FakeRespServer(Replies(mode));
        var slot = ClusterHash.GetSlot("tenant:{copy}:source");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await Copy(view, mode, "{copy}:source", "{copy}:target", 2, true)).IsTrue();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["COPY tenant:{copy}:source tenant:{copy}:target DB 2 REPLACE"]);
        await Assert.That(async () => await view.Keys.CopyAsync("{a}:source", "{b}:target", 2))
            .Throws<RespireServerException>();
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        await Assert.That(() => batch.Keys.Copy("{a}:source", "{b}:target", 2)).Throws<RespireServerException>();
        await Assert.That(() => transaction.Keys.Copy("{a}:source", "{b}:target", 2)).Throws<RespireServerException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
    }

    private static byte[][] Replies(string mode) => mode == "transaction"
        ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray()]
        : [":1\r\n"u8.ToArray()];

    private static async Task<bool> Copy(IRespireClient client, string mode,
        RespireKey source, RespireKey destination, int database, bool replace)
    {
        if (mode == "immediate") return await client.Keys.CopyAsync(source, destination, database, replace);
        using var batch = mode == "batch" ? client.CreateBatch() : null;
        await using var transaction = mode == "transaction" ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Keys.Copy(source, destination, database, replace);
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        return pending.Result;
    }
}
