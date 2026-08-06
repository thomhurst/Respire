using Respire;
using Respire.FastClient;
using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// MULTI/EXEC wire tests against the fake server: framing order, discarded QUEUED replies,
/// EXEC result delivery, and abort handling.
/// </summary>
public class TransactionTests
{
    private static readonly byte[] QueuedReply = "+QUEUED\r\n"u8.ToArray();

    [Test]
    public async Task Transaction_SendsMultiCommandsExec_ReturnsPerCommandResults()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, QueuedReply, QueuedReply, "*2\r\n+OK\r\n:5\r\n"u8.ToArray());
        await using var client = await RespireClient.CreateAsync("127.0.0.1", server.Port, connectionCount: 1);

        var result = await client.CreateTransaction()
            .Set("k", "v")
            .Incr("counter")
            .ExecuteAsync();

        var commands = server.ReceivedCommands;
        await Assert.That(commands[0]).IsEqualTo("MULTI");
        await Assert.That(commands[1]).IsEqualTo("SET k v");
        await Assert.That(commands[2]).IsEqualTo("INCR counter");
        await Assert.That(commands[3]).IsEqualTo("EXEC");

        await Assert.That(result.Type).IsEqualTo(RespDataType.Array);
        var count = result.AsArray().Length;
        var first = result.AsArray()[0].AsString();
        var second = result.AsArray()[1].AsInteger();
        await Assert.That(count).IsEqualTo(2);
        await Assert.That(first).IsEqualTo("OK");
        await Assert.That(second).IsEqualTo(5);
        result.Dispose();
    }

    [Test]
    public async Task ExecAbort_ThrowsServerException()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "-ERR wrong number of arguments for 'incr' command\r\n"u8.ToArray(),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await RespireClient.CreateAsync("127.0.0.1", server.Port, connectionCount: 1);

        var transaction = client.CreateTransaction().Incr("counter");

        await Assert.That(async () => (await transaction.ExecuteAsync()).Dispose())
            .Throws<RespireServerException>();
    }

    [Test]
    public async Task EmptyTransaction_Throws()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.CreateAsync("127.0.0.1", server.Port, connectionCount: 1);

        var transaction = client.CreateTransaction();

        await Assert.That(async () => (await transaction.ExecuteAsync()).Dispose())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ConcurrentCommands_DoNotInterleaveIntoTransaction()
    {
        // Every command gets +OK except the EXEC-shaped reply script below; the point of the
        // assertion is ordering on the wire: the MULTI..EXEC block must be contiguous even
        // with concurrent traffic on the same connection.
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await RespireClient.CreateAsync("127.0.0.1", server.Port, connectionCount: 1);

        var transaction = client.CreateTransaction();
        for (var i = 0; i < 50; i++)
        {
            transaction.Set($"tx{i}", "v");
        }

        var concurrent = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            concurrent.Add(client.SetAsync($"plain{i}", "v").AsTask());
        }

        var execTask = transaction.ExecuteAsync().AsTask();
        await Task.WhenAll(concurrent);
        (await execTask).Dispose();

        var commands = server.ReceivedCommands;
        var multiIndex = -1;
        var execIndex = -1;
        for (var i = 0; i < commands.Count; i++)
        {
            if (commands[i] == "MULTI")
            {
                multiIndex = i;
            }
            else if (commands[i] == "EXEC")
            {
                execIndex = i;
            }
        }

        await Assert.That(multiIndex).IsGreaterThanOrEqualTo(0);
        await Assert.That(execIndex).IsEqualTo(multiIndex + 51);
        for (var i = multiIndex + 1; i < execIndex; i++)
        {
            await Assert.That(commands[i].StartsWith("SET tx")).IsTrue();
        }
    }

    [Test]
    public async Task Transaction_LargerThanInflightRing_ThrowsInsteadOfSpinning()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        // Ring capacity 4: a 3-command transaction needs 5 slots and could never enqueue.
        await using var connection = await Respire.Networking.RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port, new Respire.Networking.RespireConnectionOptions { MaxInflightCommands = 4 });

        var body = "*3\r\n$3\r\nSET\r\n$1\r\nk\r\n$1\r\nv\r\n"u8.ToArray();

        await Assert.That(async () => await connection.SendTransactionAsync(body, commandCount: 3))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Transaction_IsSingleShot()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, QueuedReply, "*1\r\n+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.CreateAsync("127.0.0.1", server.Port, connectionCount: 1);

        var transaction = client.CreateTransaction().Set("k", "v");
        (await transaction.ExecuteAsync()).Dispose();

        await Assert.That(async () => (await transaction.ExecuteAsync()).Dispose())
            .Throws<InvalidOperationException>();
    }
}
