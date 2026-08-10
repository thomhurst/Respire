using System.Text;
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

        (await client.Bitmaps.GetAndSetAsync("bits", 4, true)).Should().BeFalse();
        (await client.Bitmaps.GetAsync("bits", 4)).Should().BeTrue();
        (await client.Bitmaps.CountAsync("bits")).Should().Be(1);
        (await client.Bitmaps.CountAsync("bits", 4, 4, BitIndexUnit.Bit)).Should().Be(1);
        (await client.Bitmaps.PositionAsync("bits", true)).Should().Be(4);
        (await client.Bitmaps.PositionAsync("missing-bits", true)).Should().BeNull();

        await client.Bitmaps.GetAndSetAsync("left", 0, true);
        await client.Bitmaps.GetAndSetAsync("right", 1, true);
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
    public async Task CollectionScanCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var hashKey = $"scan:hash:{suffix}";
        var setKey = $"scan:set:{suffix}";
        var sortedSetKey = $"scan:zset:{suffix}";

        await client.Hashes.SetAsync(
            hashKey, ("profile:name", "Ada"), ("profile:role", "admin"), ("other", "ignored"));
        await client.Sets.AddAsync(setKey, "alpha", "beta", "alpine");
        await client.SortedSets.AddAsync(
            sortedSetKey, new SortedSetEntry("ada", 1.5), new SortedSetEntry("grace", 2.5));

        var hashEntries = new List<KeyValuePair<string, string>>();
        await foreach (var entry in client.Hashes.ScanAsync(hashKey, "profile:*", countHint: 1))
        {
            hashEntries.Add(entry);
        }

        var setMembers = new List<string>();
        await foreach (var member in client.Sets.ScanAsync(setKey, "a*", countHint: 1))
        {
            setMembers.Add(member);
        }

        var sortedEntries = new List<SortedSetEntry>();
        await foreach (var entry in client.SortedSets.ScanAsync(sortedSetKey, countHint: 1))
        {
            sortedEntries.Add(entry);
        }

        hashEntries.Should().BeEquivalentTo(new[]
        {
            new KeyValuePair<string, string>("profile:name", "Ada"),
            new KeyValuePair<string, string>("profile:role", "admin"),
        });
        setMembers.Should().BeEquivalentTo("alpha", "alpine");
        sortedEntries.Should().BeEquivalentTo(new[]
        {
            new SortedSetEntry("ada", 1.5),
            new SortedSetEntry("grace", 2.5),
        });

        await client.DeleteAsync(hashKey, setKey, sortedSetKey);
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
        results.Select(static result => Encoding.UTF8.GetString(result.Member)).Should().Equal("london", "paris");
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

    [Test]
    public async Task BlockingCatalogDescriptor_DoesNotStallMultiplexedCommands()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var pop = client.ExecuteAsync(
            RespireCommands.List.BLPOP, ["catalog:jobs", 0], cancellationToken: cancellation.Token).AsTask();
        await Task.Delay(100, cancellation.Token);
        await client.Lists.RightPushAsync("catalog:jobs", "work").AsTask().WaitAsync(cancellation.Token);
        using var result = await pop.WaitAsync(cancellation.Token);

        result.Count.Should().Be(2);
        result[0].AsString().Should().Be("catalog:jobs");
        result[1].AsString().Should().Be("work");
    }

    [Test]
    public async Task BlockingCatalogDescriptor_CancellationDiscardsDedicatedConnection()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var execute = async () =>
        {
            using var _ = await client.ExecuteAsync(
                RespireCommands.List.BLPOP, ["catalog:empty", 0], cancellationToken: cancellation.Token);
        };

        await execute.Should().ThrowAsync<OperationCanceledException>();
        (await client.PingAsync()).Should().BeLessThan(TimeSpan.FromSeconds(1));
    }
}
