using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class GeoPolygonCommandTests
{
    [Test]
    public async Task PolygonCopiesVerticesAndNormalizesClosedRingsOnDirectAndBatchCommands()
    {
        RespireGeoPosition[] vertices = [new(-1.5, 50), new(0.5, 50), new(0.5, 52), new(-1.5, 50), new(-1.5, 50)];
        var polygon = RespireGeoSearchShape.Polygon(vertices);
        vertices[0] = new(100, 0);
        await using var server = new FakeRespServer("*1\r\n$4\r\ncafe\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(),
            "*1\r\n$4\r\ncafe\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("prefix:");

        var results = await view.Geo.SearchAsync("places", polygon,
            new RespireGeoSearchOptions { Count = 1, Any = true });
        await Assert.That(results[0].Member).IsEqualTo("cafe");
        await Assert.That(await view.Geo.SearchStoreAsync("dest", "places", polygon,
            new RespireGeoSearchOptions { Sort = RespireGeoSortOrder.Ascending }, storeDistance: true)).IsEqualTo(1);
        using var batch = view.CreateBatch();
        var search = batch.Geo.Search("places", polygon);
        var store = batch.Geo.SearchStore("dest", "places", polygon);
        await batch.ExecuteAsync();
        await Assert.That(search.Result[0].Member).IsEqualTo("cafe");
        await Assert.That(store.Result).IsEqualTo(1);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "GEOSEARCH prefix:places BYPOLYGON 3 -1.5 50 0.5 50 0.5 52 COUNT 1 ANY",
            "GEOSEARCHSTORE prefix:dest prefix:places BYPOLYGON 3 -1.5 50 0.5 50 0.5 52 ASC STOREDIST",
            "GEOSEARCH prefix:places BYPOLYGON 3 -1.5 50 0.5 50 0.5 52",
            "GEOSEARCHSTORE prefix:dest prefix:places BYPOLYGON 3 -1.5 50 0.5 50 0.5 52",
        });
    }

    [Test]
    public async Task PolygonRejectsInvalidVerticesOriginsAndOptionsBeforeDispatch()
    {
        Assert.Throws<ArgumentException>(() => RespireGeoSearchShape.Polygon());
        Assert.Throws<ArgumentException>(() => RespireGeoSearchShape.Polygon(new(0, 0), new(1, 1)));
        Assert.Throws<ArgumentException>(() => RespireGeoSearchShape.Polygon(new(0, 0), new(1, 1), new(0, 0)));
        Assert.Throws<ArgumentException>(() => RespireGeoSearchShape.Polygon(new(0, 0), new(1, 1), new(0, 0), new(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireGeoSearchShape.Polygon(new(181, 0), new(0, 1), new(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireGeoSearchShape.Polygon(new(0, 86), new(0, 1), new(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireGeoSearchShape.Polygon(new(double.NaN, 0), new(0, 1), new(1, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => RespireGeoSearchShape.Polygon(new(0, double.PositiveInfinity), new(0, 1), new(1, 0)));
        var polygon = RespireGeoSearchShape.Polygon(new(0, 0), new(0, 1), new(1, 0));
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Geo.SearchAsync("places",
            RespireGeoSearchOrigin.FromCoordinates(0, 0), polygon)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Geo.SearchStoreAsync("dest", "places",
            RespireGeoSearchOrigin.FromMember("cafe"), polygon)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Geo.SearchAsync("places", RespireGeoSearchShape.Circle(1)))
            .Throws<ArgumentException>();
        using var batch = client.CreateBatch();
        Assert.Throws<ArgumentException>(() => batch.Geo.Search("places", RespireGeoSearchOrigin.FromMember("cafe"), polygon));
        Assert.Throws<ArgumentException>(() => batch.Geo.Search("places", polygon, new() { Any = true }));
        Assert.Throws<ArgumentException>(() => batch.Geo.SearchStore("dest", "places", polygon, new() { IncludeDistance = true }));
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task PolygonCacheIdentityIncludesEveryVertexAndDistinguishesShapes()
    {
        var first = Command(RespireGeoSearchShape.Polygon(new(0, 0), new(0, 1), new(1, 0)));
        var equal = Command(RespireGeoSearchShape.Polygon(new(0, 0), new(0, 1), new(1, 0), new(0, 0)));
        var different = Command(RespireGeoSearchShape.Polygon(new(0, 0), new(0, 1), new(2, 0)));
        var box = Command(RespireGeoSearchShape.Box(1, 1));
        await Assert.That(first.TryGetClientCacheKey("GEOSEARCH", out var firstKey)).IsTrue();
        await Assert.That(equal.TryGetClientCacheKey("GEOSEARCH", out var equalKey)).IsTrue();
        await Assert.That(different.TryGetClientCacheKey("GEOSEARCH", out var differentKey)).IsTrue();
        await Assert.That(box.TryGetClientCacheKey("GEOSEARCH", out var boxKey)).IsTrue();
        await Assert.That(firstKey.Equals(equalKey)).IsTrue();
        await Assert.That(firstKey.Equals(differentKey)).IsFalse();
        await Assert.That(firstKey.Equals(boxKey)).IsFalse();
    }

    [Test]
    public async Task SyntaxErrorsGiveCompatibilityHintAndPreserveOtherServerErrors()
    {
        await using var server = new FakeRespServer("-ERR syntax error\r\n"u8.ToArray(),
            "-WRONGTYPE Operation against a key holding the wrong kind of value\r\n"u8.ToArray(),
            "-ERR syntax error\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var polygon = RespireGeoSearchShape.Polygon(new(0, 0), new(0, 1), new(1, 0));
        await Assert.That(async () => await client.Geo.SearchAsync("places", polygon))
            .Throws<RespireServerException>().WithMessage("ERR syntax error Check server support for the requested options; BYPOLYGON requires Valkey 9.0+.");
        await Assert.That(async () => await client.Geo.SearchStoreAsync("dest", "places", polygon))
            .Throws<RespireServerException>().WithMessage("WRONGTYPE Operation against a key holding the wrong kind of value");
        using var batch = client.CreateBatch();
        var pending = batch.Geo.SearchStore("dest", "places", polygon);
        await batch.TryExecuteAsync();
        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        var error = (RespireServerException)pending.Error!;
        await Assert.That(error.Code).IsEqualTo("ERR");
        await Assert.That(error.CommandName).IsEqualTo("GEOSEARCHSTORE");
        await Assert.That(error.Endpoint).IsNotNull();
        await Assert.That(error.Message).Contains("BYPOLYGON requires Valkey 9.0+");
    }

    private static GeoSearchCommand Command(RespireGeoSearchShape shape)
        => new(RespireCommands.Geo.GEOSEARCH.Verb, "places", default, shape, default, null, false);
}
