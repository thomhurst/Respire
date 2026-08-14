using StackExchange.Redis;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[ClassDataSource<RedisTestContainer>(Shared = SharedType.PerTestSession)]
// Exact hit-count assertions require stable tracking connections; shared-container overload can
// legitimately cause a continuity flush and turn a local hit into a server miss.
[NotInParallel]
public class ClientSideCacheIntegrationTests(RedisTestContainer fixture)
{
    [Test]
    public async Task ExternalMutation_InvalidatesCachedValue()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        await resources.Database.StringSetAsync("cache:key", "one");

        await Assert.That(await resources.Client.GetStringAsync("cache:key")).IsEqualTo("one");
        await resources.Database.StringSetAsync("cache:key", "two");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.GetStringAsync("cache:key")).IsEqualTo("two");
        await Assert.That(resources.Client.ClientSideCache!.GetStatistics().Hits).IsEqualTo(0);
    }

    [Test]
    public async Task MissingValue_IsCachedAndInvalidatedWhenCreated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        await resources.Database.KeyDeleteAsync("cache:missing");

        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsNull();
        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsNull();
        await resources.Database.StringSetAsync("cache:missing", "created");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.GetStringAsync("cache:missing")).IsEqualTo("created");
    }

    [Test]
    public async Task ExternalHashMutation_InvalidatesCachedAggregate()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:hash:{Guid.NewGuid():N}";
        await resources.Database.HashSetAsync(key, "field", "one");

        await Assert.That((await resources.Client.Hashes.GetAllAsync(key))["field"]).IsEqualTo("one");
        await Assert.That((await resources.Client.Hashes.GetAllAsync(key))["field"]).IsEqualTo("one");
        await resources.Database.HashSetAsync(key, "field", "two");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That((await resources.Client.Hashes.GetAllAsync(key))["field"]).IsEqualTo("two");
    }

    [Test]
    public async Task ExternalMutationOfEitherKey_InvalidatesMultiKeyProjection()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var suffix = Guid.NewGuid().ToString("N");
        var first = $"cache:set:first:{suffix}";
        var second = $"cache:set:second:{suffix}";
        await resources.Database.SetAddAsync(first, ["one", "two"]);
        await resources.Database.SetAddAsync(second, "one");

        await Assert.That(await resources.Client.Sets.IntersectAsync(first, second))
            .IsEquivalentTo(["one"]);
        await Assert.That(await resources.Client.Sets.IntersectAsync(first, second))
            .IsEquivalentTo(["one"]);
        await resources.Database.SetAddAsync(second, "two");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Sets.IntersectAsync(first, second))
            .IsEquivalentTo(["one", "two"]);
    }

    [Test]
    public async Task StringAndKeyMetadataReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var prefix = $"cache:string:{Guid.NewGuid():N}:";
        var first = prefix + "first";
        var second = prefix + "second";
        await resources.Database.StringSetAsync(first, "hello world");
        await resources.Database.StringSetAsync(second, "hello redis");

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Strings.LengthAsync(first),
            async () => _ = await resources.Client.Strings.GetRangeAsync(first, 0, 4),
            async () => _ = await resources.Client.Strings.LcsLengthAsync(first, second),
            async () => _ = await resources.Client.Keys.ExistsAsync(first),
            async () => _ = await resources.Client.Keys.TypeAsync(first),
            async () => _ = await resources.Client.Server.MemoryUsageAsync(first));

        await resources.Database.StringSetAsync(first, "changed");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Strings.LengthAsync(first)).IsEqualTo(7);
    }

    [Test]
    public async Task HashReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:hash-reads:{Guid.NewGuid():N}";
        await resources.Database.HashSetAsync(key, [
            new HashEntry("first", "one"),
            new HashEntry("second", "two"),
        ]);

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Hashes.GetStringAsync(key, "first"),
            async () => _ = await resources.Client.Hashes.GetManyAsync(key, "first", "missing"),
            async () => _ = await resources.Client.Hashes.GetAllAsync(key),
            async () => _ = await resources.Client.Hashes.ExistsAsync(key, "first"),
            async () => _ = await resources.Client.Hashes.CountAsync(key),
            async () => _ = await resources.Client.Hashes.FieldsAsync(key),
            async () => _ = await resources.Client.Hashes.ValuesAsync(key));

        await resources.Database.HashSetAsync(key, "first", "changed");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Hashes.GetStringAsync(key, "first"))
            .IsEqualTo("changed");
    }

    [Test]
    public async Task ListReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:list:{Guid.NewGuid():N}";
        await resources.Database.ListRightPushAsync(key, ["one", "two", "three"]);

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Lists.CountAsync(key),
            async () => _ = await resources.Client.Lists.RangeAsync(key, 0, 1),
            async () => _ = await resources.Client.Lists.IndexAsync(key, 1));

        await resources.Database.ListSetByIndexAsync(key, 1, "changed");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Lists.IndexAsync(key, 1)).IsEqualTo("changed");
    }

    [Test]
    public async Task SetReads_AreCachedAndAllDependenciesInvalidate()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var prefix = $"cache:set-reads:{Guid.NewGuid():N}:";
        var first = prefix + "first";
        var second = prefix + "second";
        await resources.Database.SetAddAsync(first, ["one", "two"]);
        await resources.Database.SetAddAsync(second, ["two", "three"]);

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Sets.ContainsAsync(first, "one"),
            async () => _ = await resources.Client.Sets.CountAsync(first),
            async () => _ = await resources.Client.Sets.MembersAsync(first),
            async () => _ = await resources.Client.Sets.IntersectAsync(first, second),
            async () => _ = await resources.Client.Sets.UnionAsync(first, second),
            async () => _ = await resources.Client.Sets.DifferenceAsync(first, second));

        await resources.Database.SetAddAsync(first, "four");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Sets.ContainsAsync(first, "four")).IsTrue();
    }

    [Test]
    public async Task SortedSetReads_AreCachedAndAllDependenciesInvalidate()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var prefix = $"cache:zset:{Guid.NewGuid():N}:";
        var first = prefix + "first";
        var second = prefix + "second";
        await resources.Database.SortedSetAddAsync(first, [
            new StackExchange.Redis.SortedSetEntry("one", 1),
            new StackExchange.Redis.SortedSetEntry("two", 2),
        ]);
        await resources.Database.SortedSetAddAsync(second, [
            new StackExchange.Redis.SortedSetEntry("two", 2),
            new StackExchange.Redis.SortedSetEntry("three", 3),
        ]);

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.SortedSets.ScoreAsync(first, "one"),
            async () => _ = await resources.Client.SortedSets.ScoresAsync(first, "one", "missing"),
            async () => _ = await resources.Client.SortedSets.CountAsync(first),
            async () => _ = await resources.Client.SortedSets.CountByScoreAsync(first, 1, 2),
            async () => _ = await resources.Client.SortedSets.RankAsync(first, "two"),
            async () => _ = await resources.Client.SortedSets.RangeAsync(first),
            async () => _ = await resources.Client.SortedSets.RangeWithScoresAsync(first),
            async () => _ = await resources.Client.SortedSets.RangeByScoreAsync(first, 1, 2),
            async () => _ = await resources.Client.SortedSets.RangeByScoreWithScoresAsync(
                first, new RespireScoreRange(1, 2)),
            async () => _ = await resources.Client.SortedSets.IntersectAsync(first, second),
            async () => _ = await resources.Client.SortedSets.UnionAsync(first, second),
            async () => _ = await resources.Client.SortedSets.DifferenceAsync(first, second));

        await resources.Database.SortedSetAddAsync(first, "four", 4);
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.SortedSets.ScoreAsync(first, "four")).IsEqualTo(4);
    }

    [Test]
    public async Task StreamReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:stream:{Guid.NewGuid():N}";
        await resources.Database.StreamAddAsync(key, "type", "first");

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Streams.CountAsync(key),
            async () => _ = await resources.Client.Streams.RangeAsync(key),
            async () => _ = await resources.Client.Streams.RangeAsync(key, descending: true));

        await resources.Database.StreamAddAsync(key, "type", "second");
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Streams.CountAsync(key)).IsEqualTo(2);
    }

    [Test]
    public async Task BitmapReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:bitmap:{Guid.NewGuid():N}";
        await resources.Database.StringSetBitAsync(key, 4, true);

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Bitmaps.GetAsync(key, 4),
            async () => _ = await resources.Client.Bitmaps.CountAsync(key),
            async () => _ = await resources.Client.Bitmaps.CountAsync(key, 0, 7, BitIndexUnit.Bit),
            async () => _ = await resources.Client.Bitmaps.PositionAsync(key, true),
            async () => _ = await resources.Client.Bitmaps.FieldReadOnlyAsync(
                key, BitFieldOperation.Get(BitFieldEncoding.Unsigned(8), 0)));

        await resources.Database.StringSetBitAsync(key, 4, false);
        await WaitForCacheEvictionAsync(resources.Client);

        await Assert.That(await resources.Client.Bitmaps.GetAsync(key, 4)).IsFalse();
    }

    [Test]
    public async Task GeoReads_AreCachedAndInvalidated()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var key = $"cache:geo:{Guid.NewGuid():N}";
        await resources.Database.GeoAddAsync(key, -0.1276, 51.5072, "london");
        await resources.Database.GeoAddAsync(key, 2.3522, 48.8566, "paris");

        await AssertAllAreCachedAsync(
            resources.Client,
            async () => _ = await resources.Client.Geo.DistanceAsync(
                key, "london", "paris", Respire.GeoUnit.Kilometers),
            async () => _ = await resources.Client.Geo.HashAsync(key, "london", "missing"),
            async () => _ = await resources.Client.Geo.PositionAsync(key, "london", "missing"),
            async () => _ = await resources.Client.Geo.SearchAsync(
                key,
                GeoSearchOrigin.FromMember("london"),
                GeoSearchShape.Circle(400, Respire.GeoUnit.Kilometers)));

        await resources.Database.GeoAddAsync(key, -3.1883, 55.9533, "london");
        await WaitForCacheEvictionAsync(resources.Client);

        var distance = await resources.Client.Geo.DistanceAsync(
            key, "london", "paris", Respire.GeoUnit.Kilometers);
        await Assert.That(distance).IsNotNull();
        await Assert.That(distance!.Value).IsGreaterThan(800);
    }

    [Test]
    public async Task MGet_CachesEachKeyAndRefetchesOnlyInvalidatedKey()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var prefix = $"cache:mget:{Guid.NewGuid():N}:";
        var first = prefix + "first";
        var second = prefix + "second";
        var missing = prefix + "missing";
        await resources.Database.StringSetAsync(first, "one");
        await resources.Database.StringSetAsync(second, "two");

        await Assert.That(await resources.Client.Strings.GetManyAsync(first, second, missing))
            .IsEquivalentTo((string?[])["one", "two", null]);
        await Assert.That(await resources.Client.Strings.GetManyAsync(first, second, missing))
            .IsEquivalentTo((string?[])["one", "two", null]);
        await Assert.That(resources.Client.ClientSideCache!.GetStatistics().Hits).IsEqualTo(3);

        var invalidations = resources.Client.ClientSideCache.GetStatistics().Invalidations;
        await resources.Database.StringSetAsync(second, "changed");
        await WaitUntilAsync(() =>
            resources.Client.ClientSideCache.GetStatistics().Invalidations > invalidations);

        await Assert.That(await resources.Client.Strings.GetManyAsync(first, second, missing))
            .IsEquivalentTo((string?[])["one", "changed", null]);
        await Assert.That(resources.Client.ClientSideCache.GetStatistics().Hits).IsEqualTo(5);
    }

    [Test]
    public async Task BinaryKeys_PreserveExactWireIdentity()
    {
        await using var resources = await Resources.CreateAsync(fixture);
        var first = new byte[] { 0xFF, 0x00, 0x01 };
        var second = new byte[] { 0xFF, 0x00, 0x02 };
        await resources.Database.StringSetAsync(first, "first");
        await resources.Database.StringSetAsync(second, "second");

        await Assert.That(await resources.Client.GetStringAsync(first)).IsEqualTo("first");
        await Assert.That(await resources.Client.GetStringAsync(second)).IsEqualTo("second");
        await Assert.That(await resources.Client.GetStringAsync(first)).IsEqualTo("first");
    }

    [Test]
    public async Task ConnectionLoss_FlushesBeforeReconnect()
    {
        var clientName = $"respire-cache-{Guid.NewGuid():N}";
        await using var resources = await Resources.CreateAsync(fixture, clientName);
        await resources.Database.StringSetAsync("cache:reconnect", "old");
        await resources.Client.GetStringAsync("cache:reconnect");

        var clientId = await FindClientIdAsync(resources.Database, clientName);
        await resources.Database.ExecuteAsync("CLIENT", "KILL", "ID", clientId);
        await WaitUntilAsync(() =>
            resources.Client.ClientSideCache!.GetStatistics().ContinuityFlushes > 0
            && resources.Client.IsConnected);
        await resources.Database.StringSetAsync("cache:reconnect", "new");

        await Assert.That(await resources.Client.GetStringAsync("cache:reconnect"))
            .IsEqualTo("new");
    }

    [Test]
    public async Task Disposal_ReleasesResidentEntries()
    {
        var resources = await Resources.CreateAsync(fixture);
        await resources.Database.StringSetAsync("cache:dispose", "value");
        await resources.Client.GetStringAsync("cache:dispose");
        var cache = resources.Client.ClientSideCache!;

        await resources.DisposeAsync();

        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    private static async Task<long> FindClientIdAsync(IDatabase database, string clientName)
    {
        var list = (string?)await database.ExecuteAsync("CLIENT", "LIST") ?? string.Empty;
        foreach (var line in list.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!fields.Contains($"name={clientName}", StringComparer.Ordinal))
            {
                continue;
            }

            var id = fields.Single(static field =>
                field.StartsWith("id=", StringComparison.Ordinal)
                || field.StartsWith("txt:id=", StringComparison.Ordinal));
            return long.Parse(
                id.AsSpan(id.IndexOf('=') + 1),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"Redis client '{clientName}' was not found.");
    }

    private static Task WaitForCacheEvictionAsync(RespireClient client)
        => WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

    private static async Task AssertAllAreCachedAsync(
        RespireClient client,
        params Func<Task>[] reads)
    {
        var initialHits = client.ClientSideCache!.GetStatistics().Hits;
        foreach (var read in reads)
        {
            await read();
        }

        foreach (var read in reads)
        {
            await read();
        }

        await Assert.That(client.ClientSideCache.GetStatistics().Hits - initialHits)
            .IsEqualTo(reads.Length);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Resources : IAsyncDisposable
    {
        private Resources(RespireClient client, ConnectionMultiplexer multiplexer)
        {
            Client = client;
            Multiplexer = multiplexer;
            Database = multiplexer.GetDatabase();
        }

        public RespireClient Client { get; }
        public ConnectionMultiplexer Multiplexer { get; }
        public IDatabase Database { get; }

        public static async Task<Resources> CreateAsync(
            RedisTestContainer fixture,
            string? clientName = null)
        {
            var options = RespireOptions.Parse(fixture.ConnectionString) with
            {
                ClientName = clientName,
                ClientSideCache = new(),
            };
            var client = await RespireClient.ConnectAsync(options);
            var multiplexer = await ConnectionMultiplexer.ConnectAsync(
                fixture.StackExchangeConnectionString);
            return new Resources(client, multiplexer);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Multiplexer.DisposeAsync();
        }
    }
}
