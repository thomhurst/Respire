using System.Text;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchSynonymTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CommandsPreserveArgumentsAndOwnedMemberships(int protocol)
    {
        var pairs = "$3\r\ncar\r\n*2\r\n$3\r\none\r\n$3\r\ntwo\r\n$4\r\nauto\r\n*1\r\n$3\r\none\r\n";
        await using var server = Server(command => command.StartsWith("FT.SYNDUMP")
            ? Encoding.UTF8.GetBytes((protocol == 3 ? "%2\r\n" : "*4\r\n") + pairs)
            : command.StartsWith("FT.TAGVALS") ? Encoding.UTF8.GetBytes((protocol == 3 ? "~" : "*") + "2\r\n$3\r\nred\r\n$4\r\nblue\r\n")
            : "+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var search = client.Search;
        await search.UpdateSynonymsAsync("index ü", "group one", ["car", "motor car"]);
        await CheckArguments(server, "FT.SYNUPDATE", "index ü", "group one", "car", "motor car");
        await search.UpdateSynonymsAsync("index ü", "group two", ["SKIPINITIALSCAN", "auto"], skipInitialScan: true);
        await CheckArguments(server, "FT.SYNUPDATE", "index ü", "group two", "SKIPINITIALSCAN", "SKIPINITIALSCAN", "auto");
        var synonyms = await search.GetSynonymsAsync("index ü");
        await CheckArguments(server, "FT.SYNDUMP", "index ü");
        var tags = await search.GetTagValuesAsync("index ü", "tag field");
        await CheckArguments(server, "FT.TAGVALS", "index ü", "tag field");
        await search.UpdateSynonymsAsync("index ü", "group one", ["vehicle"]);
        await Assert.That(synonyms["car"]).IsEquivalentTo(["one", "two"], CollectionOrdering.Matching);
        await Assert.That(synonyms["auto"]).IsEquivalentTo(["one"], CollectionOrdering.Matching);
        await Assert.That(tags).IsEquivalentTo(["red", "blue"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task EmptyRepliesAreValid(int protocol)
    {
        await using var server = Server(command => protocol == 3
            ? command.StartsWith("FT.SYNDUMP") ? "%0\r\n"u8.ToArray() : "~0\r\n"u8.ToArray()
            : "*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await Assert.That((await client.Search.GetSynonymsAsync("idx")).Count).IsEqualTo(0);
        await Assert.That((await client.Search.GetTagValuesAsync("idx", "tags")).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("*1\r\n$1\r\na\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("~0\r\n")]
    [Arguments("*2\r\n:1\r\n*0\r\n")]
    [Arguments("*2\r\n$1\r\na\r\n$1\r\nb\r\n")]
    [Arguments("*2\r\n$1\r\na\r\n*1\r\n$-1\r\n")]
    [Arguments("*4\r\n$1\r\na\r\n*0\r\n$1\r\na\r\n*0\r\n")]
    public async Task MalformedSynonymRepliesAreRejected(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.GetSynonymsAsync("idx")).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("%0\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("*1\r\n:1\r\n")]
    [Arguments("*1\r\n$-1\r\n")]
    public async Task MalformedTagRepliesAreRejected(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.GetTagValuesAsync("idx", "tags")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidNamesTermsAndAmbiguousFirstTermAreRejectedBeforeSending()
    {
        await using var server = Server(_ => "+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        var search = client.Search;
        Func<Task>[] calls = [
            () => search.UpdateSynonymsAsync("", "g", ["term"]).AsTask(),
            () => search.UpdateSynonymsAsync("idx", " ", ["term"]).AsTask(),
            () => search.UpdateSynonymsAsync("idx", "g", null!).AsTask(),
            () => search.UpdateSynonymsAsync("idx", "g", []).AsTask(),
            () => search.UpdateSynonymsAsync("idx", "g", [null!]).AsTask(),
            () => search.UpdateSynonymsAsync("idx", "g", [" "]).AsTask(),
            () => search.UpdateSynonymsAsync("idx", "g", ["skipinitialscan", "car"]).AsTask(),
            () => search.GetSynonymsAsync(null!).AsTask(),
            () => search.GetTagValuesAsync("", "tags").AsTask(),
            () => search.GetTagValuesAsync("idx", " ").AsTask()];
        foreach (var call in calls) await Assert.That(call).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT."))).IsFalse();
    }

    private static FakeRespServer Server(Func<string, byte[]> reply) => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray()
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
