using FluentAssertions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TypedCommandIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task StreamAddOptionsAndDescendingRange_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"stream:options:{suffix}";
        var absentKey = $"stream:absent:{suffix}";

        var notCreated = await client.Streams.AddAsync(
            absentKey, new StreamAddOptions { CreateStream = false }, ("type", "ignored"));
        notCreated.Should().BeNull();
        (await client.ExistsAsync(absentKey)).Should().BeFalse();

        var firstId = await client.Streams.AddAsync(
            key, new StreamAddOptions { Id = "1-0" }, ("type", "first"));
        firstId.Should().Be((RespireStreamId)"1-0");
        await client.Streams.AddAsync(key, new StreamAddOptions { Id = "2-0" }, ("type", "second"));
        await client.Streams.AddAsync(
            key,
            new StreamAddOptions { Id = "3-0", MaxLength = 2, ApproximateTrim = false },
            ("type", "third"));

        (await client.Streams.CountAsync(key)).Should().Be(2);
        var latest = await client.Streams.RangeAsync(key, descending: true, count: 1);
        latest.Should().ContainSingle();
        latest[0].Id.ToString().Should().Be("3-0");
        latest[0].GetString("type").Should().Be("third");
    }

    [Test]
    public async Task KeyCommands_RoundTripConditionalCopyAndTypedScan()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var prefix = $"keys:{Guid.NewGuid():N}:";
        var source = prefix + "source";
        var target = prefix + "target";
        var copy = prefix + "copy";

        await client.Strings.SetAsync(source, "one");
        (await client.Keys.TypeAsync(source)).Should().Be(RespireKeyType.String);
        (await client.Keys.TryRenameAsync(source, target)).Should().BeTrue();
        await client.Strings.SetAsync(source, "two");
        (await client.Keys.TryRenameAsync(source, target)).Should().BeFalse();
        (await client.Keys.CopyAsync(target, copy)).Should().BeTrue();
        (await client.Keys.CopyAsync(source, copy)).Should().BeFalse();
        (await client.Keys.CopyAsync(source, copy, replace: true)).Should().BeTrue();
        (await client.Strings.GetStringAsync(copy)).Should().Be("two");

        var keys = new List<string>();
        await foreach (var key in client.Keys.ScanAsync(
            prefix + "*", countHint: 1, type: RespireKeyType.String))
        {
            keys.Add(key);
        }

        keys.Should().BeEquivalentTo(source, target, copy);
        await client.Keys.DeleteAsync(source, target, copy);
        (await client.Keys.TypeAsync(source)).Should().Be(RespireKeyType.None);
    }

    [Test]
    public async Task BitmapCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Bitmaps.SetAsync("bits", 4, true)).Should().BeFalse();
        (await client.Bitmaps.GetAsync("bits", 4)).Should().BeTrue();
        (await client.Bitmaps.CountAsync("bits")).Should().Be(1);
        (await client.Bitmaps.CountAsync("bits", 4, 4, BitIndexUnit.Bit)).Should().Be(1);
        (await client.Bitmaps.PositionAsync("bits", true)).Should().Be(4);
        (await client.Bitmaps.PositionAsync("missing-bits", true)).Should().BeNull();

        await client.Bitmaps.SetAsync("left", 0, true);
        await client.Bitmaps.SetAsync("right", 1, true);
        (await client.Bitmaps.OperateAsync(BitOperation.Or, "union", "left", "right")).Should().Be(1);
        (await client.Bitmaps.CountAsync("union")).Should().Be(2);

        (await client.Bitmaps.FieldAsync(
            "fields", BitFieldOperation.Set("u8", "0", 5), BitFieldOperation.Increment("u8", "0", 2)))
            .Should().Equal(0, 7);
        (await client.Bitmaps.FieldReadOnlyAsync(
            "fields", BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), 0)))
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
    public async Task SetAndSortedSetCoverageCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var setKey = $"coverage:set:{suffix}";
        var sortedSetKey = $"coverage:zset:{suffix}";

        await client.Sets.AddAsync(setKey, "one", "two", "three");
        var random = await client.Sets.RandomMembersAsync(setKey, count: -5);
        random.Should().HaveCount(5).And.OnlyContain(
            member => member == "one" || member == "two" || member == "three");
        (await client.Sets.PopAsync(setKey, count: 2)).Should().HaveCount(2);
        (await client.Sets.CountAsync(setKey)).Should().Be(1);

        await client.SortedSets.AddAsync(
            sortedSetKey,
            new SortedSetEntry("one", 1),
            new SortedSetEntry("two", 2),
            new SortedSetEntry("three", 3),
            new SortedSetEntry("four", 4));
        (await client.SortedSets.PopAsync(sortedSetKey, count: 1)).Should()
            .Equal(new SortedSetEntry("one", 1));
        (await client.SortedSets.RemoveRangeByScoreAsync(sortedSetKey, 2, 3)).Should().Be(2);
        (await client.SortedSets.RemoveRangeByRankAsync(sortedSetKey, 0, 0)).Should().Be(1);
        (await client.SortedSets.CountAsync(sortedSetKey)).Should().Be(0);

        await client.DeleteAsync(setKey, sortedSetKey);
    }

    [Test]
    public async Task GeoCommands_RoundTripAgainstRedis()
    {
        await using var client = await RespireClient.ConnectAsync(fixture.ConnectionString);

        (await client.Geo.AddAsync(
            "cities",
            new GeoEntry(-0.1276, 51.5072, "london"),
            new GeoEntry(2.3522, 48.8566, "paris"))).Should().Be(2);

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
