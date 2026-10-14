using System.Globalization;
using System.Text;
using Respire.Protocol;
using Respire.Search;
using Respire.Tests.Models;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Core;

namespace Respire.Tests;

public class TypedSearchQueryTests
{
    [Test]
    public async Task GeneratedHashAndJsonFiltersUseAliasesAndEscapedValues()
    {
        var hash = SearchHashModelSearchSchema.Query().Where(f => f.Rating > 30)
            .Where(f => RespireSearchQueryBuilder.Or(f.Category.EqualTo("a|b"), f.Title.Matches("say \"hi\"")));
        var json = SearchJsonModelSearchSchema.Query().Where(f => f.Rating > 30)
            .Where(f => RespireSearchQueryBuilder.Or(f.Category.EqualTo("a|b"), f.Title.Matches("say \"hi\"")));
        const string expected = "((@rating:[(30 +inf]) (((@category:{a\\|b}) | (@title:\"say \\\"hi\\\"\"))))";
        await Assert.That(hash.Expression.Value).IsEqualTo(expected);
        await Assert.That(json.Expression.Value).IsEqualTo(expected);
        await Assert.That(json.IndexName).IsEqualTo("generated-json-v1");
    }

    [Test]
    public async Task QueriesAreImmutableAndProjectionIsSnapshotted()
    {
        var original = SearchHashModelSearchSchema.Query();
        RespireSearchQueryField<SearchHashModelSearchSchema.QueryFields>[]? selection = null;
        var page = original.Where(f => f.Rating.Between(1, 5)).OrderByDescending(f => f.Rating).Take(3).Skip(2)
            .Select(f => selection = [f.Title, f.Category]);
        selection![0] = selection[1];
        await Assert.That(original.Expression.Value).IsEqualTo("*");
        await Assert.That(original.ToSearchQuery().Options!.Limit).IsNull();
        await Assert.That(page.ToSearchQuery().Options!.ReturnFields[0]).IsEqualTo("title");
        await Assert.That(page.ToSearchQuery().Options!.Limit!.Value).IsEqualTo((2, 3));
        await Assert.That(original.Skip(4).ToSearchQuery().Options!.Limit!.Value).IsEqualTo((4, 10));
        await Assert.That(page.Take(0).ToSearchQuery().Options!.Limit!.Value).IsEqualTo((2, 0));
    }

    [Test]
    public async Task NumericBoundsPreservePrecisionAndRejectInvalidValues()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var field = new RespireSearchField("value", RespireSearchFieldType.Numeric, "a-b");
            var integer = new RespireSearchNumericField<object, long>(field);
            var number = new RespireSearchNumericField<object, double>(field);
            var money = new RespireSearchNumericField<object, decimal>(field);
            await Assert.That(integer.EqualTo(9007199254740993).Value).IsEqualTo("@a\\-b:[9007199254740993 9007199254740993]");
            await Assert.That((integer < 42).Value).IsEqualTo("@a\\-b:[-inf (42]");
            await Assert.That((integer <= 42).Value).IsEqualTo("@a\\-b:[-inf 42]");
            await Assert.That((integer >= 42).Value).IsEqualTo("@a\\-b:[42 +inf]");
            await Assert.That(money.LessThanOrEqualTo(1.25m).Value).IsEqualTo("@a\\-b:[-inf 1.25]");
            await Assert.That(number.GreaterThanOrEqualTo(1e20).Value).IsEqualTo("@a\\-b:[1E20 +inf]");
            await Assert.That(number.LessThan(double.PositiveInfinity).Value).IsEqualTo("@a\\-b:[-inf (+inf]");
            await Assert.That(() => number.EqualTo(double.NaN)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => integer.Between(2, 1)).Throws<ArgumentOutOfRangeException>();
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Test]
    public async Task InvalidSelectorsPagingSortingAndVectorsFailBeforeExecution()
    {
        var query = SearchJsonModelSearchSchema.Query();
        await Assert.That(() => query.Where(_ => default)).Throws<ArgumentException>();
        await Assert.That(() => query.Skip(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => query.Take(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => query.Select(_ => [])).Throws<ArgumentException>();
        await Assert.That(() => query.OrderBy(f => f.Title)).Throws<InvalidOperationException>();
        await Assert.That(() => query.OrderBy(f => f.Rating, (RespireSearchSortDirection)7)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => query.ToVectorRequest(f => f.Embedding, new byte[4], 2)).Throws<ArgumentException>();
        await Assert.That(() => query.ToVectorRequest(f => f.Embedding, new byte[8], 0)).Throws<ArgumentOutOfRangeException>();
        var unindexed = new RespireSearchTagField<object>(new("x", RespireSearchFieldType.Tag, NoIndex: true));
        await Assert.That(() => unindexed.EqualTo("x")).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SearchAndAggregateMatchHandWrittenWireCommands(int protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var query = SearchJsonModelSearchSchema.Query("live-products").Where(f => f.Rating.GreaterThan(30))
            .OrderByDescending(f => f.Rating).Skip(4).Take(6).Select(f => [f.Title, f.Category]);
        await query.SearchAsync(client.Search);
        await Assert.That(Tokens(server)).IsEqualTo("FT.SEARCH live-products @rating:[(30 +inf] RETURN 2 title category SORTBY rating DESC LIMIT 4 6 DIALECT 2");
        await query.AggregateAsync(client.Search);
        await Assert.That(Tokens(server)).IsEqualTo("FT.AGGREGATE live-products @rating:[(30 +inf] LOAD 3 @title @category @rating SORTBY 2 @rating DESC LIMIT 4 6 DIALECT 2");
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task VectorQueryPreservesBinaryParameterAndHybridFilter(int protocol)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server, protocol));
        var query = SearchHashModelSearchSchema.Query().Where(f => f.Category.EqualTo("tools"));
        var vector = new byte[] { 0, 255, 0, 128, 0, 0, 0, 0 };
        await query.VectorSearchAsync(client.Search, f => f.Embedding, vector, 3);
        var arguments = server.ReceivedArguments.Last();
        await Assert.That(Encoding.UTF8.GetString(arguments[2])).IsEqualTo("(@category:{tools})=>[KNN 3 @embedding $vector AS vector_score]");
        await Assert.That(string.Join(" ", arguments.Skip(3).Take(9).Select(Encoding.UTF8.GetString)))
            .IsEqualTo("SORTBY vector_score ASC LIMIT 0 3 PARAMS 2 vector");
        await Assert.That(arguments[12]).IsEquivalentTo(vector);
        await Assert.That(SearchJsonModelSearchSchema.Query().ToVectorRequest(f => f.Embedding, vector, 3).Expression.Value)
            .IsEqualTo("*=>[KNN 3 @embedding $vector AS vector_score]");
    }

    private static FakeRespServer Server() => new(1, FakeRespServer.PongReply)
    {
        ReplyOverride = (_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            _ when command.StartsWith("FT.SEARCH ", StringComparison.Ordinal)
                || command.StartsWith("FT.AGGREGATE ", StringComparison.Ordinal) => "*1\r\n:0\r\n"u8.ToArray(),
            _ => null,
        },
    };

    private static RespireOptions Options(FakeRespServer server, int protocol) => new()
    {
        Endpoints = { new("127.0.0.1", server.Port) }, Protocol = (RespProtocol)protocol, ThreadPoolMonitoring = false,
    };

    private static string Tokens(FakeRespServer server) => string.Join(" ", server.ReceivedArguments.Last().Select(Encoding.UTF8.GetString));
}
