using System.Text;
using Respire.Extensions.TimeSeries;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TimeSeriesClientTests
{
    private static readonly byte[] Ok = FakeRespServer.OkReply;

    private static byte[] Frame(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sent(FakeRespServer server) => string.Join(" | ", server.ReceivedCommands);

    [Test]
    public async Task Writes_PlaceTerminalLabelsLastAndKeepOptionTokens()
    {
        await using var server = new FakeRespServer(Ok, Ok, Ok, Frame(":1\r\n"), Frame(":3\r\n"), Frame("*2\r\n:1\r\n:2\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);
        var labels = new Dictionary<string, string> { ["room"] = "1" };

        await timeSeries.CreateAsync("series", new RespireTimeSeriesOptions
        {
            RetentionMilliseconds = 1000,
            Encoding = RespireTimeSeriesEncoding.Uncompressed,
            ChunkSizeBytes = 128,
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Last,
            Ignore = (5, 0.5),
            Labels = labels,
        });
        await timeSeries.AlterAsync("series", new RespireTimeSeriesOptions { Ignore = (10, 1.5), Labels = labels });
        await timeSeries.AlterAsync("series", new RespireTimeSeriesOptions { ClearLabels = true });
        var timestamp = await timeSeries.AddAsync("series", 1, 2.5, new RespireTimeSeriesAddOptions
        {
            Labels = labels,
            OnDuplicate = RespireTimeSeriesDuplicatePolicy.Sum,
            Ignore = (3, 0.25),
        });
        var createdTimestamp = await timeSeries.AddAsync("created-by-add", 1, 2.5, new RespireTimeSeriesAddOptions
        {
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Last,
        });
        var timestamps = await timeSeries.MultiAddAsync([new("series", 1, 1.5), new("other", RespireTimeSeriesTimestamp.Now, 3.5)]);

        await Assert.That(timestamp).IsEqualTo(1);
        await Assert.That(createdTimestamp).IsEqualTo(3);
        await Assert.That(timestamps).IsEquivalentTo([1L, 2L]);
        await Assert.That(Sent(server)).IsEqualTo(
            "TS.CREATE series RETENTION 1000 ENCODING UNCOMPRESSED CHUNK_SIZE 128 DUPLICATE_POLICY LAST IGNORE 5 0.5 LABELS room 1 | " +
            "TS.ALTER series IGNORE 10 1.5 LABELS room 1 | " +
            "TS.ALTER series LABELS | " +
            "TS.ADD series 1 2.5 ON_DUPLICATE SUM IGNORE 3 0.25 LABELS room 1 | " +
            "TS.ADD created-by-add 1 2.5 DUPLICATE_POLICY LAST | " +
            "TS.MADD series 1 1.5 other * 3.5");
    }

    [Test]
    public async Task ClearLabelsIsAlterOnlyAndCannotReplaceSuppliedLabels()
    {
        await using var server = new FakeRespServer(Ok);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await Assert.That(async () => await timeSeries.CreateAsync("series",
            new RespireTimeSeriesOptions { ClearLabels = true })).Throws<ArgumentException>();
        await Assert.That(async () => await timeSeries.AlterAsync("series",
            new RespireTimeSeriesOptions
            {
                ClearLabels = true,
                Labels = new Dictionary<string, string> { ["room"] = "1" },
            })).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task Alter_RejectsEncodingBeforeSending()
    {
        await using var server = new FakeRespServer(Ok);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await Assert.That(async () => await timeSeries.AlterAsync(
                "series", new RespireTimeSeriesOptions { Encoding = RespireTimeSeriesEncoding.Compressed }))
            .Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task Rules_SendAggregationTokenAlignmentAndBothKeys()
    {
        await using var server = new FakeRespServer(Ok);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await timeSeries.CreateRuleAsync("source", "destination", RespireTimeSeriesAggregation.StdP, 60_000);
        await timeSeries.CreateRuleAsync("source", "destination", RespireTimeSeriesAggregation.Twa, 60_000, alignTimestamp: 30_000);
        await timeSeries.DeleteRuleAsync("source", "destination");

        await Assert.That(Sent(server)).IsEqualTo(
            "TS.CREATERULE source destination AGGREGATION STD.P 60000 | " +
            "TS.CREATERULE source destination AGGREGATION TWA 60000 30000 | " +
            "TS.DELETERULE source destination");
    }

    [Test]
    public async Task Get_ReturnsNullForEmptySeriesAndParsesSample()
    {
        await using var server = new FakeRespServer(Frame("*0\r\n"), Frame("*2\r\n:5\r\n$3\r\n2.5\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await Assert.That(await timeSeries.GetAsync("series")).IsNull();
        await Assert.That(await timeSeries.GetAsync("series", latestPartialBucket: true))
            .IsEqualTo(new RespireTimeSeriesSample(5, 2.5));
        await Assert.That(Sent(server)).IsEqualTo("TS.GET series | TS.GET series LATEST");
    }

    [Test]
    public async Task SingleSeriesRange_SendsGrammarOrderAndRejectsMultiSeriesOptions()
    {
        await using var server = new FakeRespServer(Frame("*2\r\n*2\r\n:1\r\n$3\r\n1.5\r\n*2\r\n:2\r\n$3\r\n2.5\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var result = await timeSeries.RangeAsync("series", new(RespireTimeSeriesTimestamp.Minimum, RespireTimeSeriesTimestamp.Maximum),
            new RespireTimeSeriesRangeOptions
            {
                Latest = true,
                FilterByTimestamps = [1, 2],
                FilterByValue = (0.5, 9.5),
                Count = 10,
                Align = RespireTimeSeriesTimestamp.Minimum,
                Aggregation = (RespireTimeSeriesAggregation.Avg, 1000),
                BucketTimestamp = RespireTimeSeriesBucketTimestamp.Mid,
                Empty = true,
            });

        await Assert.That(result.Samples).IsEquivalentTo([new RespireTimeSeriesSample(1, 1.5), new RespireTimeSeriesSample(2, 2.5)]);
        await Assert.That(Sent(server)).IsEqualTo(
            "TS.RANGE series - + LATEST FILTER_BY_TS 1 2 FILTER_BY_VALUE 0.5 9.5 COUNT 10 ALIGN - AGGREGATION AVG 1000 BUCKETTIMESTAMP ~ EMPTY");

        RespireTimeSeriesRangeOptions[] rejected =
        [
            new() { WithLabels = true },
            new() { SelectedLabels = ["room"] },
            new() { Filters = ["room=1"] },
            new() { GroupBy = ("room", "max") },
            new() { Empty = true },
        ];
        foreach (var options in rejected)
        {
            await Assert.That(async () => await timeSeries.ReverseRangeAsync("series", new(0, 1), options))
                .Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task MultiGet_SendsFilterTokenAndParsesResp2()
    {
        await using var server = new FakeRespServer(Frame(
            "*2\r\n" +
            "*3\r\n$1\r\na\r\n*1\r\n*2\r\n$4\r\nroom\r\n$1\r\n1\r\n*2\r\n:7\r\n$3\r\n1.5\r\n" +
            "*3\r\n$1\r\nb\r\n*0\r\n*0\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var series = await timeSeries.MultiGetAsync(["room=1", "kind!="], withLabels: true, latestPartialBucket: true);
        await timeSeries.MultiGetAsync(["room=1"], selectedLabels: ["room", "kind"]);

        await Assert.That(series.Count).IsEqualTo(2);
        await Assert.That(series[0].Key).IsEqualTo("a");
        await Assert.That(series[0].Labels["room"]).IsEqualTo("1");
        await Assert.That(series[0].Samples).IsEquivalentTo([new RespireTimeSeriesSample(7, 1.5)]);
        await Assert.That(series[1].Samples).IsEmpty();
        await Assert.That(Sent(server)).IsEqualTo(
            "TS.MGET LATEST WITHLABELS FILTER room=1 kind!= | TS.MGET SELECTED_LABELS room kind FILTER room=1");
        await Assert.That(async () => await timeSeries.MultiGetAsync(["room=1"], withLabels: true, selectedLabels: ["room"]))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task MultiGet_ParsesResp3MapPositionally()
    {
        await using var server = new FakeRespServer(Frame(
            "%2\r\n" +
            "$1\r\na\r\n*2\r\n%1\r\n$4\r\nroom\r\n$1\r\n1\r\n*2\r\n:7\r\n,1.5\r\n" +
            "$1\r\nb\r\n*2\r\n%1\r\n$4\r\nroom\r\n_\r\n*0\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var series = await timeSeries.MultiGetAsync(["room!="], selectedLabels: ["room"]);

        await Assert.That(series.Count).IsEqualTo(2);
        await Assert.That(series[0].Labels["room"]).IsEqualTo("1");
        await Assert.That(series[0].Samples).IsEquivalentTo([new RespireTimeSeriesSample(7, 1.5)]);
        await Assert.That(series[1].Key).IsEqualTo("b");
        await Assert.That(series[1].Labels["room"]).IsNull();
        await Assert.That(series[1].Samples).IsEmpty();
    }

    [Test]
    public async Task MultiRange_SendsGrammarOrderAndParsesResp3Metadata()
    {
        await using var server = new FakeRespServer(
            Frame(
                "%1\r\n$1\r\na\r\n*3\r\n%0\r\n%1\r\n$11\r\naggregators\r\n*1\r\n$3\r\navg\r\n" +
                "*2\r\n*2\r\n:0\r\n,2.5\r\n*2\r\n:10\r\n,5.5\r\n"),
            Frame(
                "%1\r\n$6\r\nroom=1\r\n*4\r\n%0\r\n%1\r\n$8\r\nreducers\r\n*1\r\n$3\r\nmax\r\n" +
                "%1\r\n$7\r\nsources\r\n*2\r\n$1\r\na\r\n$1\r\nb\r\n*1\r\n*2\r\n:1\r\n,5.5\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var aggregated = await timeSeries.MultiRangeAsync(new(0, 100), new RespireTimeSeriesRangeOptions
        {
            SelectedLabels = ["room"],
            Count = 2,
            Aggregation = (RespireTimeSeriesAggregation.Avg, 10),
            Filters = ["room=1"],
        });
        var grouped = await timeSeries.MultiReverseRangeAsync(new(0, 100), new RespireTimeSeriesRangeOptions
        {
            WithLabels = true,
            Filters = ["room=1"],
            GroupBy = ("room", "max"),
        });

        await Assert.That(aggregated.Single().Samples)
            .IsEquivalentTo([new RespireTimeSeriesSample(0, 2.5), new RespireTimeSeriesSample(10, 5.5)]);
        await Assert.That(grouped.Single().Key).IsEqualTo("room=1");
        await Assert.That(grouped.Single().Samples).IsEquivalentTo([new RespireTimeSeriesSample(1, 5.5)]);
        await Assert.That(Sent(server)).IsEqualTo(
            "TS.MRANGE 0 100 SELECTED_LABELS room COUNT 2 AGGREGATION AVG 10 FILTER room=1 | " +
            "TS.MREVRANGE 0 100 WITHLABELS FILTER room=1 GROUPBY room REDUCE max");
        await Assert.That(async () => await timeSeries.MultiRangeAsync(new(0, 1), new RespireTimeSeriesRangeOptions()))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task KeyPrefixedView_PrefixesEverySeriesKey()
    {
        await using var server = new FakeRespServer(Ok, Frame(":1\r\n"), Frame("*2\r\n:1\r\n:2\r\n"), Ok, Ok, Frame("*0\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client.WithKeyPrefix("tenant:"));

        await timeSeries.CreateAsync("series");
        await timeSeries.AddAsync("series", 1, 1.5);
        await timeSeries.MultiAddAsync([new("series", 2, 2.5), new("other", 3, 3.5)]);
        await timeSeries.CreateRuleAsync("series", "compacted", RespireTimeSeriesAggregation.Max, 1000);
        await timeSeries.DeleteRuleAsync("series", "compacted");
        await timeSeries.RangeAsync("series", new(0, 10));

        await Assert.That(Sent(server)).IsEqualTo(
            "TS.CREATE tenant:series | TS.ADD tenant:series 1 1.5 | TS.MADD tenant:series 2 2.5 tenant:other 3 3.5 | " +
            "TS.CREATERULE tenant:series tenant:compacted AGGREGATION MAX 1000 | " +
            "TS.DELETERULE tenant:series tenant:compacted | TS.RANGE tenant:series 0 10");
    }

    // One case per label-filter command, so removing any one of them from the non-prefixable layouts fails
    // its own case. Each case checks both the typed method and the raw catalog command on a prefixed view.
    [Test]
    [Arguments("TS.MGET")]
    [Arguments("TS.MRANGE")]
    [Arguments("TS.MREVRANGE")]
    [Arguments("TS.QUERYINDEX")]
    public async Task KeyPrefixedView_RejectsLabelQuery(string command)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var prefixed = client.WithKeyPrefix("tenant:");
        var timeSeries = new RespireTimeSeriesClient(prefixed);
        var labelQuery = new RespireTimeSeriesRangeOptions { Filters = ["room=1"] };

        Func<Task> typed = command switch
        {
            "TS.MGET" => async () => await timeSeries.MultiGetAsync(["room=1"]),
            "TS.MRANGE" => async () => await timeSeries.MultiRangeAsync(new(0, 1), labelQuery),
            "TS.MREVRANGE" => async () => await timeSeries.MultiReverseRangeAsync(new(0, 1), labelQuery),
            "TS.QUERYINDEX" => async () => await timeSeries.QueryIndexAsync(["room=1"]),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
        var exception = await Assert.That(typed).Throws<NotSupportedException>();
        await Assert.That(exception!.Message).Contains("unprefixed client");

        var catalogCommand = RespireCommands.All.ToArray().Single(candidate => candidate.Name == command);
        await Assert.That(async () => await prefixed.ExecuteAsync(catalogCommand, "FILTER", "room=1"))
            .Throws<NotSupportedException>();

        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Info_ParsesResp2PairsAndRawInfoSendsDebug()
    {
        await using var server = new FakeRespServer(
            Frame(
                "*18\r\n" +
                "$12\r\ntotalSamples\r\n:2\r\n" +
                "$13\r\nretentionTime\r\n:1000\r\n" +
                "$9\r\nchunkType\r\n+compressed\r\n" +
                "$15\r\nduplicatePolicy\r\n$-1\r\n" +
                "$6\r\nlabels\r\n*1\r\n*2\r\n$4\r\nroom\r\n$1\r\n1\r\n" +
                "$9\r\nsourceKey\r\n$-1\r\n" +
                "$5\r\nrules\r\n*1\r\n*4\r\n$4\r\ndest\r\n:10\r\n$3\r\navg\r\n:5\r\n" +
                "$16\r\nignoreMaxValDiff\r\n$3\r\n0.5\r\n" +
                "$11\r\nfutureField\r\n:1\r\n"),
            Frame("*0\r\n"),
            Frame("*2\r\n$12\r\ntotalSamples\r\n:2\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var info = await timeSeries.GetInfoAsync("series");
        await timeSeries.GetRawInfoAsync("series", static result => result.Count, debug: true);
        var rawCount = await timeSeries.GetRawInfoAsync("series", static result => result.Count);

        await Assert.That(info.TotalSamples).IsEqualTo(2);
        await Assert.That(info.RetentionMilliseconds).IsEqualTo(1000);
        await Assert.That(info.ChunkType).IsEqualTo("compressed");
        await Assert.That(info.DuplicatePolicy).IsNull();
        await Assert.That(info.Labels["room"]).IsEqualTo("1");
        await Assert.That(info.SourceKey).IsNull();
        await Assert.That(info.Rules.Select(static rule => (rule.DestinationKey.ToString(), rule.BucketDurationMilliseconds, rule.Aggregation, rule.AlignTimestamp)))
            .IsEquivalentTo([("dest", 10L, "avg", 5L)]);
        await Assert.That(info.IgnoreMaxValueDifference).IsEqualTo(0.5);
        await Assert.That(rawCount).IsGreaterThan(0);
        await Assert.That(Sent(server)).IsEqualTo("TS.INFO series | TS.INFO series DEBUG | TS.INFO series");
    }

    [Test]
    public async Task Info_ParsesResp3Map()
    {
        await using var server = new FakeRespServer(Frame(
            "%6\r\n" +
            "$12\r\ntotalSamples\r\n:3\r\n" +
            "$15\r\nduplicatePolicy\r\n+last\r\n" +
            "$6\r\nlabels\r\n%1\r\n$4\r\nroom\r\n$1\r\n2\r\n" +
            "$9\r\nsourceKey\r\n$3\r\nraw\r\n" +
            "$5\r\nrules\r\n%1\r\n$4\r\ndest\r\n*3\r\n:60\r\n$5\r\nstd.p\r\n:0\r\n" +
            "$16\r\nignoreMaxValDiff\r\n,1.5\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var info = await timeSeries.GetInfoAsync("series");

        await Assert.That(info.TotalSamples).IsEqualTo(3);
        await Assert.That(info.DuplicatePolicy).IsEqualTo(RespireTimeSeriesDuplicatePolicy.Last);
        await Assert.That(info.Labels["room"]).IsEqualTo("2");
        await Assert.That(info.SourceKey?.ToString()).IsEqualTo("raw");
        await Assert.That(info.Rules.Select(static rule => (rule.DestinationKey.ToString(), rule.BucketDurationMilliseconds, rule.Aggregation, rule.AlignTimestamp)))
            .IsEquivalentTo([("dest", 60L, "std.p", 0L)]);
        await Assert.That(info.IgnoreMaxValueDifference).IsEqualTo(1.5);
    }

    [Test]
    public async Task MultiAdd_ReportsEveryRejectedSampleAndKeepsAcceptedTimestamps()
    {
        await using var server = new FakeRespServer(
            Frame("*3\r\n:1\r\n-ERR TSDB: the key does not exist\r\n:3\r\n"),
            Frame("*1\r\n:1\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var exception = await Assert.That(async () => await timeSeries.MultiAddAsync(
                [new("a", 1, 1.0), new("missing", 2, 2.0), new("c", 3, 3.0)]))
            .Throws<RespireTimeSeriesMultiAddException>();

        await Assert.That(exception!.Timestamps).IsEquivalentTo([(long?)1, null, 3]);
        await Assert.That(exception.Errors[0]).IsNull();
        await Assert.That(exception.Errors[1]!).Contains("key does not exist");
        await Assert.That(exception.Message).Contains("Sample 1");

        // A reply whose length differs from the request is malformed, not silently truncated.
        await Assert.That(async () => await timeSeries.MultiAddAsync([new("a", 1, 1.0), new("b", 2, 2.0)]))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task MultiAdd_WithMaxBatchSize_SendsChunksAndReportsRejectionsAcrossChunks()
    {
        await using var server = new FakeRespServer(
            Frame("*2\r\n:1\r\n-ERR TSDB: the key does not exist\r\n"),
            Frame("*2\r\n:3\r\n:4\r\n"),
            Frame("*1\r\n:5\r\n"),
            Frame("*2\r\n:1\r\n:2\r\n"),
            Frame("*1\r\n:3\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        // A rejected sample in the first chunk does not stop the later chunks.
        var exception = await Assert.That(async () => await timeSeries.MultiAddAsync(
                [new("a", 1, 1.0), new("missing", 2, 2.0), new("c", 3, 3.0), new("d", 4, 4.0), new("e", 5, 5.0)],
                maxBatchSize: 2))
            .Throws<RespireTimeSeriesMultiAddException>();
        await Assert.That(exception!.Timestamps).IsEquivalentTo([(long?)1, null, 3, 4, 5]);
        await Assert.That(exception.Errors[1]!).Contains("key does not exist");

        var timestamps = await timeSeries.MultiAddAsync([new("a", 1, 1.0), new("b", 2, 2.0), new("c", 3, 3.0)], maxBatchSize: 2);
        await Assert.That(timestamps).IsEquivalentTo([1L, 2L, 3L]);

        // Invalid input is rejected before any chunk is sent.
        await Assert.That(async () => await timeSeries.MultiAddAsync(
                [new("a", 1, 1.0), new("b", 2, 2.0), new("c", RespireTimeSeriesTimestamp.Maximum, 3.0)], maxBatchSize: 2))
            .Throws<ArgumentException>();
        await Assert.That(async () => await timeSeries.MultiAddAsync([new("a", 1, 1.0)], maxBatchSize: 0))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(Sent(server)).IsEqualTo(
            "TS.MADD a 1 1 missing 2 2 | TS.MADD c 3 3 d 4 4 | TS.MADD e 5 5 | " +
            "TS.MADD a 1 1 b 2 2 | TS.MADD c 3 3");
    }

    [Test]
    public async Task IncrementAndDecrement_SendTimestampAndCreationOptions()
    {
        await using var server = new FakeRespServer(Frame(":5\r\n"), Frame(":6\r\n"), Frame(":7\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await timeSeries.IncrementByAsync("counter", 1.5, new RespireTimeSeriesIncrementOptions
        {
            Timestamp = 5,
            RetentionMilliseconds = 1000,
            Encoding = RespireTimeSeriesEncoding.Uncompressed,
            ChunkSizeBytes = 128,
            DuplicatePolicy = RespireTimeSeriesDuplicatePolicy.Sum,
            Ignore = (1, 0.5),
            Labels = new Dictionary<string, string> { ["room"] = "1" },
        });
        await timeSeries.DecrementByAsync("counter", 0.5, new RespireTimeSeriesIncrementOptions { Timestamp = RespireTimeSeriesTimestamp.Now });
        await timeSeries.IncrementByAsync("counter", 2);

        await Assert.That(Sent(server)).IsEqualTo(
            "TS.INCRBY counter 1.5 TIMESTAMP 5 RETENTION 1000 ENCODING UNCOMPRESSED CHUNK_SIZE 128 DUPLICATE_POLICY SUM IGNORE 1 0.5 LABELS room 1 | " +
            "TS.DECRBY counter 0.5 TIMESTAMP * | " +
            "TS.INCRBY counter 2");
    }

    [Test]
    public async Task Timestamps_AreValidatedForWritesAndRangesBeforeSending()
    {
        await using var server = new FakeRespServer(Ok);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        Func<Task>[] rejected =
        [
            async () => await timeSeries.AddAsync("series", -1, 1.0),
            async () => await timeSeries.AddAsync("series", RespireTimeSeriesTimestamp.Minimum, 1.0),
            async () => await timeSeries.AddAsync("series", new RespireTimeSeriesTimestamp("soon"), 1.0),
            async () => await timeSeries.MultiAddAsync([new("series", RespireTimeSeriesTimestamp.Maximum, 1.0)]),
            async () => await timeSeries.IncrementByAsync("series", 1, new RespireTimeSeriesIncrementOptions { Timestamp = -5 }),
            async () => await timeSeries.RangeAsync("series", new(RespireTimeSeriesTimestamp.Now, RespireTimeSeriesTimestamp.Maximum)),
            async () => await timeSeries.ReverseRangeAsync("series", new(0, -1)),
            async () => await timeSeries.DeleteRangeAsync("series", new(-1, 5)),
            async () => await timeSeries.RangeAsync("series", new(0, 1), new RespireTimeSeriesRangeOptions
            {
                Aggregation = (RespireTimeSeriesAggregation.Avg, 10),
                Align = RespireTimeSeriesTimestamp.Now,
            }),
            async () => await timeSeries.RangeAsync("series", new(0, 1), new RespireTimeSeriesRangeOptions { FilterByTimestamps = [-1] }),
        ];
        foreach (var call in rejected)
        {
            await Assert.That(call).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task CreateRule_AcceptsWholeMillisecondTimeSpans()
    {
        await using var server = new FakeRespServer(Ok);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        await timeSeries.CreateRuleAsync("source", "destination", RespireTimeSeriesAggregation.Avg, TimeSpan.FromMinutes(1));

        await Assert.That(async () => await timeSeries.CreateRuleAsync("source", "destination", RespireTimeSeriesAggregation.Avg, TimeSpan.FromTicks(1)))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await timeSeries.CreateRuleAsync("source", "destination", RespireTimeSeriesAggregation.Avg, TimeSpan.Zero))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await timeSeries.CreateRuleAsync("source", "destination", (RespireTimeSeriesAggregation)999, 1000))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(Sent(server)).IsEqualTo("TS.CREATERULE source destination AGGREGATION AVG 60000");
    }

    [Test]
    public async Task QueryIndex_SendsBareFiltersAndRejectsBlankFilters()
    {
        await using var server = new FakeRespServer(Frame("*1\r\n$1\r\na\r\n"));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);

        var keys = await timeSeries.QueryIndexAsync(["room=1", "kind!="]);

        await Assert.That(keys.Select(static key => key.ToString())).IsEquivalentTo(["a"]);
        await Assert.That(async () => await timeSeries.QueryIndexAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await timeSeries.QueryIndexAsync([" "])).Throws<ArgumentException>();
        await Assert.That(async () => await timeSeries.MultiGetAsync([])).Throws<ArgumentException>();
        await Assert.That(Sent(server)).IsEqualTo("TS.QUERYINDEX room=1 kind!=");
    }

    [Test]
    public async Task SeriesKeyIsDerivedFromKeyValue()
    {
        var series = new RespireTimeSeriesSeries("first", new Dictionary<string, string?>(), []);
        var copy = series with { KeyValue = "second" };

        await Assert.That(series.Key).IsEqualTo("first");
        await Assert.That(copy.Key).IsEqualTo("second");
    }
}
