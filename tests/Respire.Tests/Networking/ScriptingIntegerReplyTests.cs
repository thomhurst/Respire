using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ScriptingIntegerReplyTests
{
    [Test]
    [MatrixDataSource]
    public async Task ExecuteIntegerAsync_RequiresRespInteger(
        [Matrix(RespireProtocol.Resp2, RespireProtocol.Resp3)] RespireProtocol protocol,
        [Matrix(false, true)] bool function)
    {
        byte[] numericText = "$1\r\n4\r\n"u8.ToArray();
        byte[] nullReply = protocol == RespireProtocol.Resp2 ? "$-1\r\n"u8.ToArray() : "_\r\n"u8.ToArray();
        byte[][] replies = [numericText, nullReply, ":0\r\n"u8.ToArray(), ":-4\r\n"u8.ToArray(), numericText];

        if (protocol == RespireProtocol.Resp3)
        {
            replies = ["%1\r\n+proto\r\n:3\r\n"u8.ToArray(), .. replies];
        }

        await using var server = new FakeRespServer(replies);
        // The test owns the fixture connection and selects its RESP version.
#pragma warning disable RESP010
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = protocol,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
        });
#pragma warning restore RESP010
        var script = RespireScript.Create("return redis.call('GET', KEYS[1])", readOnly: true);
        var redisFunction = RespireFunction.Create("get_count", readOnly: true);

        var bulkError = await Assert.That(async () => await ExecuteIntegerAsync()).ThrowsExactly<RespireProtocolException>();
        await Assert.That(bulkError!.Message).Contains("BulkString");
        var nullError = await Assert.That(async () => await ExecuteIntegerAsync()).ThrowsExactly<RespireProtocolException>();
        await Assert.That(nullError!.Message).Contains("Null");
        await Assert.That(await ExecuteIntegerAsync()).IsEqualTo(0L);
        await Assert.That(await ExecuteIntegerAsync()).IsEqualTo(-4L);

        long converted = function
            ? await client.Functions.ExecuteAsync<long>(redisFunction, "count")
            : await client.Scripts.ExecuteAsync<long>(script, "count");
        await Assert.That(converted).IsEqualTo(4L);

        ValueTask<long> ExecuteIntegerAsync()
        {
            return function
                ? client.Functions.ExecuteIntegerAsync(redisFunction, "count")
                : client.Scripts.ExecuteIntegerAsync(script, "count");
        }
    }
}
