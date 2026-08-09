using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class GeoCommandTests
{
    [Test]
    public async Task EveryGeoCommand_WritesExpectedFrameAndParsesReply()
    {
        await using var server = new FakeRespServer(
            ":2\r\n"u8.ToArray(),
            "$4\r\n12.5\r\n"u8.ToArray(),
            "*2\r\n$11\r\nsqdtr74hyu0\r\n$-1\r\n"u8.ToArray(),
            "*2\r\n*2\r\n$4\r\n-1.5\r\n$3\r\n2.5\r\n$-1\r\n"u8.ToArray(),
            "*1\r\n*4\r\n$4\r\ncafe\r\n$3\r\n1.5\r\n:123\r\n*2\r\n$3\r\n2.5\r\n$3\r\n3.5\r\n"u8.ToArray(),
            ":4\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(await client.Geo.AddAsync(
            "places", GeoAddCondition.NotExists, changed: true,
            new GeoEntry(1.5, 2.5, "cafe"), new GeoEntry(3.5, 4.5, "park"))).IsEqualTo(2);
        await Assert.That(await client.Geo.DistanceAsync("places", "cafe", "park", GeoUnit.Kilometers))
            .IsEqualTo(12.5);
        await Assert.That(await client.Geo.HashAsync("places", "cafe", "missing"))
            .IsEquivalentTo(new string?[] { "sqdtr74hyu0", null });
        await Assert.That(await client.Geo.PositionAsync("places", "cafe", "missing"))
            .IsEquivalentTo(new GeoPosition?[] { new(-1.5, 2.5), null });

        var options = new GeoSearchOptions
        {
            Count = 2,
            Any = true,
            IncludeDistance = true,
            IncludeHash = true,
            IncludeCoordinates = true,
        };
        var search = await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(10, GeoUnit.Kilometers), options);
        await Assert.That(search).IsEquivalentTo(new[]
        {
            new GeoSearchResult("cafe", 1.5, 123, new GeoPosition(2.5, 3.5)),
        });
        await Assert.That(await client.Geo.SearchStoreAsync(
            "nearby", "places", GeoSearchOrigin.FromCoordinates(1.5, 2.5),
            GeoSearchShape.Box(10, 20, GeoUnit.Miles),
            new GeoSearchOptions { Sort = GeoSortOrder.Descending, Count = 3 }, storeDistance: true)).IsEqualTo(4);

        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[]
        {
            "GEOADD places NX CH 1.5 2.5 cafe 3.5 4.5 park",
            "GEODIST places cafe park km",
            "GEOHASH places cafe missing",
            "GEOPOS places cafe missing",
            "GEOSEARCH places FROMMEMBER cafe BYRADIUS 10 km COUNT 2 ANY WITHDIST WITHHASH WITHCOORD",
            "GEOSEARCHSTORE nearby places FROMLONLAT 1.5 2.5 BYBOX 10 20 mi DESC COUNT 3 STOREDIST",
        });
    }

    [Test]
    public async Task GeoCommands_ValidateConflictingOptions()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(1),
            new GeoSearchOptions { Any = true })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(1),
            new GeoSearchOptions { Count = 1, Any = true, Sort = GeoSortOrder.Ascending }))
            .Throws<ArgumentException>();
        await Assert.That(async () => await client.Geo.SearchStoreAsync(
            "dest", "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(1),
            new GeoSearchOptions { IncludeDistance = true })).Throws<ArgumentException>();
        await Assert.That(async () => await client.Geo.AddAsync(
            "places", entries: [new GeoEntry(181, 0, "bad")])).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.DistanceAsync(
            "places", "a", "b", (GeoUnit)42)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromCoordinates(0, 86), GeoSearchShape.Circle(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), default))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(double.NaN)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchAsync(
            "places", GeoSearchOrigin.FromMember("cafe"), GeoSearchShape.Circle(double.PositiveInfinity)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchStoreAsync(
            "dest", "places", GeoSearchOrigin.FromMember("cafe"),
            GeoSearchShape.Box(double.NaN, 1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Geo.SearchStoreAsync(
            "dest", "places", GeoSearchOrigin.FromMember("cafe"),
            GeoSearchShape.Box(1, double.PositiveInfinity)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
