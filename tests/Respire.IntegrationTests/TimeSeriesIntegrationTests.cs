using Respire.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

[Category(TestCategories.ProtocolIndependent)]
// Redis 8 bundles RedisTimeSeries; the shared Redis 7 fixture does not.
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TimeSeriesIntegrationTests(ModernRedisTestContainer fixture)
{
    [ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.Keyed, Key = TestConstraints.ClientCacheServer)]
    public required ModernRedisTestContainer CacheServer { get; init; }

    private async Task<RespireClient> ConnectAsync(int protocol)
        => await RespireClient.ConnectAsync($"{fixture.ConnectionString}?protocol={protocol}");

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task WritesReadsAndRulesRoundTrip(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var timeSeries = new RespireTimeSeriesClient(client);
        var id = Guid.NewGuid().ToString("N");
        RespireKey source = $"{{ts:{id}}}:source";
        RespireKey compacted = $"{{ts:{id}}}:compacted";
        var labels = new Dictionary<string, string> { ["run"] = id, ["kind"] = "raw" };

        await timeSeries.CreateAsync(source, new RespireTimeSeriesOptions
        {
            RetentionMilliseconds = 0,
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Last,
            Labels = labels,
        });
        await timeSeries.CreateAsync(compacted, new RespireTimeSeriesOptions
        {
            Labels = new Dictionary<string, string> { ["run"] = id, ["kind"] = "compacted" },
        });
        // TS.GET on a series with no samples replies with an empty array in both RESP2 and RESP3.
        await Assert.That(await timeSeries.GetAsync(source)).IsNull();
        await timeSeries.CreateRuleAsync(source, compacted, RespireTimeSeriesAggregation.Sum, 10, alignTimestamp: 0);
        await timeSeries.AlterAsync(source, new RespireTimeSeriesOptions { Ignore = (0, 0.0), Labels = labels });

        await Assert.That(await timeSeries.AddAsync(source, 1, 1.5, new RespireTimeSeriesAddOptions
        {
            OnDuplicate = RespireTimeSeriesDuplicatePolicy.Sum,
            Labels = labels,
        })).IsEqualTo(1);
        await timeSeries.AddAsync(source, 1, 1.0, new RespireTimeSeriesAddOptions { OnDuplicate = RespireTimeSeriesDuplicatePolicy.Sum });
        var added = await timeSeries.MultiAddAsync([new(source, 5, 3.0), new(source, 12, 4.0)], maxBatchSize: 1);
        await Assert.That(added).IsEquivalentTo([5L, 12L]);

        var latest = await timeSeries.GetAsync(source);
        await Assert.That(latest).IsNotNull();
        var range = await timeSeries.RangeAsync(source, new(0, 12));
        await Assert.That(range.Samples).IsEquivalentTo([
            new RespireTimeSeriesSample(1, 2.5), new RespireTimeSeriesSample(5, 3.0), new RespireTimeSeriesSample(12, 4.0),
        ]);
        var reversed = await timeSeries.ReverseRangeAsync(source, new(0, 12), new RespireTimeSeriesRangeOptions
        {
            Aggregation = (RespireTimeSeriesAggregation.Max, 10),
            Align = RespireTimeSeriesTimestamp.Minimum,
            BucketTimestamp = RespireTimeSeriesBucketTimestamp.Start,
        });
        await Assert.That(reversed.Samples).IsEquivalentTo([
            new RespireTimeSeriesSample(10, 4.0), new RespireTimeSeriesSample(0, 3.0),
        ]);
        var compactedSamples = await timeSeries.RangeAsync(compacted, new(RespireTimeSeriesTimestamp.Minimum, RespireTimeSeriesTimestamp.Maximum));
        await Assert.That(compactedSamples.Samples).IsEquivalentTo([new RespireTimeSeriesSample(0, 5.5)]);

        await timeSeries.AlterAsync(source, new RespireTimeSeriesOptions { ClearLabels = true });
        var remainingLabelMatches = await timeSeries.MultiGetAsync([$"run={id}"], withLabels: true);
        await Assert.That(remainingLabelMatches.Select(series => series.Key).ToArray()).IsEquivalentTo([compacted.ToString()]);

        RespireKey createdByAdd = $"{{ts:{id}}}:created";
        await timeSeries.AddAsync(createdByAdd, 1, 1.5, new RespireTimeSeriesAddOptions
        {
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Last,
        });
        await timeSeries.AddAsync(createdByAdd, 1, 2.5);
        var createdSample = await timeSeries.GetAsync(createdByAdd);
        await Assert.That(createdSample?.Value).IsEqualTo(2.5);

        var info = await timeSeries.GetInfoAsync(source);
        await Assert.That(info.TotalSamples).IsEqualTo(3);
        await Assert.That(info.FirstTimestamp).IsEqualTo(1);
        await Assert.That(info.LastTimestamp).IsEqualTo(12);
        await Assert.That(info.DuplicatePolicy).IsEqualTo(RespireTimeSeriesDuplicatePolicy.Last);
        await Assert.That(info.Labels).IsEmpty();
        await Assert.That(info.Rules.Count).IsEqualTo(1);
        await Assert.That(info.Rules[0].DestinationKey.ToString()).IsEqualTo(compacted.ToString());
        await Assert.That(info.Rules[0].BucketDurationMilliseconds).IsEqualTo(10);
        await Assert.That(info.Rules[0].Aggregation.ToLowerInvariant()).IsEqualTo("sum");
        var compactedInfo = await timeSeries.GetInfoAsync(compacted);
        await Assert.That(compactedInfo.SourceKey?.ToString()).IsEqualTo(source.ToString());
        await Assert.That(compactedInfo.Labels["kind"]).IsEqualTo("compacted");
        await Assert.That(await timeSeries.GetRawInfoAsync(source, static info => info.Count, debug: true)).IsGreaterThan(0);
        await Assert.That(await timeSeries.GetRawInfoAsync(source, static info => info.Count)).IsGreaterThan(0);
        await timeSeries.DeleteRuleAsync(source, compacted);
        await Assert.That(await timeSeries.DeleteRangeAsync(source, new(0, 5))).IsEqualTo(2);
        await Assert.That(await timeSeries.GetAsync(compacted, latestPartialBucket: true)).IsNotNull();

        RespireKey counter = $"{{ts:{id}}}:counter";
        var incremented = await timeSeries.IncrementByAsync(counter, 2.5);
        var decremented = await timeSeries.DecrementByAsync(counter, 1.0);
        await Assert.That(decremented).IsGreaterThanOrEqualTo(incremented);
        await Assert.That((await timeSeries.GetAsync(counter))!.Value.Value).IsEqualTo(1.5);

        RespireKey timedCounter = $"{{ts:{id}}}:timed-counter";
        var incrementOptions = new RespireTimeSeriesIncrementOptions
        {
            Timestamp = 100,
            RetentionMilliseconds = 0,
            Encoding = RespireTimeSeriesEncoding.Uncompressed,
            ChunkSizeBytes = 128,
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Sum,
            Labels = new Dictionary<string, string> { ["run"] = id, ["kind"] = "counter" },
        };
        await Assert.That(await timeSeries.IncrementByAsync(timedCounter, 2, incrementOptions)).IsEqualTo(100);
        await Assert.That(await timeSeries.DecrementByAsync(timedCounter, 0.5, new RespireTimeSeriesIncrementOptions { Timestamp = 100 }))
            .IsEqualTo(100);
        await Assert.That(await timeSeries.GetAsync(timedCounter)).IsEqualTo(new RespireTimeSeriesSample(100, 1.5));
        var counterInfo = await timeSeries.GetInfoAsync(timedCounter);
        await Assert.That(counterInfo.ChunkType?.ToLowerInvariant()).IsEqualTo("uncompressed");
        await Assert.That(counterInfo.Labels["kind"]).IsEqualTo("counter");

        var missing = await Assert.That(async () => await timeSeries.MultiAddAsync([
                new(source, 20, 1.0), new($"{{ts:{id}}}:missing", 20, 1.0),
            ]))
            .Throws<RespireTimeSeriesMultiAddException>();
        await Assert.That(missing!.Timestamps[0]).IsEqualTo(20);
        await Assert.That(missing.Timestamps[1]).IsNull();
        await Assert.That(missing.Errors[1]).IsNotNull();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task LabelQueriesParseEveryProtocolShape(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var timeSeries = new RespireTimeSeriesClient(client);
        var id = Guid.NewGuid().ToString("N");
        string first = $"ts:{id}:a", second = $"ts:{id}:b";
        await timeSeries.CreateAsync(first, new RespireTimeSeriesOptions
        {
            Labels = new Dictionary<string, string> { ["run"] = id, ["room"] = "1" },
        });
        await timeSeries.CreateAsync(second, new RespireTimeSeriesOptions
        {
            Labels = new Dictionary<string, string> { ["run"] = id, ["room"] = "2" },
        });
        await timeSeries.AddAsync(first, 1, 1.5);
        await timeSeries.AddAsync(first, 2, 2.5);
        await timeSeries.AddAsync(second, 1, 4.5);

        var keys = await timeSeries.QueryIndexAsync([$"run={id}"]);
        await Assert.That(keys.Select(static key => key.ToString())).IsEquivalentTo([first, second]);

        var latest = (await timeSeries.MultiGetAsync([$"run={id}"], withLabels: true)).OrderBy(series => series.Key).ToArray();
        await Assert.That(latest.Select(series => series.Key)).IsEquivalentTo([first, second]);
        await Assert.That(latest[0].Labels["room"]).IsEqualTo("1");
        await Assert.That(latest[0].Samples).IsEquivalentTo([new RespireTimeSeriesSample(2, 2.5)]);

        var selected = (await timeSeries.MultiGetAsync([$"run={id}", "room=2"], selectedLabels: ["room", "missing"])).Single();
        await Assert.That(selected.Labels["room"]).IsEqualTo("2");
        await Assert.That(selected.Labels["missing"]).IsNull();

        var ranges = (await timeSeries.MultiRangeAsync(
                new(RespireTimeSeriesTimestamp.Minimum, RespireTimeSeriesTimestamp.Maximum),
                new RespireTimeSeriesRangeOptions
                {
                    WithLabels = true,
                    Aggregation = (RespireTimeSeriesAggregation.Avg, 10),
                    Filters = [$"run={id}"],
                }))
            .OrderBy(series => series.Key).ToArray();
        await Assert.That(ranges[0].Labels["room"]).IsEqualTo("1");
        await Assert.That(ranges[0].Samples).IsEquivalentTo([new RespireTimeSeriesSample(0, 2.0)]);
        await Assert.That(ranges[1].Samples).IsEquivalentTo([new RespireTimeSeriesSample(0, 4.5)]);

        var grouped = (await timeSeries.MultiReverseRangeAsync(
                new(RespireTimeSeriesTimestamp.Minimum, RespireTimeSeriesTimestamp.Maximum),
                new RespireTimeSeriesRangeOptions { Filters = [$"run={id}"], GroupBy = ("run", "max") }))
            .Single();
        await Assert.That(grouped.Key).IsEqualTo($"run={id}");
        await Assert.That(grouped.Samples).IsEquivalentTo([
            new RespireTimeSeriesSample(2, 2.5), new RespireTimeSeriesSample(1, 4.5),
        ]);
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task KeyPrefixedViewStoresPrefixedSeries(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var prefix = $"tenant:{Guid.NewGuid():N}:";
        var tenant = new RespireTimeSeriesClient(client.WithKeyPrefix(prefix));
        var root = new RespireTimeSeriesClient(client);

        await tenant.AddAsync("series", 1, 1.5);
        await tenant.MultiAddAsync([new("series", 2, 2.5)]);

        var stored = await root.RangeAsync($"{prefix}series", new(0, 10));
        await Assert.That(stored.Samples).IsEquivalentTo([
            new RespireTimeSeriesSample(1, 1.5), new RespireTimeSeriesSample(2, 2.5),
        ]);
        await Assert.That(async () => await tenant.QueryIndexAsync(["room=1"])).Throws<NotSupportedException>();
    }

    [Test]
    [Arguments(2)]
    [Arguments(3)]
    public async Task ServerErrorsAndCancelledWritesPreserveExistingSamples(int protocol)
    {
        await using var client = await ConnectAsync(protocol);
        var timeSeries = new RespireTimeSeriesClient(client);
        var key = $"ts:{Guid.NewGuid():N}";
        await client.SetAsync(key + ":string", "not a time series");
        await Assert.That(async () => await timeSeries.AddAsync(key + ":string", 1, 1.0))
            .Throws<RespireServerException>();
        await Assert.That(async () => await timeSeries.GetAsync(key + ":missing"))
            .Throws<RespireServerException>();

        await timeSeries.AddAsync(key, 1, 1.5);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var error = await Assert.That(async () =>
                await timeSeries.AddAsync(key + ":cancelled", 2, 2.5, cancellationToken: cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancelled.Token);
        var multiAddError = await Assert.That(async () => await timeSeries.MultiAddAsync(
                [new(key, 2, 2.5), new(key, 3, 3.5)], maxBatchSize: 1, cancellationToken: cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(multiAddError!.CancellationToken).IsEqualTo(cancelled.Token);

        await Assert.That(await client.Keys.ExistsAsync(key + ":cancelled")).IsFalse();
        await Assert.That((await timeSeries.RangeAsync(key, new(0, 10))).Samples)
            .IsEquivalentTo([new RespireTimeSeriesSample(1, 1.5)]);
    }

    // Stable tracking is needed to prove a cache hit before each mutation, as in the cache suite.
    [Test, NotInParallel(TestConstraints.ClientCacheHits)]
    public async Task LocalMutationsInvalidateTrackedReadsAndFlushCompactionDependencies()
    {
        var options = RespireOptions.Parse(CacheServer.ConnectionString) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new(),
        };
        await using var client = await RespireClient.ConnectAsync(options);
        var timeSeries = new RespireTimeSeriesClient(client);
        var prefix = $"ts:{{{Guid.NewGuid():N}}}:";
        RespireKey source = prefix + "source";
        RespireKey destination = prefix + "compacted";
        RespireKey unrelated = prefix + "unrelated";
        var cache = client.ClientSideCache!;
        await client.SetAsync(unrelated, "retained");

        // TS.CREATE must invalidate the cached missing-key result, despite NOLOOP tracking.
        await Assert.That(await client.Keys.ExistsAsync(source)).IsFalse();
        var hits = cache.GetStatistics().Hits;
        await Assert.That(await client.Keys.ExistsAsync(source)).IsFalse();
        await Assert.That(cache.GetStatistics().Hits).IsEqualTo(hits + 1);
        await timeSeries.CreateAsync(source);
        await Assert.That(await client.Keys.ExistsAsync(source)).IsTrue();

        await timeSeries.CreateAsync(destination);
        await timeSeries.CreateRuleAsync(source, destination, RespireTimeSeriesAggregation.Sum, 10);
        // Every sample mutation can affect a compaction destination absent from its arguments.
        // The documented conservative contract is a full local flush, including unrelated keys.
        Func<Task>[] mutations =
        [
            async () => { await timeSeries.AddAsync(source, 1, 1.5); },
            async () => { await timeSeries.MultiAddAsync([new(source, 11, 2.5), new(source, 21, 3.5)], maxBatchSize: 1); },
            async () => { await timeSeries.IncrementByAsync(source, 1, new() { Timestamp = 31 }); },
            async () => { await timeSeries.DecrementByAsync(source, 0.5, new() { Timestamp = 41 }); },
            async () => { await timeSeries.DeleteRangeAsync(source, new(41, 41)); },
        ];
        foreach (var mutate in mutations)
        {
            await Assert.That(await client.GetStringAsync(unrelated)).IsEqualTo("retained");
            hits = cache.GetStatistics().Hits;
            await Assert.That(await client.GetStringAsync(unrelated)).IsEqualTo("retained");
            await Assert.That(cache.GetStatistics().Hits).IsEqualTo(hits + 1);

            await mutate();

            await Assert.That(cache.Count).IsEqualTo(0);
        }

        // Samples at timestamps 11 and later close bucket 0, publishing its sum to the destination.
        var compacted = await timeSeries.RangeAsync(destination, new(0, 9));
        await Assert.That(compacted.Samples).IsEquivalentTo([new RespireTimeSeriesSample(0, 1.5)]);
    }
}
