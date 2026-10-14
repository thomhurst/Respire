using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CrossSlotExceptionTests
{
    [Test]
    [MatrixDataSource]
    public async Task SetCardinalityHonorsKeyContextBeforeAdmission(
        [Matrix(false, true)] bool includeKeys,
        [Matrix(false, true)] bool union,
        [Matrix("typed", "batch", "transaction")] string surface)
    {
        await using var server = new FakeRespServer();
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            IncludeCrossSlotKeyContext = includeKeys,
        });
        var client = root.WithKeyPrefix("private-prefix:");
        byte[] first = [255, .. "{first}:secret"u8];
        byte[] second = [0, 128, .. "{second}:secret"u8];
        RespireKey[] keys = [first, second];
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        async Task Invoke()
        {
            if (surface == "typed")
            {
                if (union) await client.Sets.UnionCountAsync(keys);
                else await client.Sets.DifferenceCountAsync(keys);
            }
            else
            {
                var sets = surface == "batch" ? batch.Sets : transaction.Sets;
                if (union) _ = sets.UnionCount(keys);
                else _ = sets.DifferenceCount(keys);
            }
        }
        var error = (await Assert.That(Invoke).ThrowsExactly<RespireCrossSlotException>())!;
        await Assert.That(error.CommandName).IsEqualTo(union ? "SUNIONCARD" : "SDIFFCARD");
        await Assert.That(error.ExpectedSlot).IsEqualTo(((RespireKey)first).ClusterSlot);
        await Assert.That(error.ConflictingSlot).IsEqualTo(((RespireKey)second).ClusterSlot);
        var expectedHex = includeKeys ? Convert.ToHexString([.. "private-prefix:"u8, .. second]) : null;
        await Assert.That(error.ConflictingKeyHex).IsEqualTo(expectedHex);
        Array.Fill(second, (byte)'x');
        await Assert.That(error.ConflictingKeyHex).IsEqualTo(expectedHex);
        await Assert.That(error.ToString().Contains("private-prefix", StringComparison.Ordinal)).IsFalse();
        await Assert.That(error.ToString().Contains("secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        _ = batch.Set("{third}:key", "value");
        _ = transaction.Set("{third}:key", "value");
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(transaction.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [MatrixDataSource]
    public async Task LocalRejectionHasSlotsAndOptInBinaryContext(
        [Matrix(false, true)] bool includeKeys,
        [Matrix(false, true)] bool cache,
        [Matrix("typed", "raw", "batch", "transaction")] string surface)
    {
        await using var server = new FakeRespServer();
        await using var root = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
            IncludeCrossSlotKeyContext = includeKeys,
            ClientSideCache = cache ? new() : null,
        });
        var client = root.WithKeyPrefix("private-prefix:");
        byte[] first = [255, .. "{first}:secret"u8];
        byte[] second = [0, 128, .. "{second}:secret"u8];
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        async Task Invoke()
        {
            switch (surface)
            {
                case "typed": await client.Strings.GetManyAsync(first, second); break;
                case "raw":
                    // Immediate raw MGET does not support prefix views; pass its physical keys explicitly.
                    RespireValue[] physicalKeys = [(byte[])[.. "private-prefix:"u8, .. first],
                        (byte[])[.. "private-prefix:"u8, .. second]];
                    using (await root.ExecuteAsync(RespireCommands.String.MGET, physicalKeys)) { }
                    break;
                case "batch": _ = batch.Execute("MGET", first, second); break;
                case "transaction": _ = transaction.Execute("MGET", first, second); break;
            }
        }
        var error = (await Assert.That(Invoke).ThrowsExactly<RespireCrossSlotException>())!;
        await Assert.That(error.CommandName).IsEqualTo("MGET");
        await Assert.That(error.ExpectedSlot).IsEqualTo(((RespireKey)first).ClusterSlot);
        await Assert.That(error.ConflictingSlot).IsEqualTo(((RespireKey)second).ClusterSlot);
        await Assert.That(error.ExpectedSlot).IsNotEqualTo(error.ConflictingSlot);
        await Assert.That(error.Message).Contains("same nonempty hash tag");
        await Assert.That(error.ToString().Contains("private-prefix", StringComparison.Ordinal)).IsFalse();
        await Assert.That(error.ToString().Contains("secret", StringComparison.Ordinal)).IsFalse();
        var expectedHex = includeKeys ? Convert.ToHexString([.. "private-prefix:"u8, .. second]) : null;
        await Assert.That(error.ConflictingKeyHex).IsEqualTo(expectedHex);
        Array.Fill(second, (byte)'x');
        await Assert.That(error.ConflictingKeyHex).IsEqualTo(expectedHex);
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
        // Rejection must not pin the transaction's slot or reserve an entry in either queue.
        _ = batch.Set("{third}:key", "value");
        _ = transaction.Set("{third}:key", "value");
        await Assert.That(batch.Count).IsEqualTo(1);
        await Assert.That(transaction.Count).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(RespireException.GetDefinitiveServerError(error)).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RejectedRawCommandLeavesQueueExecutable(bool transactional)
    {
        await using var server = new FakeRespServer();
        server.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n"),
            "MULTI" => "+OK\r\n"u8.ToArray(),
            "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
            _ => transactional ? "+QUEUED\r\n"u8.ToArray() : "+OK\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        await Assert.That(() => queue.Execute("MGET", "{first}:key", "{second}:key"))
            .ThrowsExactly<RespireCrossSlotException>();
        var pending = queue.Set("{third}:key", "value");
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("MGET ", StringComparison.Ordinal))).IsFalse();
        await Assert.That(server.ReceivedCommands.Count(command => command == "SET {third}:key value")).IsEqualTo(1);
    }

    [Test]
    public async Task RedisCrossSlotReplyRemainsServerError()
    {
        await using var server = new FakeRespServer("-CROSSSLOT injected server reply\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var error = (await Assert.That(async () =>
            await client.Strings.GetManyAsync("{first}:key", "{second}:key"))
            .ThrowsExactly<RespireServerException>())!;
        await Assert.That(error.Code).IsEqualTo(RespireErrorCodes.CrossSlot);
        await Assert.That(error.CommandName).IsEqualTo("MGET");
        await Assert.That(server.ReceivedCommands).Contains("MGET {first}:key {second}:key");
        await Assert.That(RespireException.GetDefinitiveServerError(error)).IsEqualTo(error);
    }
}
