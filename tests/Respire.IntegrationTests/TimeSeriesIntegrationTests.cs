using Respire.Extensions.TimeSeries;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.IntegrationTests;

// Redis 8 bundles RedisTimeSeries; the shared Redis 7 fixture does not.
[ClassDataSource<ModernRedisTestContainer>(Shared = SharedType.PerTestSession)]
public class TimeSeriesIntegrationTests(ModernRedisTestContainer fixture)
{
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
        await timeSeries.CreateRuleAsync(source, compacted, RespireTimeSeriesAggregation.Sum, 10, alignTimestamp: 0);
        await timeSeries.AlterAsync(source, new RespireTimeSeriesOptions { Ignore = (0, 0.0), Labels = labels });

        await Assert.That(await timeSeries.AddAsync(source, 1, 1.5, new RespireTimeSeriesAddOptions
        {
            OnDuplicate = RespireTimeSeriesDuplicatePolicy.Sum,
            Labels = labels,
        })).IsEqualTo(1);
        await timeSeries.AddAsync(source, 1, 1.0, new RespireTimeSeriesAddOptions { OnDuplicate = RespireTimeSeriesDuplicatePolicy.Sum });
        var added = await timeSeries.MultiAddAsync([new(source, 5, 3.0), new(source, 12, 4.0)]);
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
        using (var rawInfo = await timeSeries.GetRawInfoAsync(source, debug: true))
            await Assert.That(rawInfo.Count).IsGreaterThan(0);
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
    public async Task KeyPrefixedViewStoresPrefixedSeries()
    {
        await using var client = await ConnectAsync(3);
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
}
