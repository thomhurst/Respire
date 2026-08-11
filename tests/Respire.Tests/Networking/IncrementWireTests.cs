using TUnit.Core;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Respire.Tests.Networking;

/// <summary>
/// Wire-format tests for the INCR/DECR fast path: a delta of 1 must go out as the two-token
/// INCR/DECR form (matching StackExchange.Redis), any other delta as INCRBY/DECRBY.
/// </summary>
public class IncrementWireTests
{
    [Test]
    public async Task Increment_DefaultDelta_SendsIncr()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.IncrementAsync("counter");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("INCR counter");
        await Assert.That(result).IsEqualTo(1L);
    }

    [Test]
    public async Task Increment_ExplicitDelta_SendsIncrBy()
    {
        await using var server = new FakeRespServer(":5\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.IncrementAsync("counter", 5);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("INCRBY counter 5");
        await Assert.That(result).IsEqualTo(5L);
    }

    [Test]
    public async Task Decrement_DefaultDelta_SendsDecr()
    {
        await using var server = new FakeRespServer(":-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.DecrementAsync("counter");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DECR counter");
        await Assert.That(result).IsEqualTo(-1L);
    }

    [Test]
    public async Task Decrement_ExplicitDelta_SendsDecrBy()
    {
        await using var server = new FakeRespServer(":-3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var result = await client.DecrementAsync("counter", 3);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DECRBY counter 3");
        await Assert.That(result).IsEqualTo(-3L);
    }

    [Test]
    public async Task Batch_DefaultAndExplicitDelta_SendIncrAndIncrBy()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), ":6\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var one = batch.Increment("counter");
        var five = batch.Increment("counter", 5);
        await batch.ExecuteAsync();

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("INCR counter");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("INCRBY counter 5");
        await Assert.That(await one).IsEqualTo(1L);
        await Assert.That(await five).IsEqualTo(6L);
    }

    [Test]
    public async Task Transaction_DefaultDelta_SendsIncrInsideMultiExec()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n:1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var transaction = client.CreateTransaction();
        var incremented = transaction.Increment("counter");
        await transaction.CommitAsync();

        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("INCR counter");
        await Assert.That(incremented.Result).IsEqualTo(1L);
    }
}
