using System.Globalization;
using System.Text;
using Respire.Search;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchOptionTests
{
    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)]
    [Arguments(5)] [Arguments(6)] [Arguments(7)] [Arguments(8)] [Arguments(9)]
    [Arguments(10)] [Arguments(11)] [Arguments(12)]
    public async Task QueryModifiersHaveSeparateCountedWireTokens(int option)
    {
        (RespireSearchQueryOptions options, string[] expected) = option switch
        {
            0 => (new RespireSearchQueryOptions { InKeys = ["doc:1", "doc:2"] }, new[] { "INKEYS", "2", "doc:1", "doc:2" }),
            1 => (new() { InFields = ["title", "body"] }, ["INFIELDS", "2", "title", "body"]),
            2 => (new() { Highlight = new() { Fields = ["title"], Tags = ("<mark>", "</mark>") } }, ["HIGHLIGHT", "FIELDS", "1", "title", "TAGS", "<mark>", "</mark>"]),
            3 => (new() { Summarize = new() { Fields = ["body"], Fragments = 2, Length = 10, Separator = " | " } }, ["SUMMARIZE", "FIELDS", "1", "body", "FRAGS", "2", "LEN", "10", "SEPARATOR", " | "]),
            4 => (new() { Slop = 0 }, ["SLOP", "0"]),
            5 => (new() { InOrder = true }, ["INORDER"]),
            6 => (new() { Language = "french" }, ["LANGUAGE", "french"]),
            7 => (new() { Scorer = "DISMAX" }, ["SCORER", "DISMAX"]),
            8 => (new() { WithScores = true, ExplainScore = true }, ["WITHSCORES", "EXPLAINSCORE"]),
            9 => (new() { Verbatim = true }, ["VERBATIM"]),
            10 => (new() { NoStopWords = true }, ["NOSTOPWORDS"]),
            11 => (new() { WithPayloads = true }, ["WITHPAYLOADS"]),
            _ => (new() { WithSortKeys = true }, ["WITHSORTKEYS"]),
        };
        await using var server = Server(Array(Integer(0)));
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), options));
        await Assert.That(LastArguments(server)).IsEquivalentTo(new[] { "FT.SEARCH", "idx", "*" }.Concat(expected).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    public async Task InKeysPreservesEmptyAndBinaryKeys()
    {
        await using var server = Server(Array(Integer(0)));
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        byte[] binary = [0, 255, 128];
        await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), new() { InKeys = [RespireKey.Empty, binary] }));
        var arguments = server.ReceivedArguments.Last();
        await Assert.That(arguments[^2]).IsEmpty();
        await Assert.That(arguments[^1]).IsEquivalentTo(binary, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)]
    [Arguments(4)] [Arguments(5)] [Arguments(6)] [Arguments(7)]
    public async Task IndexModifiersPrecedeSchemaAndCountStopwords(int option)
    {
        var definition = new RespireSearchIndexDefinition { Fields = [new("title", RespireSearchFieldType.Text)] };
        (RespireSearchIndexDefinition selected, string[] expected) = option switch
        {
            0 => (definition with { StopWords = ["the", "and"] }, new[] { "STOPWORDS", "2", "the", "and" }),
            1 => (definition with { StopWords = [] }, ["STOPWORDS", "0"]),
            2 => (definition with { Language = "french" }, ["LANGUAGE", "french"]),
            3 => (definition with { LanguageField = "language" }, ["LANGUAGE_FIELD", "language"]),
            4 => (definition with { Score = 0.25 }, ["SCORE", "0.25"]),
            5 => (definition with { TemporarySeconds = 60 }, ["TEMPORARY", "60"]),
            6 => (definition with { SkipInitialScan = true }, ["SKIPINITIALSCAN"]),
            _ => (definition with { Fields = [new("title", RespireSearchFieldType.Text) { Phonetic = RespireSearchPhoneticMatcher.English }] }, []),
        };
        await using var server = Server("+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await client.Search.CreateIndexAsync("idx", selected);
        var suffix = option == 7 ? new[] { "SCHEMA", "title", "TEXT", "PHONETIC", "dm:en" } : ["SCHEMA", "title", "TEXT"];
        await Assert.That(LastArguments(server)).IsEquivalentTo(new[] { "FT.CREATE", "idx", "ON", "HASH" }.Concat(expected).Concat(suffix).ToArray(), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(2, false, 0)] [Arguments(2, true, 0)] [Arguments(3, false, 0)] [Arguments(3, true, 0)]
    [Arguments(2, false, 1)] [Arguments(2, true, 1)] [Arguments(3, false, 1)] [Arguments(3, true, 1)]
    [Arguments(2, false, 2)] [Arguments(2, true, 2)] [Arguments(3, false, 2)] [Arguments(3, true, 2)]
    public async Task MetadataAndTextRemainOwnedAfterNextReply(int protocol, bool noContent, int entryPoint)
    {
        byte[] payload = [0, 255, 128];
        var explanation = Array(Bulk("root"), Array(Bulk("child")));
        var score = Array(Bulk("0.5"), explanation);
        var fields = Array(Bulk("title"), Bulk("<mark>dogs</mark>"), Bulk("body"), Bulk("dogs | cats | "));
        var reply = protocol == 2
            ? noContent ? Array(Integer(1), Bulk("doc:1"), score, Bulk(payload), Bulk("#5"))
                : Array(Integer(1), Bulk("doc:1"), score, Bulk(payload), Bulk("#5"), fields)
            : Map(Bulk("total_results"), Integer(1), Bulk("results"), Array(noContent
                ? Map(Bulk("id"), Bulk("doc:1"), Bulk("score"), score, Bulk("payload"), Bulk(payload), Bulk("sortkey"), Bulk("#5"))
                : Map(Bulk("id"), Bulk("doc:1"), Bulk("score"), score, Bulk("payload"), Bulk(payload), Bulk("sortkey"), Bulk("#5"), Bulk("extra_attributes"), fields)));
        await using var server = Server(reply, profile: entryPoint == 2);
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var query = new RespireSearchQuery(RespireSearchQueryBuilder.Text("dogs"), new()
        {
            NoContent = noContent, WithScores = true, ExplainScore = true, WithPayloads = true, WithSortKeys = true,
            Highlight = noContent ? null : new() { Fields = ["title"] },
            Summarize = noContent ? null : new() { Fields = ["body"], Separator = " | " },
        });
        var result = entryPoint switch
        {
            0 => await client.Search.SearchAsync("idx", query),
            1 => await client.Search.VectorSearchAsync("idx", new("embedding", new byte[8], 1), query.Options),
            _ => (await client.Search.ProfileSearchAsync("idx", query)).Result,
        };
        await client.PingAsync();
        var document = result.Documents.Single();
        await Assert.That(document.Score).IsEqualTo(0.5);
        await Assert.That(document.ScoreExplanation!.Items[0].Scalar).IsEqualTo("root");
        await Assert.That(document.ScoreExplanation.Items[1].Items[0].Scalar).IsEqualTo("child");
        await Assert.That(document.Payload!.Value.ToArray()).IsEquivalentTo(payload, CollectionOrdering.Matching);
        await Assert.That(document.SortKey!.Scalar).IsEqualTo("#5");
        if (noContent) await Assert.That(document.Fields).IsEmpty();
        else
        {
            await Assert.That(document.TextResults["title"]).IsEqualTo(new RespireSearchTextResult("<mark>dogs</mark>", true, false));
            await Assert.That(document.TextResults["body"]).IsEqualTo(new RespireSearchTextResult("dogs | cats | ", false, true));
        }
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)]
    [Arguments(5)] [Arguments(6)] [Arguments(7)] [Arguments(8)] [Arguments(9)]
    public async Task InvalidQueryOptionsFailBeforeSending(int option)
    {
        RespireSearchQueryOptions options = option switch
        {
            0 => new RespireSearchQueryOptions { ExplainScore = true },
            1 => new() { NoContent = true, Highlight = new() },
            2 => new() { NoContent = true, Summarize = new() },
            3 => new() { Slop = -1 },
            4 => new() { Summarize = new() { Fragments = 0 } },
            5 => new() { Summarize = new() { Length = -1 } },
            6 => new() { InFields = [""] },
            7 => new() { Language = " " },
            8 => new() { Scorer = "" },
            _ => new() { Highlight = new() { Fields = [""] } },
        };
        await using var server = Server(Array(Integer(0)));
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await Assert.That(async () => await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), options))).Throws<ArgumentException>();
        await Assert.That(server.ReceivedArguments.Any(args => Encoding.UTF8.GetString(args[0]) == "FT.SEARCH")).IsFalse();
    }

    [Test]
    public async Task CollectCountsEveryNestedTokenAndPreservesEntryMaps()
    {
        var top = Array(Array(Bulk("title"), Bulk("dogs"), Bulk("rating"), Bulk("5")));
        await using var server = Server(Array(Integer(1), Array(Bulk("top"), top)));
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        var result = await client.Search.AggregateAsync("idx", RespireSearchQueryBuilder.MatchAll(), new()
        {
            Stages = [RespireSearchAggregateStage.GroupBy([], RespireSearchReducer.Collect(new()
            {
                Fields = ["title", "@rating"], Distinct = true,
                SortBy = [new("rating", RespireSearchSortDirection.Descending)], Limit = (0, 5),
            }, "top"))],
        });
        await Assert.That(LastArguments(server)).IsEquivalentTo(new[] { "FT.AGGREGATE", "idx", "*", "GROUPBY", "0", "REDUCE", "COLLECT", "12", "FIELDS", "2", "@title", "@rating", "DISTINCT", "SORTBY", "2", "@rating", "DESC", "LIMIT", "0", "5", "AS", "top" }, CollectionOrdering.Matching);
        await Assert.That(result.StructuredRows[0]["top"].Items[0].Items[1].Scalar).IsEqualTo("dogs");
    }

    [Test]
    [Arguments(2)] [Arguments(3)]
    public async Task EveryReplyFlagCombinationPreservesSlotsAcrossDocuments(int protocol)
    {
        for (var flags = 0; flags < 16; flags++)
        {
            var options = new RespireSearchQueryOptions
            {
                NoContent = (flags & 1) != 0, WithScores = (flags & 2) != 0,
                WithPayloads = (flags & 4) != 0, WithSortKeys = (flags & 8) != 0,
            };
            var rows = new List<byte[]>();
            for (var row = 0; row < 2; row++)
            {
                var items = new List<byte[]>();
                if (protocol == 3) items.Add(Bulk("id"));
                items.Add(Bulk("doc:" + row));
                if (options.WithScores)
                {
                    if (protocol == 3) items.Add(Bulk("score"));
                    items.Add(Bulk("0.5"));
                }
                if (options.WithPayloads)
                {
                    if (protocol == 3) items.Add(Bulk("payload"));
                    items.Add(row == 0 ? "$-1\r\n"u8.ToArray() : Bulk(""));
                }
                if (options.WithSortKeys)
                {
                    if (protocol == 3) items.Add(Bulk("sortkey"));
                    items.Add(row == 0 ? "$-1\r\n"u8.ToArray() : Bulk("#5"));
                }
                if (!options.NoContent)
                {
                    if (protocol == 3) items.Add(Bulk("extra_attributes"));
                    items.Add(Array(Bulk("title"), Bulk("dogs")));
                }
                if (protocol == 3) rows.Add(Map(items.ToArray()));
                else rows.AddRange(items);
            }
            var reply = protocol == 2 ? Array([Integer(2), .. rows])
                : Map(Bulk("total_results"), Integer(2), Bulk("results"), Array(rows.ToArray()));
            await using var server = Server(reply);
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var result = await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), options));
            await Assert.That(result.Documents.Select(document => document.Id).ToArray()).IsEquivalentTo(["doc:0", "doc:1"], CollectionOrdering.Matching);
            await Assert.That(result.Documents[1].Score).IsEqualTo(options.WithScores ? 0.5 : (double?)null);
            await Assert.That(result.Documents[0].Payload).IsNull();
            if (options.WithPayloads) await Assert.That(result.Documents[1].Payload!.Value.Length).IsEqualTo(0);
            else await Assert.That(result.Documents[1].Payload).IsNull();
            await Assert.That(result.Documents[0].SortKey).IsNull();
            await Assert.That(result.Documents[1].SortKey?.Scalar).IsEqualTo(options.WithSortKeys ? "#5" : null);
            await Assert.That(result.Documents[1].Fields.Count).IsEqualTo(options.NoContent ? 0 : 1);
        }
    }

    [Test]
    public async Task QuerySelectionsCopyInputLists()
    {
        byte[] bytes = [0, 255];
        var keys = new List<RespireKey> { bytes };
        var fields = new List<string> { "title" };
        var options = new RespireSearchQueryOptions
        {
            InKeys = keys, InFields = fields,
            Highlight = new() { Fields = fields }, Summarize = new() { Fields = fields },
        };
        keys.Clear(); fields[0] = "body";
        await using var server = Server(Array(Integer(0)));
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), options with { }));
        await Assert.That(server.ReceivedArguments.Last()[5]).IsEquivalentTo(new byte[] { 0, 255 }, CollectionOrdering.Matching);
        await Assert.That(options.InFields[0]).IsEqualTo("title");
        await Assert.That(options.Highlight!.Fields[0]).IsEqualTo("title");
        await Assert.That(options.Summarize!.Fields[0]).IsEqualTo("title");
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)] [Arguments(5)]
    public async Task InvalidSchemaOptionsFailBeforeSending(int option)
    {
        var valid = new RespireSearchIndexDefinition { Fields = [new("title", RespireSearchFieldType.Text)] };
        var invalid = option switch
        {
            0 => valid with { Score = double.NaN },
            1 => valid with { Score = 1.1 },
            2 => valid with { TemporarySeconds = 0 },
            3 => valid with { StopWords = [""] },
            4 => valid with { Fields = [new("tag", RespireSearchFieldType.Tag) { Phonetic = RespireSearchPhoneticMatcher.English }] },
            _ => valid with { Fields = [new("title", RespireSearchFieldType.Text) { Phonetic = (RespireSearchPhoneticMatcher)99 }] },
        };
        await using var server = Server("+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await Assert.That(async () => await client.Search.CreateIndexAsync("idx", invalid)).Throws<ArgumentException>();
        await Assert.That(server.ReceivedArguments.Any(args => Encoding.UTF8.GetString(args[0]) == "FT.CREATE")).IsFalse();
    }

    [Test]
    [Arguments(0)] [Arguments(1)] [Arguments(2)] [Arguments(3)] [Arguments(4)]
    public async Task InvalidCollectOptionsFailDuringReducerConstruction(int option)
    {
        RespireSearchCollectOptions invalid = option switch
        {
            0 => new(),
            1 => new() { AllFields = true, Fields = ["title"] },
            2 => new() { Fields = ["@@"] },
            3 => new() { AllFields = true, Limit = (-1, 1) },
            _ => new() { AllFields = true, SortBy = [new("title", (RespireSearchSortDirection)99)] },
        };
        await Assert.That(() => RespireSearchReducer.Collect(invalid)).Throws<ArgumentException>();
    }

    [Test]
    [Arguments(2)] [Arguments(3)]
    public async Task MalformedScoreExplanationIsRejected(int protocol)
    {
        var badScore = Array(Bulk("0.5"));
        var reply = protocol == 2 ? Array(Integer(1), Bulk("doc:1"), badScore)
            : Map(Bulk("total_results"), Integer(1), Bulk("results"), Array(Map(Bulk("id"), Bulk("doc:1"), Bulk("score"), badScore)));
        await using var server = Server(reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        await Assert.That(async () => await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(),
            new() { NoContent = true, WithScores = true, ExplainScore = true }))).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(2)] [Arguments(3)]
    public async Task DefaultTextSelectionPreservesOverlapAndSkipsCollectionValues(int protocol)
    {
        var fields = Array(Bulk("title"), Bulk("<b>dogs</b>..."), Bulk("items"), Array(Bulk("dogs")));
        var reply = protocol == 2 ? Array(Integer(1), Bulk("doc:1"), fields)
            : Map(Bulk("total_results"), Integer(1), Bulk("results"), Array(Map(Bulk("id"), Bulk("doc:1"), Bulk("extra_attributes"), fields)));
        await using var server = Server(reply);
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var result = await client.Search.SearchAsync("idx", new(RespireSearchQueryBuilder.MatchAll(), new() { Highlight = new(), Summarize = new() }));
        await Assert.That(result.Documents[0].TextResults["title"]).IsEqualTo(new RespireSearchTextResult("<b>dogs</b>...", true, true));
        await Assert.That(result.Documents[0].TextResults.ContainsKey("items")).IsFalse();
        await Assert.That(result.Documents[0].StructuredFields["items"].Items[0].Scalar).IsEqualTo("dogs");
    }

    [Test]
    [Arguments(RespireSearchPhoneticMatcher.English, "dm:en")]
    [Arguments(RespireSearchPhoneticMatcher.French, "dm:fr")]
    [Arguments(RespireSearchPhoneticMatcher.Portuguese, "dm:pt")]
    [Arguments(RespireSearchPhoneticMatcher.Spanish, "dm:es")]
    public async Task PhoneticMatchersUseServerTokens(RespireSearchPhoneticMatcher matcher, string token)
    {
        await using var server = Server("+OK\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server, 2));
        await client.Search.CreateIndexAsync("idx", new() { Fields = [new("title", RespireSearchFieldType.Text) { Phonetic = matcher }] });
        await Assert.That(LastArguments(server)[^2..]).IsEquivalentTo(new[] { "PHONETIC", token }, CollectionOrdering.Matching);
    }

    private static FakeRespServer Server(byte[] reply, bool profile = false) => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) =>
        {
            if (command == "HELLO 3") return "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
            if (!command.StartsWith("FT.", StringComparison.Ordinal)) return null;
            return profile ? Array(reply, Array()) : reply;
        },
    };

    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = (RespProtocol)protocol, ThreadPoolMonitoring = false,
    };

    private static string[] LastArguments(FakeRespServer server)
        => server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();

    private static byte[] Bulk(string text) => Bulk(Encoding.UTF8.GetBytes(text));
    private static byte[] Bulk(byte[] bytes) => Join(Encoding.ASCII.GetBytes("$" + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n"), bytes, "\r\n"u8.ToArray());
    private static byte[] Integer(int value) => Encoding.ASCII.GetBytes(":" + value.ToString(CultureInfo.InvariantCulture) + "\r\n");
    private static byte[] Array(params byte[][] items) => Collection('*', items.Length, items);
    private static byte[] Map(params byte[][] items) => Collection('%', items.Length / 2, items);
    private static byte[] Collection(char prefix, int count, byte[][] items)
        => Join([Encoding.ASCII.GetBytes(prefix + count.ToString(CultureInfo.InvariantCulture) + "\r\n"), .. items]);
    private static byte[] Join(params byte[][] parts)
    {
        using var stream = new MemoryStream();
        foreach (var part in parts) stream.Write(part);
        return stream.ToArray();
    }
}
