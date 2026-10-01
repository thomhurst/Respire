using Redis.Search;
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
    private static readonly byte[] EmptyAggregate = "*1\r\n:0\r\n"u8.ToArray();
    private static readonly byte[] EmptyResp3Search = "%2\r\n$13\r\ntotal_results\r\n:0\r\n$7\r\nresults\r\n*0\r\n"u8.ToArray();

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

        var result = await search.AggregateAsync("idx", "*", new()
        {
            Stages = [RespireSearchAggregateStage.SortBy(new RespireSearchAggregateSort("@count", RespireSearchSortDirection.Descending))],
        });

        var arguments = LastArguments(server);
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
        await Assert.That(values.Items.Select(value => value.Scalar!).ToArray()).IsEquivalentTo(["foo", "bar"], CollectionOrdering.Matching);
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
        await Assert.That(values.Items.Select(value => value.Scalar!).ToArray()).IsEquivalentTo(["foo", "bar"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateSortKeepsAliasWithSpacesInOneArgumentAndSendsMax()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyAggregate,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.AggregateAsync("idx", "*", new()
        {
            Stages =
            [
                new RespireSearchAggregateSortBy(
                [
                    new("@my field", RespireSearchSortDirection.Descending),
                    new("@name"),
                ]) { Max = 5 },
            ],
        });

        await Assert.That(LastArguments(server)[3..]).IsEquivalentTo(
            ["SORTBY", "4", "@my field", "DESC", "@name", "ASC", "MAX", "5"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateStagesPreserveCallerOrder()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyAggregate,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.AggregateAsync("idx", "*", new()
        {
            Stages =
            [
                RespireSearchAggregateStage.GroupBy(["@category"], new RespireSearchReducer("SUM", ["@price"], "total")),
                RespireSearchAggregateStage.Apply("@total * 2", "doubled"),
                RespireSearchAggregateStage.Filter("@doubled > 10"),
            ],
            Dialect = 2,
        });

        await Assert.That(LastArguments(server)).IsEquivalentTo(
            [
                "FT.AGGREGATE", "idx", "*",
                "GROUPBY", "1", "@category", "REDUCE", "SUM", "1", "@price", "AS", "total",
                "APPLY", "@total * 2", "AS", "doubled",
                "FILTER", "@doubled > 10",
                "DIALECT", "2",
            ],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateSortAndLimitStagesStayBeforeGrouping()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyAggregate,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.AggregateAsync("idx", "*", new()
        {
            Stages =
            [
                RespireSearchAggregateStage.Load("@price"),
                RespireSearchAggregateStage.SortBy(new RespireSearchAggregateSort("@price", RespireSearchSortDirection.Descending)),
                RespireSearchAggregateStage.Limit(0, 5),
                RespireSearchAggregateStage.GroupBy([], new RespireSearchReducer("COUNT", [], "count")),
            ],
        });

        await Assert.That(LastArguments(server)).IsEquivalentTo(
            [
                "FT.AGGREGATE", "idx", "*", "LOAD", "1", "@price", "SORTBY", "2", "@price", "DESC",
                "LIMIT", "0", "5", "GROUPBY", "0", "REDUCE", "COUNT", "0", "AS", "count",
            ],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task AggregateWithCursorSendsCursorOptionsAndParsesPages()
    {
        foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
        {
            await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "HELLO 3" => Hello,
                    _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => protocol == RespProtocol.Resp2
                        ? "*2\r\n*2\r\n:1\r\n*2\r\n$1\r\nn\r\n$1\r\n1\r\n:42\r\n"u8.ToArray()
                        : "*2\r\n%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%2\r\n$16\r\nextra_attributes\r\n%1\r\n$1\r\nn\r\n$1\r\n1\r\n$6\r\nvalues\r\n*0\r\n:42\r\n"u8.ToArray(),
                    "FT.CURSOR READ idx 42 COUNT 2" => protocol == RespProtocol.Resp2
                        ? "*2\r\n*2\r\n:1\r\n*2\r\n$1\r\nn\r\n$1\r\n2\r\n:0\r\n"u8.ToArray()
                        : "*2\r\n%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%1\r\n$16\r\nextra_attributes\r\n%1\r\n$1\r\nn\r\n$1\r\n2\r\n:0\r\n"u8.ToArray(),
                    "FT.CURSOR DEL idx 7" => FakeRespServer.OkReply,
                    _ => null,
                },
            };
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var search = new RespireSearchClient(client);

            var first = await search.AggregateWithCursorAsync("idx", "*",
                new() { Stages = [RespireSearchAggregateStage.Load("@n")], Dialect = 2 },
                new() { Count = 1, MaxIdleMilliseconds = 5_000 });
            await Assert.That(LastArguments(server)[^9..]).IsEquivalentTo(
                ["1", "@n", "WITHCURSOR", "COUNT", "1", "MAXIDLE", "5000", "DIALECT", "2"],
                CollectionOrdering.Matching);
            await Assert.That(first.CursorId).IsEqualTo(42);
            await Assert.That(first.IsComplete).IsFalse();
            await Assert.That(first.Result.Rows[0]["n"]).IsEqualTo("1");

            var second = await search.ReadCursorAsync("idx", first.CursorId, 2);
            await Assert.That(second.IsComplete).IsTrue();
            await Assert.That(second.Result.Rows[0]["n"]).IsEqualTo("2");

            await search.DeleteCursorAsync("idx", 7);
            await Assert.That(server.ReceivedCommands).Contains("FT.CURSOR DEL idx 7");
        }
    }

    [Test]
    public async Task CursorMethodsRejectInvalidArgumentsBeforeSending()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.ReadCursorAsync("idx", 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await search.ReadCursorAsync("idx", 1, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await search.DeleteCursorAsync("idx", -1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await search.AggregateWithCursorAsync("idx", "*", cursor: new() { Count = 0 }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AggregatePagesReadsEveryPageWithoutDeletingCompletedCursor()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n*2\r\n:2\r\n*2\r\n$1\r\nn\r\n$1\r\n1\r\n:42\r\n"u8.ToArray(),
                "FT.CURSOR READ idx 42" => "*2\r\n*2\r\n:2\r\n*2\r\n$1\r\nn\r\n$1\r\n2\r\n:0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var values = new List<string?>();
        await foreach (var page in search.AggregatePagesAsync("idx", "*", cursor: new() { Count = 1 }))
        {
            values.AddRange(page.Rows.Select(row => row["n"]));
        }

        await Assert.That(values).IsEquivalentTo(["1", "2"], CollectionOrdering.Matching);
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CURSOR DEL", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AggregatePagesDeletesCursorWhenEnumerationStopsEarly()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n*2\r\n:2\r\n*2\r\n$1\r\nn\r\n$1\r\n1\r\n:42\r\n"u8.ToArray(),
                "FT.CURSOR DEL idx 42" => FakeRespServer.OkReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        await foreach (var _ in search.AggregatePagesAsync("idx", "*", cursor: new() { Count = 1 }))
        {
            break;
        }

        await Assert.That(server.ReceivedCommands).Contains("FT.CURSOR DEL idx 42");
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CURSOR READ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task AggregatePagesDeletesCursorWhenAReadFails()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n*2\r\n:2\r\n*2\r\n$1\r\nn\r\n$1\r\n1\r\n:42\r\n"u8.ToArray(),
                "FT.CURSOR READ idx 42" => "-ERR Timeout limit was reached\r\n"u8.ToArray(),
                "FT.CURSOR DEL idx 42" => FakeRespServer.OkReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);
        var pages = 0;

        var exception = await Assert.That(async () =>
            {
                await foreach (var _ in search.AggregatePagesAsync("idx", "*", cursor: new() { Count = 1 }))
                {
                    pages++;
                }
            })
            .Throws<RespireServerException>();

        await Assert.That(exception!.Message).Contains("Timeout limit");
        await Assert.That(pages).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).Contains("FT.CURSOR READ idx 42");
        await Assert.That(server.ReceivedCommands).Contains("FT.CURSOR DEL idx 42");
    }

    [Test]
    public async Task AggregateWithCursorDeletesCursorWhenTheFirstPageIsMalformed()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n+OK\r\n:42\r\n"u8.ToArray(),
                "FT.CURSOR DEL idx 42" => FakeRespServer.OkReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.AggregateWithCursorAsync("idx", "*")).Throws<InvalidOperationException>();
        await Assert.That(async () =>
            {
                await foreach (var _ in search.AggregatePagesAsync("idx", "*"))
                {
                }
            })
            .Throws<InvalidOperationException>();

        await Assert.That(server.ReceivedCommands.Count(command => command == "FT.CURSOR DEL idx 42")).IsEqualTo(2);
    }

    [Test]
    public async Task CursorPageOverloadsUseThePageIndex()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.AGGREGATE", StringComparison.Ordinal) => "*2\r\n*1\r\n:0\r\n:42\r\n"u8.ToArray(),
                "FT.CURSOR READ books 42 COUNT 5" => "*2\r\n*1\r\n:0\r\n:43\r\n"u8.ToArray(),
                "FT.CURSOR DEL books 43" => FakeRespServer.OkReply,
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var first = await search.AggregateWithCursorAsync("books", "*");
        await Assert.That(first.Index).IsEqualTo("books");
        var second = await search.ReadCursorAsync(first, 5);
        await Assert.That(second.Index).IsEqualTo("books");
        await Assert.That(second.CursorId).IsEqualTo(43);
        await search.DeleteCursorAsync(second);
        await search.DeleteCursorAsync(new RespireSearchAggregateCursorPage(second.Result, 0));

        await Assert.That(server.ReceivedCommands).Contains("FT.CURSOR READ books 42 COUNT 5");
        await Assert.That(server.ReceivedCommands.Count(command => command.StartsWith("FT.CURSOR DEL", StringComparison.Ordinal))).IsEqualTo(1);
        await Assert.That(async () => await search.ReadCursorAsync(new RespireSearchAggregateCursorPage(second.Result, 9)))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("-ERR unknown command 'FT.HYBRID', with args beginning with: 'idx' ")]
    [Arguments("-ERR proxy: command not available")]
    public async Task HybridSearchReportsUnsupportedServerAsNotSupported(string error)
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.HYBRID", StringComparison.Ordinal) => Encoding.ASCII.GetBytes(error + "\r\n"),
                "COMMAND INFO FT.HYBRID" => "*1\r\n*-1\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var exception = await Assert.That(async () => await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)))
            .Throws<NotSupportedException>();
        await Assert.That(exception!.Message).Contains("8.4.0");
        await Assert.That(exception.InnerException).IsTypeOf<RespireServerException>();
        await Assert.That(server.ReceivedCommands).Contains("COMMAND INFO FT.HYBRID");
    }

    [Test]
    public async Task HybridSearchFallsBackToErrorTextWhenCommandInfoIsDenied()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.HYBRID", StringComparison.Ordinal) => "-ERR unknown command 'FT.HYBRID', with args beginning with: 'idx' \r\n"u8.ToArray(),
                "COMMAND INFO FT.HYBRID" => "-NOPERM this user has no permissions to run the 'command|info' command\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)))
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task HybridSearchKeepsOtherServerErrorsAndProbesOnce()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                _ when command.StartsWith("FT.HYBRID", StringComparison.Ordinal) => "-ERR unknown command argument: idx\r\n"u8.ToArray(),
                "COMMAND INFO FT.HYBRID" => "*1\r\n*1\r\n$9\r\nft.hybrid\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            await Assert.That(async () => await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)))
                .Throws<RespireServerException>();
        }

        await Assert.That(server.ReceivedCommands.Count(command => command == "COMMAND INFO FT.HYBRID")).IsEqualTo(1);
    }

    [Test]
    public async Task HybridSearchDoesNotProbeForNonErrCodes()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FT.HYBRID", StringComparison.Ordinal)
                ? "-NOPERM this user has no permissions to run the 'ft.hybrid' command\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)))
            .Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("COMMAND", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task SearchQueryValidatesExpressionWhenCreated()
    {
        await Assert.That(() => new RespireSearchQuery(" ")).Throws<ArgumentException>();
        var query = new RespireSearchQuery("*");
        await Assert.That(() => query with { Expression = "" }).Throws<ArgumentException>();
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

        var exception = await Assert.That(async () => await search.SearchAsync("idx", new("@name:$name", new()
        {
            Parameters = new Dictionary<string, RespireValue> { ["name"] = "value" },
        }))).Throws<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo("Dialect");

        await search.SearchAsync("idx", new("@name:$name", new()
        {
            Parameters = new Dictionary<string, RespireValue> { ["name"] = "value" },
            Dialect = 3,
        }));
        await Assert.That(LastArguments(server)[^2..]).IsEquivalentTo(["DIALECT", "3"], CollectionOrdering.Matching);
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

        var plan = await search.ExplainAsync("idx", "query", new() { Cli = true });

        await Assert.That(plan).IsEqualTo($"line1{Environment.NewLine}line2");
    }

    [Test]
    public async Task ExplainPassesRequestedDialect()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : "$4\r\nplan\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.ExplainAsync("idx", "*=>[KNN 1 @embedding $vector]", new() { Dialect = 2 });

        await Assert.That(server.ReceivedCommands).Contains("FT.EXPLAIN idx *=>[KNN 1 @embedding $vector] DIALECT 2");
        await Assert.That(async () => await search.ExplainAsync("idx", "*", new() { Dialect = 0 }))
            .Throws<ArgumentOutOfRangeException>();
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
    public async Task NumericRangeSupportsExclusiveBoundsAndServerNumberSyntax()
    {
        await Assert.That(RespireSearchQueryBuilder.NumericRange("price", 1.5, 10.0, exclusiveMinimum: true))
            .IsEqualTo("@price:[(1.5 10]");
        await Assert.That(RespireSearchQueryBuilder.NumericRange("price", 0.0000001, 1e20, exclusiveMaximum: true))
            .IsEqualTo("@price:[1E-07 (1E20]");
        await Assert.That(RespireSearchQueryBuilder.NumericRange("id", 9_007_199_254_740_993L, long.MaxValue))
            .IsEqualTo("@id:[9007199254740993 9223372036854775807]");
        await Assert.That(() => RespireSearchQueryBuilder.NumericRange("price", 5L, 1L)).Throws<ArgumentOutOfRangeException>();
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
    public async Task SearchRejectsMalformedFieldLists()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FT.SEARCH", StringComparison.Ordinal)
                ? "*3\r\n:1\r\n$3\r\ndoc\r\n*3\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n$4\r\nlost\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.SearchAsync("idx", new("*"))).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task SearchPreservesBinaryProjectedFields()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3"
                ? Hello
                : [.. "*3\r\n:1\r\n$3\r\n"u8, 0, 255, 128, .. "\r\n*2\r\n$6\r\nvector\r\n$4\r\n"u8, 0, 255, 128, 1, .. "\r\n"u8],
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.SearchAsync("idx", new("*"));

        await Assert.That(result.Documents[0].StructuredFields["vector"].Bytes!.Value.ToArray())
            .IsEquivalentTo(new byte[] { 0, 255, 128, 1 }, CollectionOrdering.Matching);
        await Assert.That(result.Documents[0].DocumentKey).IsEqualTo(new RespireKey(new byte[] { 0, 255, 128 }));
    }

    [Test]
    public async Task HybridSearchParsesResp3Fields()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "HELLO 3" => Hello,
                _ => "%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%3\r\n$5\r\n__key\r\n$3\r\ndoc\r\n$7\r\n__score\r\n$3\r\n0.5\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n"u8.ToArray(),
            },
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        var result = await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3));

        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Score).IsEqualTo(0.5);
        await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
    }

    [Test]
    public async Task HybridSearchParsesResp2RowsAndSendsParametersWithoutDialectOption()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            // Redis 8.4 RESP2 layout: a flat key/value array with nested key/value rows.
            ReplyOverride = (_, command) => command.StartsWith("FT.HYBRID idx", StringComparison.Ordinal)
                ? "*8\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n*4\r\n$5\r\n__key\r\n$3\r\ndoc\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n$8\r\nwarnings\r\n*1\r\n$4\r\nslow\r\n$14\r\nexecution_time\r\n$3\r\n0.1\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.HybridSearchAsync("idx", new("title:$term", "embedding", new byte[] { 1, 2 }, 3)
        {
            RrfWindow = 25,
            LoadFields = ["title"],
            Parameters = new Dictionary<string, RespireValue> { ["term"] = "foo" },
            TimeoutMilliseconds = 500,
        });

        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
        await Assert.That(result.Warnings).IsEquivalentTo(["slow"], CollectionOrdering.Matching);
        var command = LastArguments(server);
        await Assert.That(command[command.ToList().IndexOf("WINDOW") + 1]).IsEqualTo("25");
        await Assert.That(command[command.ToList().IndexOf("PARAMS") + 1]).IsEqualTo("4");
        await Assert.That(command).Contains("term");
        await Assert.That(command).Contains("TIMEOUT");
        var loadIndex = command.ToList().IndexOf("LOAD");
        await Assert.That(command[loadIndex + 1]).IsEqualTo("3");
        await Assert.That(command.Skip(loadIndex + 2).Take(3)).IsEquivalentTo(["@__key", "@__score", "@title"], CollectionOrdering.Matching);
        await Assert.That(command.Contains("DIALECT", StringComparer.Ordinal)).IsFalse();
    }

    [Test]
    public async Task HybridSearchRejectsReservedVectorParameter()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)
        {
            Parameters = new Dictionary<string, RespireValue> { ["vector"] = "x" },
        })).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.HYBRID", StringComparison.Ordinal))).IsFalse();
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

        var exception = await Assert.That(async () => await search.VectorSearchAsync(
            "idx", new("embedding", new byte[] { 1, 2 }, 3), new() { Dialect = 1 }))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(exception!.ParamName).IsEqualTo("Dialect");
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.SEARCH", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task VectorSearchRejectsCallerParameterNamedVector()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await Assert.That(async () => await search.VectorSearchAsync(
            "idx", new("embedding", new byte[] { 1, 2 }, 3), new()
            {
                Parameters = new Dictionary<string, RespireValue> { ["vector"] = "caller" },
            }))
            .Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.SEARCH", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task VectorSearchUsesDialectTwoAndTransmitsVectorBytes()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyResp3Search,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.VectorSearchAsync("idx", new("embedding", new byte[] { 1, 2 }, 3));

        var arguments = server.ReceivedArguments.Last();
        var textArguments = arguments.Select(Encoding.UTF8.GetString).ToArray();
        await Assert.That(textArguments[..3]).IsEquivalentTo(
            ["FT.SEARCH", "idx", "*=>[KNN 3 @embedding $vector AS vector_score]"], CollectionOrdering.Matching);
        await Assert.That(textArguments[3..^3]).IsEquivalentTo(
            ["SORTBY", "vector_score", "ASC", "LIMIT", "0", "3", "PARAMS", "2", "vector"], CollectionOrdering.Matching);
        await Assert.That(arguments[^3].AsSpan().SequenceEqual(new byte[] { 1, 2 })).IsTrue();
        await Assert.That(textArguments[^2..]).IsEquivalentTo(["DIALECT", "2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task VectorSearchKeepsCallerLimitSortAndParameters()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyResp3Search,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.VectorSearchAsync("idx",
            new("embedding", new byte[] { 1, 2 }, 50) { Filter = "@category:{$category}" },
            new()
            {
                Limit = (10, 5),
                SortBy = ("year", RespireSearchSortDirection.Descending),
                Parameters = new Dictionary<string, RespireValue> { ["category"] = "books" },
                Dialect = 3,
            });

        var arguments = LastArguments(server);
        await Assert.That(arguments[2]).IsEqualTo("(@category:{$category})=>[KNN 50 @embedding $vector AS vector_score]");
        await Assert.That(arguments[3..9]).IsEquivalentTo(["SORTBY", "year", "DESC", "LIMIT", "10", "5"], CollectionOrdering.Matching);
        await Assert.That(arguments[9..11]).IsEquivalentTo(["PARAMS", "4"], CollectionOrdering.Matching);
        await Assert.That(arguments[^4..]).IsEquivalentTo(["category", "books", "DIALECT", "3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task VectorSearchRequestValidatesWhenCreated()
    {
        await Assert.That(() => new RespireVectorSearchRequest("embedding", new byte[] { 1 }, 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RespireVectorSearchRequest(" ", new byte[] { 1 }, 1)).Throws<ArgumentException>();
        await Assert.That(() => new RespireVectorSearchRequest("embedding", ReadOnlyMemory<byte>.Empty, 1)).Throws<ArgumentException>();
        var request = new RespireVectorSearchRequest("embedding", new byte[] { 1 }, 1);
        await Assert.That(() => request with { K = -1 }).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => request with { ScoreField = "" }).Throws<ArgumentException>();
    }

    [Test]
    public async Task VectorSearchEscapesFieldIdentifier()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : EmptyResp3Search,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.VectorSearchAsync("idx", new("embedding-v2", new byte[] { 1, 2 }, 3) { ScoreField = "distance-score" });

        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("@embedding\\-v2", StringComparison.Ordinal))).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("AS distance\\-score]", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task HybridSearchKeepsLoadedFieldsNamedLikeMetadata()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("FT.HYBRID idx", StringComparison.Ordinal)
                ? "*4\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n*8\r\n$5\r\n__key\r\n$3\r\ndoc\r\n$7\r\n__score\r\n$3\r\n0.5\r\n$2\r\nid\r\n$5\r\nother\r\n$5\r\nscore\r\n$4\r\nhigh\r\n"u8.ToArray()
                : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(server, RespProtocol.Resp2));
        var search = new RespireSearchClient(client);

        var result = await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)
        {
            LoadFields = ["id", "score"],
        });

        // The reserved names identify the document; loaded fields with metadata-like names stay fields.
        await Assert.That(result.Documents[0].Id).IsEqualTo("doc");
        await Assert.That(result.Documents[0].Score).IsEqualTo(0.5);
        await Assert.That(result.Documents[0].Fields["id"]).IsEqualTo("other");
        await Assert.That(result.Documents[0].Fields["score"]).IsEqualTo("high");
    }

    [Test]
    public async Task HybridSearchPreservesLoadedExtraAttributesField()
    {
        foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
        {
            await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "HELLO 3" => Hello,
                    _ when command.StartsWith("FT.HYBRID idx", StringComparison.Ordinal) => protocol == RespProtocol.Resp2
                        ? "*4\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n*6\r\n$5\r\n__key\r\n$3\r\ndoc\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n$16\r\nextra_attributes\r\n$6\r\ncustom\r\n"u8.ToArray()
                        : "%2\r\n$13\r\ntotal_results\r\n:1\r\n$7\r\nresults\r\n*1\r\n%3\r\n$5\r\n__key\r\n$3\r\ndoc\r\n$5\r\ntitle\r\n$3\r\nfoo\r\n$16\r\nextra_attributes\r\n$6\r\ncustom\r\n"u8.ToArray(),
                    _ => null,
                },
            };
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var search = new RespireSearchClient(client);

            var result = await search.HybridSearchAsync("idx", new("title:foo", "embedding", new byte[] { 1, 2 }, 3)
            {
                LoadFields = ["title", "extra_attributes"],
            });

            await Assert.That(result.Documents[0].Fields["title"]).IsEqualTo("foo");
            await Assert.That(result.Documents[0].Fields["extra_attributes"]).IsEqualTo("custom");
        }
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
        await Assert.That(async () => await search.CreateIndexAsync("idx", new()
        {
            Fields = [new("title", RespireSearchFieldType.Tag) { Weight = 2 }],
        })).Throws<ArgumentException>();
        await Assert.That(async () => await search.CreateIndexAsync("idx", new()
        {
            Fields =
            [
                new("embedding", RespireSearchFieldType.Vector, Options: ["FLAT"])
                {
                    Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 2, RespireSearchDistanceMetric.L2),
                },
            ],
        })).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("FT.CREATE", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task TypedSchemaOptionsComputeArgumentCounts()
    {
        await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "HELLO 3" ? Hello : "+OK\r\n"u8.ToArray(),
        };
        await using var client = await RespireClient.ConnectAsync(Options(server));
        var search = new RespireSearchClient(client);

        await search.CreateIndexAsync("idx", new()
        {
            Prefixes = ["doc:"],
            Fields =
            [
                new("title", RespireSearchFieldType.Text, Alias: "t", Sortable: true) { Weight = 2.5, NoStem = true },
                new("category", RespireSearchFieldType.Tag) { Separator = ';', CaseSensitive = true },
                new("embedding", RespireSearchFieldType.Vector)
                {
                    Vector = new(RespireSearchVectorAlgorithm.Hnsw, RespireSearchVectorType.Float32, 3, RespireSearchDistanceMetric.Cosine)
                    {
                        Attributes = new Dictionary<string, string> { ["M"] = "16" },
                    },
                },
            ],
        });

        await Assert.That(LastArguments(server)).IsEquivalentTo(
            [
                "FT.CREATE", "idx", "ON", "HASH", "PREFIX", "1", "doc:", "SCHEMA",
                "title", "AS", "t", "TEXT", "WEIGHT", "2.5", "NOSTEM", "SORTABLE",
                "category", "TAG", "SEPARATOR", ";", "CASESENSITIVE",
                "embedding", "VECTOR", "HNSW", "8", "TYPE", "FLOAT32", "DIM", "3", "DISTANCE_METRIC", "COSINE", "M", "16",
            ],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task IndexInfoParsesResp2AndResp3Replies()
    {
        foreach (var protocol in new[] { RespProtocol.Resp2, RespProtocol.Resp3 })
        {
            await using var server = new FakeRespServer(1, FakeRespServer.PongReply)
            {
                ReplyOverride = (_, command) => command switch
                {
                    "HELLO 3" => Hello,
                    "FT.INFO idx" => protocol == RespProtocol.Resp2
                        ? "*6\r\n$10\r\nindex_name\r\n$3\r\nidx\r\n$10\r\nattributes\r\n*1\r\n*9\r\n$10\r\nidentifier\r\n$5\r\ntitle\r\n$9\r\nattribute\r\n$5\r\ntitle\r\n$4\r\ntype\r\n$4\r\nTEXT\r\n$6\r\nWEIGHT\r\n$1\r\n1\r\n$8\r\nSORTABLE\r\n$8\r\nnum_docs\r\n$1\r\n5\r\n"u8.ToArray()
                        : "%3\r\n$10\r\nindex_name\r\n$3\r\nidx\r\n$10\r\nattributes\r\n*1\r\n%5\r\n$10\r\nidentifier\r\n$5\r\ntitle\r\n$9\r\nattribute\r\n$5\r\ntitle\r\n$4\r\ntype\r\n$4\r\nTEXT\r\n$6\r\nWEIGHT\r\n,1\r\n$5\r\nflags\r\n*1\r\n$8\r\nSORTABLE\r\n$8\r\nnum_docs\r\n:5\r\n"u8.ToArray(),
                    _ => null,
                },
            };
            await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
            var search = new RespireSearchClient(client);

            var info = await search.GetIndexInfoAsync("idx");

            await Assert.That(info.Name).IsEqualTo("idx");
            await Assert.That(info.DocumentCount).IsEqualTo(5);
            await Assert.That(info.Attributes.Count).IsEqualTo(1);
            var attribute = info.Attributes[0];
            await Assert.That(attribute.Identifier).IsEqualTo("title");
            await Assert.That(attribute.Type).IsEqualTo("TEXT");
            await Assert.That(attribute.Flags).IsEquivalentTo(["SORTABLE"], CollectionOrdering.Matching);
            await Assert.That(attribute.Options["WEIGHT"].Scalar).IsEqualTo("1");
            await Assert.That(info.Properties.ContainsKey("num_docs")).IsTrue();
        }
    }

    private static string[] LastArguments(FakeRespServer server)
        => server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString).ToArray();

    private static RespireOptions Options(FakeRespServer server, RespProtocol protocol = RespProtocol.Resp3) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        Protocol = protocol,
        ThreadPoolMonitoring = false,
    };
}
