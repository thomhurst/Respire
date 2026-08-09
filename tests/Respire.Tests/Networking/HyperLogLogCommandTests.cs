using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class HyperLogLogCommandTests
{
    [Test]
    public async Task EveryHyperLogLogCommand_WritesExpectedFrameAndParsesReply()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(), ":42\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.HyperLogLog.AddAsync("visitors", "a", "b")).IsTrue();
        await Assert.That(await client.HyperLogLog.CountAsync("visitors", "archive")).IsEqualTo(42);
        await client.HyperLogLog.MergeAsync("all", "visitors", "archive");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "PFADD visitors a b",
            "PFCOUNT visitors archive",
            "PFMERGE all visitors archive",
        });
    }

    [Test]
    public async Task HyperLogLogCommands_RequireInputs()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.HyperLogLog.AddAsync("key", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.HyperLogLog.CountAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await client.HyperLogLog.MergeAsync("key", [])).Throws<ArgumentException>();
    }
}
