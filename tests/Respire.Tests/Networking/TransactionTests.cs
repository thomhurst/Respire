using Respire.Protocol;
using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// MULTI/EXEC wire tests against the fake server: framing order, discarded QUEUED replies,
/// typed pending completion, abort handling, and the read-before-commit guard.
/// </summary>
public class TransactionTests
{
    private static readonly byte[] QueuedReply = "+QUEUED\r\n"u8.ToArray();

    private static ValueTask<RespireClient> ConnectAsync(int port)
        => FakeRespServer.ConnectClientAsync(port);

    [Test]
    public async Task Transaction_SendsMultiCommandsExec_CompletesTypedPendings()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, QueuedReply, QueuedReply, "*2\r\n+OK\r\n:5\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        var set = transaction.SetAsync("k", "v");
        var incremented = transaction.IncrementAsync("counter", 5);
        var committed = await transaction.CommitAsync();

        var commands = server.ReceivedCommands;
        await Assert.That(commands[0]).IsEqualTo("MULTI");
        await Assert.That(commands[1]).IsEqualTo("SET k v");
        await Assert.That(commands[2]).IsEqualTo("INCRBY counter 5");
        await Assert.That(commands[3]).IsEqualTo("EXEC");

        await Assert.That(committed).IsTrue();
        await Assert.That(set.Result).IsTrue();
        await Assert.That(incremented.Result).IsEqualTo(5);
        await Assert.That(await incremented).IsEqualTo(5);
    }

    [Test]
    public async Task PendingResult_BeforeCommit_ThrowsInsteadOfDeadlocking()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        var pending = transaction.GetStringAsync("k");

        await Assert.That(() => _ = pending.Result).Throws<InvalidOperationException>();
        await transaction.DisposeAsync();
    }

    [Test]
    public async Task AbortedExec_ReturnsFalse_PendingsReportAborted()
    {
        // EXEC replies null when a watched key changed: MULTI +OK, QUEUED, then *-1.
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, QueuedReply, "*-1\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        var pending = transaction.IncrementAsync("counter");
        var committed = await transaction.CommitAsync();

        await Assert.That(committed).IsFalse();
        await Assert.That(() => _ = pending.Result).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task ExecAbort_ThrowsServerException()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "-ERR wrong number of arguments for 'incr' command\r\n"u8.ToArray(),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        _ = transaction.IncrementAsync("counter");

        await Assert.That(async () => await transaction.CommitAsync()).Throws<RespireServerException>();
    }

    [Test]
    public async Task CommandError_PreservesQueuedCommandName()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            QueuedReply,
            "*1\r\n-WRONGTYPE bad value\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        var pending = transaction.GetStringAsync("key");

        await transaction.CommitAsync();
        var error = Assert.Throws<RespireServerException>(() => _ = pending.Result);
        await Assert.That(error.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task EmptyTransaction_CommitsAsNoOpAndBecomesCompleted()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();

        await Assert.That(await transaction.CommitAsync()).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(async () => await transaction.CommitAsync()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyWatchedTransaction_ReleasesWithoutMultiExec()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server.Port);
        await using var transaction = await client.CreateTransactionAsync(["watched"]);

        await Assert.That(await transaction.CommitAsync()).IsTrue();

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["WATCH watched"]);
    }

    [Test]
    public async Task RejectedCommand_DoesNotCorruptFollowingTransaction()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, QueuedReply, "*1\r\n+OK\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();

        await Assert.That(() => transaction.SetAsync("invalid", RespireValue.Null))
            .Throws<ArgumentException>();
        var pending = transaction.SetAsync("valid", "value");
        var committed = await transaction.CommitAsync();

        await Assert.That(transaction.Count).IsEqualTo(1);
        await Assert.That(committed).IsTrue();
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["MULTI", "SET valid value", "EXEC"]);
    }

    [Test]
    public async Task ConcurrentCommands_DoNotInterleaveIntoTransaction()
    {
        // Every command gets +OK; the point of the assertion is ordering on the wire: the
        // MULTI..EXEC block must be contiguous even with concurrent traffic on the connection.
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        for (var i = 0; i < 50; i++)
        {
            _ = transaction.SetAsync($"tx{i}", "v");
        }

        var concurrent = new List<Task>();
        for (var i = 0; i < 50; i++)
        {
            concurrent.Add(client.SetAsync($"plain{i}", "v").AsTask());
        }

        var commitTask = transaction.CommitAsync().AsTask();
        await Task.WhenAll(concurrent);
        await commitTask;

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
        await using var client = await ConnectAsync(server.Port);

        var transaction = client.CreateTransaction();
        _ = transaction.SetAsync("k", "v");
        await transaction.CommitAsync();

        await Assert.That(async () => await transaction.CommitAsync()).Throws<InvalidOperationException>();
    }
}
