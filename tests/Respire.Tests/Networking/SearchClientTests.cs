using Respire.Extensions.Search;
using TUnit.Assertions;
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
        await Assert.That(result.Total).IsEqualTo(1);
        await Assert.That(result.Rows[0]["name"]).IsEqualTo("foo");
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

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) },
        Protocol = RespProtocol.Resp3,
        ThreadPoolMonitoring = false,
    };
}
