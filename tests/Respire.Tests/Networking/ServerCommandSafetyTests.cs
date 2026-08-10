using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerCommandSafetyTests
{
    [Test]
    public async Task AdminServerCommands_AreRejectedWhenAllowAdminIsFalse()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await AssertRequiresAllowAdmin(
            () => client.Server.FlushDatabaseAsync(),
            "FLUSHDB");
        await AssertRequiresAllowAdmin(
            () => client.Server.FlushAllAsync(),
            "FLUSHALL");
        await AssertRequiresAllowAdmin(
            () => client.Server.SetConfigAsync("maxmemory", "1"),
            "CONFIG SET");
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task AdminServerCommands_RunWhenAllowAdminIsTrue()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            AllowAdmin = true,
            Connections = 1,
        });

        await client.Server.FlushDatabaseAsync();
        await client.Server.FlushAllAsync();
        await client.Server.SetConfigAsync("maxmemory", "1");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "FLUSHDB",
            "FLUSHALL",
            "CONFIG SET maxmemory 1",
        });
    }

    private static async Task AssertRequiresAllowAdmin(Func<ValueTask> action, string operation)
    {
        var exception = await Assert.That(async () => await action())
            .ThrowsExactly<NotSupportedException>();
        await Assert.That(exception!.Message).Contains(operation);
        await Assert.That(exception.Message).Contains("RespireOptions.AllowAdmin");
    }
}
