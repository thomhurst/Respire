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
    private static ValueTask<RespireClient> ConnectAsync(int port)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
            Connections = 1,
        });

    [Test]
    public async Task Increment_DefaultDelta_SendsIncr()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var result = await client.IncrementAsync("counter");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("INCR counter");
        await Assert.That(result).IsEqualTo(1L);
    }

    [Test]
    public async Task Increment_ExplicitDelta_SendsIncrBy()
    {
        await using var server = new FakeRespServer(":5\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var result = await client.IncrementAsync("counter", 5);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("INCRBY counter 5");
        await Assert.That(result).IsEqualTo(5L);
    }

    [Test]
    public async Task Decrement_DefaultDelta_SendsDecr()
    {
        await using var server = new FakeRespServer(":-1\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var result = await client.DecrementAsync("counter");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DECR counter");
        await Assert.That(result).IsEqualTo(-1L);
    }

    [Test]
    public async Task Decrement_ExplicitDelta_SendsDecrBy()
    {
        await using var server = new FakeRespServer(":-3\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server.Port);

        var result = await client.DecrementAsync("counter", 3);

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DECRBY counter 3");
        await Assert.That(result).IsEqualTo(-3L);
    }
}
