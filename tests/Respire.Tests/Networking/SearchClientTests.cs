using Respire.Extensions.Search;
using Respire.Protocol;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SearchClientTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task AggregateParsesResp3RowsAndSortsBySeparateTokens()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "FT.AGGREGATE idx * SORTBY 2 @count DESC" => "%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%1\r\n$16\r\nextra_attributes\r\n%1\r\n$4\r\nname\r\n$3\r\nfoo\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        var result = await search.AggregateAsync("idx", "*", new() { SortBy = ["@count DESC"] });

        await Assert.That(server.ReceivedCommands.Contains("FT.AGGREGATE idx * SORTBY 2 @count DESC")).IsTrue();
        var arguments = server.ReceivedArguments[^1].Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(arguments[^4..]).IsEquivalentTo(["SORTBY", "2", "@count", "DESC"], CollectionOrdering.Matching);
        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Rows[0]["name"]).IsEqualTo("foo");
    }

    [Test]
    public async Task AggregatePreservesResp2CollectionValues()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "FT.AGGREGATE idx *"
                ? "*2\r\n:1\r\n*2\r\n$5\r\nitems\r\n*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.AggregateAsync("idx", "*");
        var values = result.StructuredRows[0]["items"];

        await Assert.That(values.Type).IsEqualTo(RespDataType.Array);
        await Assert.That(values.Items.Select(value => value.Scalar).ToArray()).IsEquivalentTo(["foo", "bar"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregatePreservesResp3CollectionValues()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                _ => "%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%1\r\n$16\r\nextra_attributes\r\n%1\r\n$5\r\nitems\r\n*2\r\n$3\r\nfoo\r\n$3\r\nbar\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        var result = await search.AggregateAsync("idx", "*");
        var values = result.StructuredRows[0]["items"];

        await Assert.That(values.Type).IsEqualTo(RespDataType.Array);
        await Assert.That(values.Items.Select(value => value.Scalar).ToArray()).IsEquivalentTo(["foo", "bar"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateSortKeepsAliasWithSpacesInOneArgument()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                _ => "+OK\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.AggregateAsync("idx", "*", new() { SortBy = ["@my field DESC"] });

        var arguments = server.ReceivedArguments[^1].Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(arguments[^4..]).IsEquivalentTo(["SORTBY", "2", "@my field", "DESC"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateStagesPreserveCallerOrder()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : "+OK\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.AggregateAsync("idx", "*", new()
        {
            Stages = [new RespireSearchAggregateApply("@price * 2", "doubled"), new RespireSearchAggregateFilter("@doubled > 10")],
        });

        var arguments = server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(arguments[^9..]).IsEquivalentTo(
            ["FT.AGGREGATE", "idx", "*", "APPLY", "@price * 2", "AS", "doubled", "FILTER", "@doubled > 10"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task NamedParametersRequireExplicitCompatibleDialect()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : "*1\r\n:0\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.SearchAsync("idx", new("@name:$name", new()
        {
            Parameters = new Dictionary<string, RespireValue> { ["name"] = "value" },
        }))).Throws<ArgumentOutOfRangeException>();

        await search.SearchAsync("idx", new("@name:$name", new()
        {
            Parameters = new Dictionary<string, RespireValue> { ["name"] = "value" },
            Dialect = 3,
        }));
        var arguments = server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(arguments[^2..]).IsEquivalentTo(["DIALECT", "3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExplainCliReturnsEveryPlanLine()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                "FT.EXPLAINCLI idx query" => "*2\r\n$5\r\nline1\r\n$5\r\nline2\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        var plan = await search.ExplainAsync("idx", "query", explainCli: true);

        await Assert.That(plan).IsEqualTo($"line1{Environment.NewLine}line2");
    }

    [Test]
    public async Task TagQueryEscapesQuerySyntaxCharacters()
    {
        var query = RespireSearchQueryBuilder.Tag("category", "$sale value-with punctuation");

        await Assert.That(query).IsEqualTo("@category:{\\$sale\\ value\\-with\\ punctuation}");
    }

    [Test]
    public async Task NumericRangeUsesRedisTokensForInfiniteBounds()
    {
        var query = RespireSearchQueryBuilder.NumericRange("price", double.NegativeInfinity, double.PositiveInfinity);

        await Assert.That(query).IsEqualTo("@price:[-inf +inf]");
    }

    [Test]
    public async Task SearchParsesResp2DocumentsAndSendsTypedOptions()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "FT.SEARCH idx query LIMIT 0 1"
                ? "*3\r\n:1\r\n$3\r\ndoc\r\n*2\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.SearchAsync("idx", new("query", new() { Limit = (0, 1) }));

        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
    }

    [Test]
    public async Task HybridSearchParsesResp3Fields()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                _ => "%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%2\r\n$2\r\nid\r\n$3\r\ndoc\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        var result = await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3));

        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
    }

    [Test]
    public async Task HybridSearchParsesResp2RowsAndSelectsDialectTwo()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FT.HYBRID idx", StringComparison.Ordinal)
                ? "*2\r\n:1\r\n*4\r\n$2\r\nid\r\n$3\r\ndoc\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3));

        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
        var command = server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(command[^2..]).IsEquivalentTo(["DIALECT", "2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task VectorSearchRejectsDialectOneBeforeSendingCommand()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.VectorSearchAsync(
            "idx", new("embedding", new byte[] { 1, 2 }, 3), new() { Dialect = 1 }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.SEARCH", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task VectorSearchUsesDialectTwoAndTransmitsVectorBytes()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                _ => "%2\r\n$13\r\ntotal_results\r\n:0\r\n$7\r\nresults\r\n*0\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.VectorSearchAsync("idx", new("embedding", new byte[] { 1, 2 }, 3));

        var commandIndex = server.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("FT.SEARCH", StringComparison.Ordinal));
        await Assert.That(commandIndex).IsGreaterThanOrEqualTo(0);
        var arguments = server.ReceivedArguments[commandIndex];
        var textArguments = arguments.Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(textArguments).Contains("DIALECT");
        await Assert.That(textArguments[textArguments.ToList().IndexOf("DIALECT") + 1]).IsEqualTo("2");
        await Assert.That(arguments.Any(argument => argument.AsSpan().SequenceEqual(new byte[] { 1, 2 }))).IsTrue();
    }

    [Test]
    public async Task VectorSchemaRejectsUnsupportedFlags()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : "+OK\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.CreateIndexAsync("idx", new()
        {
            Fields = [new("embedding", RespireSearchFieldType.Vector, Sortable: true, Options: ["FLAT", "6", "TYPE", "FLOAT32", "DIM", "2", "DISTANCE_METRIC", "COSINE"])],
        })).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CREATE", StringComparison.Ordinal))).IsFalse();
    }

    private static RespireOptions Options(FakeRespServer server, RespProtocol protocol = RespProtocol.Resp3) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        Protocol = protocol,
        ThreadPoolMonitoring = false,
    };
}
