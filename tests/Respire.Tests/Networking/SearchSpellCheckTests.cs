using System.Text;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchSpellCheckTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task OptionsAndOwnedScoreAssociationsMatchBothProtocols(int protocol)
    {
        var reply = protocol == 2
            ? "*2\r\n*3\r\n+TERM\r\n+helo\r\n*2\r\n*2\r\n+0.5\r\n+hello\r\n*2\r\n+0\r\n+help\r\n*3\r\n+TERM\r\n+xyz\r\n*0\r\n"
            : "%1\r\n+results\r\n%2\r\n+helo\r\n*2\r\n%1\r\n+hello\r\n,0.5\r\n%1\r\n+help\r\n,0\r\n+xyz\r\n*0\r\n";
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var result = await client.Search.SpellCheckAsync("index", "helo xyz", new()
        { Distance = 2, IncludeDictionaries = ["words ü", "more"], ExcludeDictionaries = ["ignored"], Dialect = 4 });
        await Assert.That(server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString)).IsEquivalentTo(
            ["FT.SPELLCHECK", "index", "helo xyz", "DISTANCE", "2", "TERMS", "INCLUDE", "words ü", "TERMS", "INCLUDE", "more", "TERMS", "EXCLUDE", "ignored", "DIALECT", "4"], CollectionOrdering.Matching);
        await client.Search.SpellCheckAsync("index", "anything");
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.SPELLCHECK index anything");
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Term).IsEqualTo("helo");
        await Assert.That(result[0].Suggestions).IsEquivalentTo(
            [new RespireSearchSpellingSuggestion("hello", 0.5), new RespireSearchSpellingSuggestion("help", 0)], CollectionOrdering.Matching);
        await Assert.That(result[1].Term).IsEqualTo("xyz");
        await Assert.That(result[1].Suggestions.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task DictionaryCountsAndOwnedTermsUseExactArguments(int protocol)
    {
        await using var server = Server(command => command.StartsWith("FT.DICTDUMP")
            ? (protocol == 3 ? "~2\r\n+hello\r\n+ü\r\n" : "*2\r\n+hello\r\n+ü\r\n") : ":2\r\n");
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await Assert.That(await client.Search.AddDictionaryTermsAsync("dict", ["hello", "ü"])).IsEqualTo(2L);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.DICTADD dict hello ü");
        var terms = await client.Search.DumpDictionaryAsync("dict");
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.DICTDUMP dict");
        await Assert.That(await client.Search.DeleteDictionaryTermsAsync("dict", ["hello", "ü"])).IsEqualTo(2L);
        await Assert.That(server.ReceivedCommands.Last()).IsEqualTo("FT.DICTDEL dict hello ü");
        await Assert.That(terms).IsEquivalentTo(["hello", "ü"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("*0\r\n")]
    [Arguments("%1\r\n+results\r\n%0\r\n")]
    public async Task EmptySpellcheckResultsAreAccepted(string reply)
    {
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That((await client.Search.SpellCheckAsync("idx", "")).Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("*-1\r\n")]
    [Arguments("%0\r\n")]
    [Arguments("%1\r\n+other\r\n%0\r\n")]
    [Arguments("%1\r\n+results\r\n*0\r\n")]
    [Arguments("%1\r\n+results\r\n%1\r\n:1\r\n*0\r\n")]
    [Arguments("%1\r\n+results\r\n%1\r\n+x\r\n*1\r\n%1\r\n+y\r\n,nan\r\n")]
    [Arguments("*1\r\n*2\r\n+TERM\r\n+x\r\n")]
    [Arguments("*1\r\n*3\r\n+OTHER\r\n+x\r\n*0\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n$-1\r\n*0\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*-1\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*1\r\n*1\r\n+bad\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*1\r\n*2\r\n+bad\r\n+y\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*1\r\n*2\r\n+-1\r\n+y\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*1\r\n*2\r\n+inf\r\n+y\r\n")]
    [Arguments("*1\r\n*3\r\n+TERM\r\n+x\r\n*1\r\n*2\r\n+0\r\n:1\r\n")]
    public async Task MalformedSpellcheckRepliesAreRejected(string reply)
    {
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.SpellCheckAsync("idx", "helo")).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("+1\r\n")]
    [Arguments(":-1\r\n")]
    [Arguments("$-1\r\n")]
    public async Task MalformedDictionaryCountsAreRejected(string reply)
    {
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.AddDictionaryTermsAsync("dict", ["hello"])).Throws<InvalidOperationException>();
        await Assert.That(async () => await client.Search.DeleteDictionaryTermsAsync("dict", ["hello"])).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("*-1\r\n")]
    [Arguments("%0\r\n")]
    [Arguments("*1\r\n$-1\r\n")]
    [Arguments("*1\r\n:1\r\n")]
    public async Task MalformedDictionaryDumpsAreRejected(string reply)
    {
        await using var server = Server(_ => reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        await Assert.That(async () => await client.Search.DumpDictionaryAsync("dict")).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InvalidArgumentsNeverReachServer()
    {
        await using var server = Server(_ => ":0\r\n");
        await using var client = await RespireClient.ConnectAsync(Options(server, 3));
        var search = client.Search;
        Func<Task>[] invalid = [
            () => search.AddDictionaryTermsAsync("", ["x"]).AsTask(),
            () => search.DeleteDictionaryTermsAsync(null!, ["x"]).AsTask(),
            () => search.DumpDictionaryAsync(" ").AsTask(),
            () => search.AddDictionaryTermsAsync("d", null!).AsTask(),
            () => search.AddDictionaryTermsAsync("d", []).AsTask(),
            () => search.AddDictionaryTermsAsync("d", [null!]).AsTask(),
            () => search.DeleteDictionaryTermsAsync("d", [""]).AsTask(),
            () => search.SpellCheckAsync("", "x").AsTask(),
            () => search.SpellCheckAsync("idx", null!).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { Distance = 0 }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { Distance = 5 }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { Dialect = 0 }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { Dialect = 5 }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { IncludeDictionaries = null! }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { ExcludeDictionaries = null! }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { IncludeDictionaries = [null!] }).AsTask(),
            () => search.SpellCheckAsync("idx", "x", new() { ExcludeDictionaries = [""] }).AsTask()];
        foreach (var call in invalid) await Assert.That(call).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT."))).IsFalse();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task PrefixCancellationAndServerErrorsArePreserved(int protocol)
    {
        await using var server = Server(_ => "-ERR dictionary failure\r\n");
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Func<RespireSearchClient, CancellationToken, Task>[] calls = [
            (s, t) => s.AddDictionaryTermsAsync("d", ["hello"], t).AsTask(),
            (s, t) => s.DeleteDictionaryTermsAsync("d", ["hello"], t).AsTask(),
            (s, t) => s.DumpDictionaryAsync("d", t).AsTask(),
            (s, t) => s.SpellCheckAsync("idx", "helo", cancellationToken: t).AsTask()];
        foreach (var call in calls)
        {
            await Assert.That(() => call(prefixed.Search, default)).Throws<NotSupportedException>();
            var error = await Assert.That(() => call(client.Search, canceled.Token)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(canceled.Token);
        }
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT."))).IsFalse();
        foreach (var call in calls) await Assert.That(() => call(client.Search, default)).Throws<RespireServerException>();
    }

    private static FakeRespServer Server(Func<string, string> reply) => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command == "HELLO 3" ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
            : command.StartsWith("FT.") ? Encoding.UTF8.GetBytes(reply(command)) : null,
    };

    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    { Endpoints = { new("127.0.0.1", server.Port) }, Protocol = (RespProtocol)protocol, ThreadPoolMonitoring = false };
}
