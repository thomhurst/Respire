using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CommandCatalogTests
{
    [Test]
    public async Task Catalog_ContainsEveryAuditedCommandExactlyOnce()
    {
        var commands = RespireCommands.All.ToArray();

        await Assert.That(commands.Length).IsEqualTo(621);
        await Assert.That(commands.Select(static command => command.Name).Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(commands.Length);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.Redis)))
            .IsEqualTo(597);
        await Assert.That(commands.Count(static command => command.Sources.HasFlag(RespireCommandSource.Valkey)))
            .IsEqualTo(463);
    }

    [Test]
    public async Task EveryCatalogCommand_WritesItsExactCommandWords()
    {
        var commands = RespireCommands.All.ToArray();
        await using var server = new FakeRespServer(
            Enumerable.Repeat(FakeRespServer.OkReply, commands.Length).ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        foreach (var command in commands)
        {
            using var result = await client.ExecuteAsync(command);
            await Assert.That(result.AsString()).IsEqualTo("OK");
        }

        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(commands.Length);
        for (var i = 0; i < commands.Length; i++)
        {
            await Assert.That(server.ReceivedCommands[i]).IsEqualTo(commands[i].Name);
        }
    }

    [Test]
    public async Task CatalogCommand_PreservesArgumentsAsSingleTokens()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        using var result = await client.ExecuteAsync(
            RespireCommands.Json.JSON_SET, "document", "$", "{\"message\":\"hello world\"}");

        await Assert.That(server.ReceivedCommands.Single())
            .IsEqualTo("JSON.SET document $ {\"message\":\"hello world\"}");
    }

    [Test]
    public async Task CatalogCommand_PropagatesServerErrors()
    {
        await using var server = new FakeRespServer("-ERR catalog failure\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.ExecuteAsync(RespireCommands.String.GETEX, "key"))
            .Throws<RespireServerException>()
            .WithMessage("ERR catalog failure");
    }
}
