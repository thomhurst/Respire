using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class VariadicCommandTests
{
    [Test]
    public async Task ValueArguments_PreserveEveryCommandShape()
    {
        await using var server = IntegerReplyServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        for (var count = 0; count <= 5; count++)
        {
            var values = Enumerable.Range(1, count).Select(i => (RespireValue)$"v{i}").ToArray();
            await client.Lists.LeftPushAsync("list", values);
        }

        await AssertCommands(server.ReceivedCommands,
            "LPUSH list",
            "LPUSH list v1",
            "LPUSH list v1 v2",
            "LPUSH list v1 v2 v3",
            "LPUSH list v1 v2 v3 v4",
            "LPUSH list v1 v2 v3 v4 v5");
    }

    [Test]
    public async Task KeyArguments_PreserveEveryCommandShape()
    {
        await using var server = IntegerReplyServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        for (var count = 0; count <= 5; count++)
        {
            var keys = Enumerable.Range(1, count).Select(i => (RespireKey)$"k{i}").ToArray();
            await client.Keys.DeleteAsync(keys);
        }

        await AssertCommands(server.ReceivedCommands,
            "DEL",
            "DEL k1",
            "DEL k1 k2",
            "DEL k1 k2 k3",
            "DEL k1 k2 k3 k4",
            "DEL k1 k2 k3 k4 k5");
    }

    private static FakeRespServer IntegerReplyServer()
        => new(Enumerable.Repeat(":1\r\n"u8.ToArray(), 6).ToArray());

    private static async Task AssertCommands(IReadOnlyList<string> actual, params string[] expected)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            await Assert.That(actual[i]).IsEqualTo(expected[i]);
        }
    }
}
