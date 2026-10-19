using FluentAssertions;
using Testcontainers.Redis;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
[ParallelLimiter<DockerHeavy>]
public class GeoPolygonIntegrationTests
{
    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task Valkey9PolygonSearchAndStoreWorkDirectBatchedAndTransactional(int protocol)
    {
        await using var container = await ContainerStartRetry.StartAsync(
            () => new RedisBuilder("valkey/valkey:9.0-alpine").Build(), [6379]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Protocol = (RespireProtocol)protocol, Connections = 1,
        }, token);
        var geo = client.WithKeyPrefix("prefix:");
        await geo.Geo.AddAsync("{places}:source", [new(1, 0.5, "inside"), new(3, 3, "outside")], token);
        // Both open and closed rings are accepted; the repeated endpoint is not sent.
        var open = RespireGeoSearchShape.Polygon(new(0, 0), new(2, 0), new(2, 2), new(0, 2));
        var closed = RespireGeoSearchShape.Polygon(new(0, 0), new(2, 0), new(2, 2), new(0, 2), new(0, 0));
        var details = new RespireGeoSearchOptions
        {
            Sort = RespireGeoSortOrder.Ascending, Count = 1,
            IncludeDistance = true, IncludeHash = true, IncludeCoordinates = true,
        };
        var expected = await geo.Geo.SearchAsync("{places}:source", open, details, token);
        expected.Should().ContainSingle();
        expected[0].Member.Should().Be("inside");
        expected[0].Distance.Should().BeInRange(55_000, 56_000); // Meters, about half a degree from the center.
        expected[0].Hash.Should().BeGreaterThan(0);
        expected[0].Position!.Value.Longitude.Should().BeApproximately(1, 0.00001);
        expected[0].Position!.Value.Latitude.Should().BeApproximately(0.5, 0.00001);
        (await geo.Geo.SearchStoreAsync("{places}:direct", "{places}:source", closed,
            storeDistance: true, cancellationToken: token)).Should().Be(1);
        (await geo.SortedSets.ScoreAsync("{places}:direct", "inside", token))
            .Should().BeApproximately(expected[0].Distance!.Value, 0.001);

        using (var batch = geo.CreateBatch())
        {
            var search = batch.Geo.Search("{places}:source", closed, details);
            var store = batch.Geo.SearchStore("{places}:batch", "{places}:source", open);
            await batch.ExecuteAsync(token);
            search.Result.Should().Equal(expected);
            store.Result.Should().Be(1);
        }
        await using (var transaction = geo.CreateTransaction())
        {
            var search = transaction.Geo.Search("{places}:source", closed, details);
            var store = transaction.Geo.SearchStore("{places}:transaction", "{places}:source", open);
            await transaction.CommitAsync(token);
            search.Result.Should().Equal(expected);
            store.Result.Should().Be(1);
        }
        foreach (var key in new[] { "{places}:batch", "{places}:transaction" })
        {
            var stored = await geo.Geo.SearchAsync(key, open, cancellationToken: token);
            stored.Select(result => result.Member).Should().Equal("inside");
        }
        // ANY uses the same polygon frame without sorting or details.
        (await geo.Geo.SearchAsync("{places}:source", open, new() { Count = 1, Any = true }, token))
            .Select(result => result.Member).Should().Equal("inside");
    }

    [Test]
    [Arguments("valkey/valkey:8.0-alpine", 2)]
    [Arguments("valkey/valkey:8.0-alpine", 3)]
    [Arguments("redis:7.2-alpine", 2)]
    [Arguments("redis:7.2-alpine", 3)]
    public async Task OlderServersReportPolygonRequirementWithoutMaskingOtherErrors(string image, int protocol)
    {
        await using var container = await ContainerStartRetry.StartAsync(() => new RedisBuilder(image).Build(), [6379]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(container.Hostname, container.GetMappedPublicPort(6379))],
            Protocol = (RespireProtocol)protocol, Connections = 1,
        }, token);
        await client.Geo.AddAsync("places", new RespireGeoEntry(1, 1, "inside"), token);
        var polygon = RespireGeoSearchShape.Polygon(new(0, 0), new(2, 0), new(2, 2), new(0, 2));
        var search = async () => await client.Geo.SearchAsync("places", polygon, cancellationToken: token);
        await search.Should().ThrowAsync<RespireServerException>().WithMessage("*BYPOLYGON requires Valkey 9.0+*");
        var store = async () => await client.Geo.SearchStoreAsync("dest", "places", polygon, cancellationToken: token);
        await store.Should().ThrowAsync<RespireServerException>().WithMessage("*BYPOLYGON requires Valkey 9.0+*");
        using (var batch = client.CreateBatch())
        {
            var pending = batch.Geo.Search("places", polygon);
            await batch.TryExecuteAsync(token);
            pending.Error.Should().BeOfType<RespireServerException>().Which.Message.Should().Contain("Valkey 9.0+");
        }
        await using (var transaction = client.CreateTransaction())
        {
            var pending = transaction.Geo.SearchStore("dest", "places", polygon);
            await transaction.CommitAsync(token);
            pending.Error.Should().BeOfType<RespireServerException>().Which.Message.Should().Contain("Valkey 9.0+");
        }
        // Existing shapes still work on these servers.
        (await client.Geo.SearchAsync("places", RespireGeoSearchOrigin.FromCoordinates(1, 1),
            RespireGeoSearchShape.Circle(1), cancellationToken: token)).Should().ContainSingle();
    }
}
