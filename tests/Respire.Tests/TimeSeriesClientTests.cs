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
    public async Task KeyPrefixedView_PrefixesEverySeriesKeyAndRejectsLabelQueries()
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

        await Assert.That(async () => await timeSeries.MultiGetAsync(["room=1"])).Throws<NotSupportedException>();
        await Assert.That(async () => await timeSeries.MultiRangeAsync(new(0, 1), new RespireTimeSeriesRangeOptions { Filters = ["room=1"] }))
            .Throws<NotSupportedException>();
        await Assert.That(async () => await timeSeries.QueryIndexAsync(["room=1"])).Throws<NotSupportedException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(6);
    }
}
