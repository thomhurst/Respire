using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TypedCommandIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task BitmapCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Bitmaps.SetAsync("bits", 4, true)).Should().BeFalse();
        (await client.Bitmaps.GetAsync("bits", 4)).Should().BeTrue();
        (await client.Bitmaps.CountAsync("bits")).Should().Be(1);
        (await client.Bitmaps.CountAsync("bits", 4, 4, BitIndexUnit.Bit)).Should().Be(1);
        (await client.Bitmaps.PositionAsync("bits", true)).Should().Be(4);

        await client.Bitmaps.SetAsync("left", 0, true);
        await client.Bitmaps.SetAsync("right", 1, true);
        (await client.Bitmaps.OperateAsync(BitOperation.Or, "union", "left", "right")).Should().Be(1);
        (await client.Bitmaps.CountAsync("union")).Should().Be(2);

        (await client.Bitmaps.FieldAsync(
            "fields", BitFieldOperation.Set("u8", "0", 5), BitFieldOperation.Increment("u8", "0", 2)))
            .Should().Equal(0, 7);
        (await client.Bitmaps.FieldReadOnlyAsync("fields", BitFieldOperation.Get("u8", "0")))
            .Should().Equal(7);
    }

    [Test]
    public async Task HyperLogLogCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.HyperLogLog.AddAsync("visitors:a", "ada", "grace")).Should().BeTrue();
        (await client.HyperLogLog.AddAsync("visitors:b", "grace", "linus")).Should().BeTrue();
        (await client.HyperLogLog.CountAsync("visitors:a")).Should().Be(2);
        (await client.HyperLogLog.CountAsync("visitors:a", "visitors:b")).Should().Be(3);
        await client.HyperLogLog.MergeAsync("visitors:all", "visitors:a", "visitors:b");
        (await client.HyperLogLog.CountAsync("visitors:all")).Should().Be(3);
    }

    [Test]
    public async Task GeoCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Geo.AddAsync(
            "cities", entries:
            [
                new GeoEntry(-0.1276, 51.5072, "london"),
                new GeoEntry(2.3522, 48.8566, "paris"),
            ])).Should().Be(2);

        (await client.Geo.DistanceAsync("cities", "london", "paris", GeoUnit.Kilometers))
            .Should().BeInRange(340, 350);
        (await client.Geo.HashAsync("cities", "london", "missing"))
            .Should().SatisfyRespectively(
                hash => hash.Should().NotBeNullOrEmpty(),
                hash => hash.Should().BeNull());
        (await client.Geo.PositionAsync("cities", "london", "missing"))
            .Should().SatisfyRespectively(
                position => position.Should().NotBeNull(),
                position => position.Should().BeNull());

        var results = await client.Geo.SearchAsync(
            "cities",
            GeoSearchOrigin.FromMember("london"),
            GeoSearchShape.Circle(400, GeoUnit.Kilometers),
            new GeoSearchOptions { Sort = GeoSortOrder.Ascending, IncludeDistance = true });
        results.Select(static result => result.Member).Should().Equal("london", "paris");
        results[0].Distance.Should().BeApproximately(0, 0.001);

        (await client.Geo.SearchStoreAsync(
            "nearby", "cities", GeoSearchOrigin.FromMember("london"),
            GeoSearchShape.Circle(400, GeoUnit.Kilometers))).Should().Be(2);
    }

    [Test]
    public async Task CatalogDescriptor_ExecutesAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        using var result = await client.ExecuteAsync(RespireCommands.Server.DBSIZE);

        result.AsInteger().Should().BeGreaterThanOrEqualTo(0);
    }
}
