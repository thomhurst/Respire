using FluentAssertions;
using Respire.Search;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class SearchProfileIntegrationTests(ModernRedisTestContainer fixture)
{
    private static readonly RespireSearchExpression All = RespireSearchExpression.FromRaw("*");
    private static readonly byte[] Vector = BitConverter.GetBytes(1f).Concat(BitConverter.GetBytes(0f)).ToArray();

    [Test]
    [Arguments(2, false)]
    [Arguments(2, true)]
    [Arguments(3, false)]
    [Arguments(3, true)]
    public async Task AllProfileFormsKeepResultsAndServerTrees(int protocol, bool limited)
    {
        await using var client = await ConnectAsync(protocol);
        var search = client.Search;
        var index = "profile:" + Guid.NewGuid().ToString("N");
        await search.CreateIndexAsync(index, new()
        {
            Prefixes = [index + ":doc:"],
            Fields = [new("title", RespireSearchFieldType.Text), new("price", RespireSearchFieldType.Numeric),
                new("embedding", RespireSearchFieldType.Vector)
                { Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 2, RespireSearchDistanceMetric.L2) }],
        });
        try
        {
            for (var i = 1; i <= 2; i++)
                await client.Hashes.SetAsync(index + ":doc:" + i, ("title", "hello"), ("price", i), ("embedding", Vector));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((await search.VectorSearchAsync(index, new("embedding", Vector, 2), cancellationToken: deadline.Token)).Documents.Count != 2)
                await Task.Delay(20, deadline.Token);

            var query = new RespireSearchQuery(RespireSearchExpression.FromRaw("hello @price:[0 3]"),
                new() { WithScores = true, ReturnFields = ["title"] });
            var normalSearch = await search.SearchAsync(index, query);
            var profiledSearch = await search.ProfileSearchAsync(index, query, limited);
            profiledSearch.Result.Total.Should().Be(normalSearch.Total).And.Be(2);
            profiledSearch.Result.Documents.Select(document => document.Id).Should().BeEquivalentTo(normalSearch.Documents.Select(document => document.Id));
            profiledSearch.Result.Documents.Should().OnlyContain(document => document.Fields["title"] == "hello" && document.Score != null);
            var iterator = Descendants(profiledSearch.Profile).Single(node => node.Type == "INTERSECT");
            iterator.Children.Should().HaveCount(2);
            iterator.Children.Select(child => child.Type).Should().BeEquivalentTo("TEXT", "NUMERIC");
            iterator.Metrics["Number of reading operations"].Should().BeGreaterThan(0);
            AssertServerProfile(profiledSearch.Profile);

            var aggregate = new RespireSearchAggregateOptions
            { Stages = [RespireSearchAggregateStage.GroupBy([], new RespireSearchReducer("COUNT", [], "count"))] };
            var normalAggregate = await search.AggregateAsync(index, All, aggregate);
            var profiledAggregate = await search.ProfileAggregateAsync(index, All, aggregate, limited);
            profiledAggregate.Result.Rows.Should().HaveCount(1);
            profiledAggregate.Result.Rows[0]["count"].Should().Be(normalAggregate.Rows[0]["count"]).And.Be("2");
            AssertServerProfile(profiledAggregate.Profile);

            var hybrid = new RespireHybridSearchQuery(RespireSearchExpression.FromRaw("hello"), "embedding", Vector, 2)
            { LoadFields = ["title"] };
            var normalHybrid = await search.HybridSearchAsync(index, hybrid);
            var profiledHybrid = await search.ProfileHybridSearchAsync(index, hybrid, limited);
            profiledHybrid.Result.Documents.Select(document => document.Id).Should().BeEquivalentTo(normalHybrid.Documents.Select(document => document.Id));
            profiledHybrid.Result.Documents.Should().HaveCount(2).And.OnlyContain(document => document.Fields["title"] == "hello" && document.Score > 0);
            profiledHybrid.Profile.Children[0].Children.Select(node => node.Name).Should().Contain(["SEARCH", "VSIM"]);
            Descendants(profiledHybrid.Profile).Should().Contain(node => node.Type == "Hybrid Merger");
            AssertServerProfile(profiledHybrid.Profile);

            var noContent = await search.ProfileSearchAsync(index, new(All, new() { NoContent = true, WithScores = true }), limited);
            noContent.Result.Documents.Should().HaveCount(2).And.OnlyContain(document => document.Fields.Count == 0 && document.Score != null);
            var empty = await search.ProfileSearchAsync(index, new(RespireSearchExpression.FromRaw("absentword")), limited);
            empty.Result.Total.Should().Be(0);
            empty.Result.Documents.Should().BeEmpty();
            AssertServerProfile(empty.Profile);
            var emptyAggregate = await search.ProfileAggregateAsync(index, RespireSearchExpression.FromRaw("absentword"), limited: limited);
            emptyAggregate.Result.Rows.Should().BeEmpty();
            AssertServerProfile(emptyAggregate.Profile);
            var emptyHybrid = await search.ProfileHybridSearchAsync(index, hybrid with { Limit = 0 }, limited);
            emptyHybrid.Result.Documents.Should().BeEmpty();
            AssertServerProfile(emptyHybrid.Profile);

            // A later reply and an index mutation must not change the retained owned profile.
            var time = iterator.TimeMilliseconds;
            await client.Hashes.SetAsync(index + ":doc:1", "title", "changed");
            await search.ProfileSearchAsync(index, new(All), limited);
            iterator.TimeMilliseconds.Should().Be(time);
            iterator.Children[0].Properties.Should().NotBeEmpty();
        }
        finally
        {
            await search.DropIndexAsync(index, deleteDocuments: true);
        }
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ProfileErrorsAndPrefixesPreserveExistingPolicies(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var missing = "profile:missing:" + Guid.NewGuid().ToString("N");
        var search = client.Search;
        Func<Task> searchError = async () => await search.ProfileSearchAsync(missing, new(All));
        Func<Task> aggregateError = async () => await search.ProfileAggregateAsync(missing, All);
        Func<Task> hybridError = async () => await search.ProfileHybridSearchAsync(missing, new(All, "embedding", Vector, 2));
        await searchError.Should().ThrowAsync<RespireServerException>();
        await aggregateError.Should().ThrowAsync<RespireServerException>();
        await hybridError.Should().ThrowAsync<RespireServerException>();
        await using var tenant = client.WithKeyPrefix("tenant:");
        Func<Task> prefixedSearch = async () => await tenant.Search.ProfileSearchAsync(missing, new(All));
        Func<Task> prefixedAggregate = async () => await tenant.Search.ProfileAggregateAsync(missing, All);
        Func<Task> prefixedHybrid = async () => await tenant.Search.ProfileHybridSearchAsync(missing, new(All, "embedding", Vector, 2));
        await prefixedSearch.Should().ThrowAsync<NotSupportedException>();
        await prefixedAggregate.Should().ThrowAsync<NotSupportedException>();
        await prefixedHybrid.Should().ThrowAsync<NotSupportedException>();
    }

    [Test]
    public async Task ProfilingPreservesTheLocalCache()
    {
        // Keep tracking state independent of the shared fixture's other connections.
        await using var server = new SearchProfileCacheContainer();
        await server.InitializeAsync();
        await using var client = await RespireClient.ConnectAsync(RespireOptions.Parse(server.ConnectionString) with
        { Protocol = RespProtocol.Resp3, ClientSideCache = new() });
        var index = "profile:" + Guid.NewGuid().ToString("N");
        await client.Search.CreateIndexAsync(index, new()
        {
            Prefixes = [index + ":doc:"], Fields = [new("title", RespireSearchFieldType.Text),
                new("embedding", RespireSearchFieldType.Vector)
                { Vector = new(RespireSearchVectorAlgorithm.Flat, RespireSearchVectorType.Float32, 2, RespireSearchDistanceMetric.L2) }],
        });
        var key = index + ":cached";
        try
        {
            await client.SetAsync(key, "value");
            (await client.GetStringAsync(key)).Should().Be("value");
            var hits = client.ClientSideCache!.GetStatistics().Hits;
            await client.Search.ProfileSearchAsync(index, new(All));
            (await client.GetStringAsync(key)).Should().Be("value");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 1);
            await client.Search.ProfileAggregateAsync(index, All);
            (await client.GetStringAsync(key)).Should().Be("value");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 2);
            await client.Search.ProfileHybridSearchAsync(index, new(All, "embedding", Vector, 1));
            (await client.GetStringAsync(key)).Should().Be("value");
            client.ClientSideCache.GetStatistics().Hits.Should().Be(hits + 3);
        }
        finally
        {
            await client.Search.DropIndexAsync(index, deleteDocuments: true);
            await client.Keys.DeleteAsync(key);
        }
    }

    private ValueTask<RespireClient> ConnectAsync(int protocol) => RespireClient.ConnectAsync(
        RespireOptions.Parse(fixture.ConnectionString) with { Protocol = (RespProtocol)protocol });

    private static IEnumerable<RespireSearchProfileNode> Descendants(RespireSearchProfileNode root)
    {
        yield return root;
        foreach (var child in root.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static void AssertServerProfile(RespireSearchProfileNode profile)
    {
        profile.Properties.Should().ContainKeys("Shards", "Coordinator");
        Descendants(profile).Should().Contain(node => node.Metrics.ContainsKey("Total profile time"));
        Descendants(profile).Should().Contain(node => node.Properties.ContainsKey("Result processors profile"));
    }
}

/// <summary>Dedicated tracking fixture for Search profile cache tests.</summary>
public sealed class SearchProfileCacheContainer() : StandaloneRedisTestContainer("redis:8.10-alpine");
