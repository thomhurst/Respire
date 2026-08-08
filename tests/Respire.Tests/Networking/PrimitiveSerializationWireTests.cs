using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PrimitiveSerializationWireTests
{
    [Test]
    public async Task GenericPrimitives_RoundTripWithoutObjectSerializerEncoding()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$2\r\n42\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$1\r\n1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.SetAsync("count", 42);
        var count = await client.GetAsync<int>("count");
        await client.SetAsync("enabled", true);
        var enabled = await client.GetAsync<bool>("enabled");

        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("SET count 42");
        await Assert.That(server.ReceivedCommands[1]).IsEqualTo("GET count");
        await Assert.That(server.ReceivedCommands[2]).IsEqualTo("SET enabled true");
        await Assert.That(server.ReceivedCommands[3]).IsEqualTo("GET enabled");
        await Assert.That(count).IsEqualTo(42);
        await Assert.That(enabled).IsTrue();
    }
}
