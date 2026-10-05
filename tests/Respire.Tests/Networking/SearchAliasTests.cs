using System.Text;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchAliasTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CommandsPreserveNamesAndReturnOwnedCollections(int protocol)
    {
        await using var server = Server(command => command.StartsWith("FT._LIST") || command.StartsWith("FT.ALIASLIST")
            ? Encoding.UTF8.GetBytes((protocol == 3 ? "~" : "*") + "2\r\n$7\r\nname ü\r\n$6\r\nsecond\r\n")
            : "+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var search = client.Search;
        var indexes = await search.ListIndexesAsync();
        await CheckArguments(server, "FT._LIST");
        var aliases = await search.ListAliasesAsync("index ü");
        await CheckArguments(server, "FT.ALIASLIST", "index ü");
        await search.AddAliasAsync("alias ü", "index ü");
        await CheckArguments(server, "FT.ALIASADD", "alias ü", "index ü");
        await search.UpdateAliasAsync("alias ü", "next index");
        await CheckArguments(server, "FT.ALIASUPDATE", "alias ü", "next index");
        await search.DeleteAliasAsync("alias ü");
        await CheckArguments(server, "FT.ALIASDEL", "alias ü");
        // Replies have been disposed and subsequent commands have reused receive storage.
        await Assert.That(indexes).IsEquivalentTo(["name ü", "second"], CollectionOrdering.Matching);
        await Assert.That(aliases).IsEquivalentTo(["name ü", "second"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("*0\r\n")]
    [Arguments("~0\r\n")]
    public async Task EmptyCollectionsAreValid(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That((await client.Search.ListIndexesAsync()).Count).IsEqualTo(0);
        await Assert.That((await client.Search.ListAliasesAsync("idx")).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("+OK\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("%0\r\n")]
    [Arguments("*1\r\n$-1\r\n")]
    [Arguments("*1\r\n:1\r\n")]
    [Arguments("*1\r\n*0\r\n")]
    public async Task MalformedCollectionsAreRejected(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.ListIndexesAsync()).Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Search.ListAliasesAsync("idx")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidNamesAreRejectedBeforeSending()
    {
        await using var server = Server(_ => "+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        var search = client.Search;
        foreach (var name in new[] { null, "", " " })
        {
            Func<Task>[] calls = [
                () => search.AddAliasAsync(name!, "idx").AsTask(),
                () => search.AddAliasAsync("alias", name!).AsTask(),
                () => search.UpdateAliasAsync(name!, "idx").AsTask(),
                () => search.UpdateAliasAsync("alias", name!).AsTask(),
                () => search.DeleteAliasAsync(name!).AsTask(),
                () => search.ListAliasesAsync(name!).AsTask()];
            foreach (var call in calls)
                await Assert.That(call).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT."))).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EveryCommandPreservesServerErrors(int protocol)
    {
        await using var server = Server(_ => "-ERR denied by test\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var search = client.Search;
        Func<Task>[] calls = [
            () => search.ListIndexesAsync().AsTask(),
            () => search.ListAliasesAsync("idx").AsTask(),
            () => search.AddAliasAsync("alias", "idx").AsTask(),
            () => search.UpdateAliasAsync("alias", "idx").AsTask(),
            () => search.DeleteAliasAsync("alias").AsTask()];
        foreach (var call in calls)
        {
            var error = await Assert.That(call).Throws<RespireServerException>();
            await Assert.That(error!.Message).Contains("denied by test");
        }
    }

    private static FakeRespServer Server(Func<string, byte[]> reply) => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3"
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
            : command.StartsWith("FT.") ? reply(command) : null,
    };

    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        Protocol = (RespProtocol)protocol,
        ThreadPoolMonitoring = false,
    };

    private static async Task CheckArguments(FakeRespServer server, params string[] expected)
        => await Assert.That(server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray())
            .IsEquivalentTo(expected, CollectionOrdering.Matching);
}
