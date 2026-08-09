using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
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
    public async Task EveryCatalogCommand_SerializesItsExactCommandWords()
    {
        var commands = RespireCommands.All.ToArray();
        foreach (var command in commands)
        {
            var buffer = new WriteBuffer(64);
            try
            {
                var writer = new RespWriter(buffer);
                new CatalogCommand(command, []).Write(ref writer);
                var position = 0;
                var status = RespParser.TryParseValue(buffer.WrittenMemory.Span, ref position, out var frame);
                try
                {
                    await Assert.That(status).IsEqualTo(RespParseStatus.Done);
                    await Assert.That(position).IsEqualTo(buffer.Count);
                    var elements = frame.AsArray();
                    var actualWords = new string[elements.Length];
                    for (var index = 0; index < elements.Length; index++)
                    {
                        actualWords[index] = elements[index].AsString();
                    }

                    var words = command.Name.Split(' ');
                    await Assert.That(actualWords.Length).IsEqualTo(words.Length);
                    for (var index = 0; index < words.Length; index++)
                    {
                        await Assert.That(actualWords[index]).IsEqualTo(words[index]);
                    }
                }
                finally
                {
                    frame.Dispose();
                }
            }
            finally
            {
                buffer.Release();
            }
        }
    }

    [Test]
    public async Task Catalog_ClassifiesCommandsThatCannotUseTheMultiplexedPath()
    {
        var commands = RespireCommands.All.ToArray();
        var blocking = commands
            .Where(static command => command.Behavior == RespireCommandBehavior.Blocking)
            .Select(static command => command.Name)
            .ToArray();
        var connectionScoped = commands
            .Where(static command => command.Behavior == RespireCommandBehavior.ConnectionScoped)
            .Select(static command => command.Name)
            .ToArray();

        await Assert.That(blocking).IsEquivalentTo(new[]
        {
            "BLMOVE", "BLMPOP", "BLPOP", "BRPOP", "BRPOPLPUSH", "BZMPOP", "BZPOPMAX", "BZPOPMIN",
        });
        await Assert.That(connectionScoped).IsEquivalentTo(new[]
        {
            "ASKING", "AUTH", "CLIENT", "CLIENT CACHING", "CLIENT NO-EVICT", "CLIENT NO-TOUCH", "CLIENT REPLY",
            "CLIENT SETINFO", "CLIENT SETNAME", "CLIENT TRACKING", "DISCARD", "EXEC", "HELLO", "MONITOR",
            "MULTI", "PSUBSCRIBE", "PSYNC", "PUNSUBSCRIBE", "QUIT", "READONLY", "READWRITE", "REPLCONF",
            "RESET", "SCRIPT", "SCRIPT DEBUG", "SELECT", "SSUBSCRIBE", "SUBSCRIBE", "SUNSUBSCRIBE", "SYNC",
            "UNSUBSCRIBE", "UNWATCH", "WAIT", "WAITAOF", "WATCH",
        });
        await Assert.That(RespireCommands.Stream.XREAD.IsBlocking(["STREAMS", "source", "0"]))
            .IsFalse();
        await Assert.That(RespireCommands.Stream.XREAD.IsBlocking(["block", 1000, "STREAMS", "source", "0"]))
            .IsTrue();
        await Assert.That(RespireCommands.Stream.XREADGROUP.IsBlocking(
                ["GROUP", "workers", "consumer", "BLOCK"u8.ToArray(), 1000, "STREAMS", "source", ">"]))
            .IsTrue();
    }

    [Test]
    public async Task ConnectionScopedCatalogCommands_AreRejectedBeforeSending()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        foreach (var command in RespireCommands.All.ToArray().Where(
                     static command => command.Behavior == RespireCommandBehavior.ConnectionScoped))
        {
            await Assert.That(async () => await client.ExecuteAsync(command))
                .Throws<NotSupportedException>();
        }

        await Assert.That(server.ReceivedCommands).IsEmpty();
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
