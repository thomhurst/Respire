using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SetManyIfNotExistsTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImmediateAndDeferred_EncodePairsAndParseFlags(bool transactional)
    {
        byte[][] replies = transactional
            ? [":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), FakeRespServer.OkReply,
                "+QUEUED\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray()]
            : [":1\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        (RespireKey Key, RespireValue Value)[] pairs = [("a", "one"), ("b", "two")];
        await Assert.That(await view.Strings.SetManyIfNotExistsAsync(pairs)).IsTrue();
        await Assert.That(await view.Strings.SetManyIfNotExistsAsync(pairs, CancellationToken.None)).IsFalse();
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Strings.SetManyIfNotExists(pairs);
        pairs[1] = ("changed", "changed");
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(pending.Result).IsTrue();
        var commands = server.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC").ToArray();
        await Assert.That(commands.Length).IsEqualTo(3);
        foreach (var command in commands)
            await Assert.That(command).IsEqualTo("MSETNX tenant:a one tenant:b two");
    }

    [Test]
    public async Task EmptyPairsAndCancellation_DoNotSendOrEnqueue()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        await Assert.That(async () => await client.Strings.SetManyIfNotExistsAsync()).Throws<ArgumentException>();
        await Assert.That(() => batch.Strings.SetManyIfNotExists()).Throws<ArgumentException>();
        await Assert.That(() => transaction.Strings.SetManyIfNotExists()).Throws<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = client.Strings.SetManyIfNotExistsAsync([("a", "value")], cancellation.Token);
        await Assert.That(canceled.IsCanceled).IsTrue();
        var error = await Assert.That(async () => await canceled).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CrossSlotPairs_FailBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        (RespireKey Key, RespireValue Value)[] pairs = [("{a}:one", "first"), ("{b}:two", "second")];
        await Assert.That(async () => await view.Strings.SetManyIfNotExistsAsync(pairs))
            .Throws<RespireServerException>().WithMessage("CROSSSLOT Keys in request don't hash to the same slot");
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        await Assert.That(() => batch.Strings.SetManyIfNotExists(pairs)).Throws<RespireServerException>();
        await Assert.That(() => transaction.Strings.SetManyIfNotExists(pairs)).Throws<RespireServerException>();
        // Rejected pairs must not pin the transaction to either rejected slot.
        _ = transaction.Strings.SetManyIfNotExists(("{c}:one", "first"), ("{c}:two", "second"));
        await Assert.That(server.ReceivedCommands.Where(c => c != "CLUSTER SLOTS")).IsEmpty();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task ClusterRouting_UsesPrefixedKeysAndIgnoresValueSlots(string path)
    {
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray()]
            : [":1\r\n"u8.ToArray()];
        await using var owner = new FakeRespServer(2, replies);
        var slot = ClusterHash.GetSlot("{tenant}:a");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("{tenant}:");
        (RespireKey Key, RespireValue Value)[] pairs = [("a", "{different}:one"), ("b", "{other}:two")];
        bool result;
        if (path == "immediate") result = await view.Strings.SetManyIfNotExistsAsync(pairs);
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.Strings.SetManyIfNotExists(pairs);
            await batch.ExecuteAsync();
            result = pending.Result;
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.Strings.SetManyIfNotExists(pairs);
            await transaction.CommitAsync();
            result = pending.Result;
        }
        await Assert.That(result).IsTrue();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["MSETNX {tenant}:a {different}:one {tenant}:b {other}:two"]);
    }
}
