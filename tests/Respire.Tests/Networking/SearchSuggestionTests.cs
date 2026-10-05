using System.Text;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchSuggestionTests
{
    [Test]
    [Arguments(2, false, false)]
    [Arguments(2, true, false)]
    [Arguments(2, false, true)]
    [Arguments(2, true, true)]
    [Arguments(3, false, false)]
    [Arguments(3, true, false)]
    [Arguments(3, false, true)]
    [Arguments(3, true, true)]
    public async Task GetEncodesOptionsAndCopiesAllResultShapes(int protocol, bool scores, bool payloads)
    {
        var reply = "*" + (2 * (1 + (scores ? 1 : 0) + (payloads ? 1 : 0))) + "\r\n$5\r\nhello\r\n"
            + (scores ? protocol == 3 ? ",1.5\r\n" : "$3\r\n1.5\r\n" : "")
            + (payloads ? "$2\r\n\0ÿ\r\n" : "") + "$4\r\nhelp\r\n"
            + (scores ? protocol == 3 ? ",2\r\n" : "$1\r\n2\r\n" : "")
            + (payloads ? "$-1\r\n" : "");
        await using var server = Server(command => command.StartsWith("FT.SUGGET") ? Encoding.Latin1.GetBytes(reply) : ":2\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var key = new RespireKey(new byte[] { 0, 255, 32 });
        var result = await client.Search.GetSuggestionsAsync(key, "hel ü", new()
        { Fuzzy = true, WithScores = scores, WithPayloads = payloads, Max = 7 });
        var args = server.ReceivedArguments.Last();
        await Assert.That(args[1]).IsEquivalentTo(new byte[] { 0, 255, 32 }, CollectionOrdering.Matching);
        var expected = new List<string> { "hel ü", "FUZZY" };
        if (scores) expected.Add("WITHSCORES");
        if (payloads) expected.Add("WITHPAYLOADS");
        expected.AddRange(["MAX", "7"]);
        await Assert.That(args.Skip(2).Select(Encoding.UTF8.GetString)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await client.Search.GetSuggestionCountAsync(key);
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Text).IsEqualTo("hello");
        await Assert.That(result[0].Score).IsEqualTo(scores ? 1.5 : (double?)null);
        if (payloads)
            await Assert.That(result[0].Payload!.Value.ToArray()).IsEquivalentTo(new byte[] { 0, 255 }, CollectionOrdering.Matching);
        else
            await Assert.That(result[0].Payload).IsNull();
        await Assert.That(result[1].Payload).IsNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task AddDeleteAndLengthUseExactWireForms(int protocol)
    {
        await using var server = Server(_ => ":1\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var search = client.Search;
        await Assert.That(await search.AddSuggestionAsync("dictionary", "hello ü", -0.5,
            new() { Increment = true, Payload = new byte[] { 0, 255 } })).IsEqualTo(1L);
        var args = server.ReceivedArguments.Last();
        await Assert.That(args.Take(6).Select(Encoding.UTF8.GetString))
            .IsEquivalentTo(["FT.SUGADD", "dictionary", "hello ü", "-0.5", "INCR", "PAYLOAD"], CollectionOrdering.Matching);
        await Assert.That(args[6]).IsEquivalentTo(new byte[] { 0, 255 }, CollectionOrdering.Matching);
        await search.AddSuggestionAsync("dictionary", "hello", 0);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.SUGADD dictionary hello 0");
        await Assert.That(await search.DeleteSuggestionAsync("dictionary", "hello ü")).IsTrue();
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.SUGDEL dictionary hello ü");
        await Assert.That(await search.GetSuggestionCountAsync("dictionary")).IsEqualTo(1L);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.SUGLEN dictionary");
    }

    [Test]
    [Arguments("*1\r\n$4\r\ntext\r\n")]
    [Arguments("%0\r\n")]
    [Arguments("*-1\r\n")]
    [Arguments("*3\r\n:1\r\n,1\r\n$-1\r\n")]
    [Arguments("*3\r\n$1\r\nx\r\n$3\r\nbad\r\n$-1\r\n")]
    [Arguments("*3\r\n$1\r\nx\r\n,1\r\n:1\r\n")]
    public async Task MalformedSuggestionsAreRejected(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.GetSuggestionsAsync("key", "", new() { WithScores = true, WithPayloads = true }))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task EmptyResultsPayloadsAndInfiniteScoresArePreserved()
    {
        await using var server = Server(command => command.Contains("WITHSCORES")
            ? "*3\r\n$1\r\nx\r\n$3\r\ninf\r\n$0\r\n\r\n"u8.ToArray() : "*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await Assert.That((await client.Search.GetSuggestionsAsync("key", "")).Count).IsEqualTo(0);
        var entry = (await client.Search.GetSuggestionsAsync("key", "", new() { WithScores = true, WithPayloads = true }))[0];
        await Assert.That(entry.Score).IsEqualTo(double.PositiveInfinity);
        await Assert.That(entry.Payload.HasValue).IsTrue();
        await Assert.That(entry.Payload!.Value.Length).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidArgumentsDoNotSendCommands()
    {
        await using var server = Server(_ => ":0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        var search = client.Search;
        Func<Task>[] calls = [
            () => search.AddSuggestionAsync("key", null!, 1).AsTask(),
            () => search.AddSuggestionAsync("key", "text", double.NaN).AsTask(),
            () => search.DeleteSuggestionAsync("key", null!).AsTask(),
            () => search.GetSuggestionsAsync("key", null!).AsTask(),
            () => search.GetSuggestionsAsync("key", "", new() { Max = 0 }).AsTask(),
            () => search.GetSuggestionsAsync("key", "", new() { Max = -1 }).AsTask()];
        foreach (var call in calls) await Assert.That(call).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT."))).IsFalse();
    }

    [Test]
    [Arguments("+1\r\n")]
    [Arguments(":-1\r\n")]
    [Arguments("$-1\r\n")]
    public async Task MalformedCountsAreRejected(string reply)
    {
        await using var server = Server(_ => Encoding.UTF8.GetBytes(reply));
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.AddSuggestionAsync("key", "text", 1)).Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Search.DeleteSuggestionAsync("key", "text")).Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Search.GetSuggestionCountAsync("key")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidDeletionFlagIsRejected()
    {
        await using var server = Server(_ => ":2\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.DeleteSuggestionAsync("key", "text")).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("inf", double.PositiveInfinity)]
    [Arguments("INF", double.PositiveInfinity)]
    [Arguments("+inf", double.PositiveInfinity)]
    [Arguments("+InF", double.PositiveInfinity)]
    [Arguments("-inf", double.NegativeInfinity)]
    [Arguments("-INF", double.NegativeInfinity)]
    [Arguments("Infinity", double.PositiveInfinity)]
    [Arguments("-Infinity", double.NegativeInfinity)]
    [Arguments("nan", double.NaN)]
    [Arguments("NaN", double.NaN)]
    public async Task SpecialScoreTokensAcceptServerCaseVariations(string token, double expected)
    {
        var reply = Encoding.UTF8.GetBytes("*2\r\n$1\r\nx\r\n$" + token.Length + "\r\n" + token + "\r\n");
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        var result = await client.Search.GetSuggestionsAsync("key", "x", new() { WithScores = true });
        var score = result[0].Score!.Value;
        if (double.IsNaN(expected)) await Assert.That(double.IsNaN(score)).IsTrue();
        else await Assert.That(score).IsEqualTo(expected);
    }

    [Test]
    public async Task InvalidMaxNamesThePropertyAndIncludesItsValue()
    {
        await using var server = Server(_ => "*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        var error = await Assert.That(async () => await client.Search.GetSuggestionsAsync("key", "x", new() { Max = -1 }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(error!.ParamName).IsEqualTo("options");
        await Assert.That(error.ActualValue).IsEqualTo(-1);
        await Assert.That(error.Message).Contains("Max");
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
}
