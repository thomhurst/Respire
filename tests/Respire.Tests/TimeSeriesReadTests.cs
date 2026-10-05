using Respire.TimeSeries;
using Respire.Commands;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class TimeSeriesReadTests
{
    [Test]
    public async Task EmptyFollowRepliesAreRateLimitedAndCancellationInterruptsBackoff()
    {
        var firstReply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, _) =>
            {
                firstReply.TrySetResult();
                return "*0\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var reader = client.TimeSeries.FollowAsync("missing", 0, cancellationToken: cancel.Token).GetAsyncEnumerator();
        var next = reader.MoveNextAsync().AsTask();
        await firstReply.Task.WaitAsync(timeout.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(350, timeout.Token);
        cancel.Cancel();
        await Assert.That(async () => await next).Throws<OperationCanceledException>();
        var maximumCalls = (int)Math.Ceiling(elapsed.Elapsed.TotalMilliseconds / 100) + 2;
        await Assert.That(server.ReceivedCommands.Count).IsLessThanOrEqualTo(maximumCalls);
    }

    [Test]
    [Arguments("TS.NRANGE")]
    [Arguments("TS.NREVRANGE")]
    public async Task ExplicitRangeLayoutRoutesEveryKeyAndRejectsCrossSlot(string operation)
    {
        RespireValue[] arguments = [2, "{series}:a", "{series}:b", 0, 100];
        await Assert.That(RawCommandKeyLayouts.TryGetPrefixableLayout(operation, arguments, out var layout)).IsTrue();
        await Assert.That(layout.Start).IsEqualTo(1);
        await Assert.That(layout.Count).IsEqualTo(2);
        await Assert.That(RawCommandKeyLayouts.ValidateClusterKeys(operation, arguments).Index).IsEqualTo(1);
        await Assert.That(() => RawCommandKeyLayouts.ValidateClusterKeys(operation, [2, "{a}:one", "{b}:two", 0, 100]))
            .ThrowsExactly<RespireServerException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExplicitRangesPreserveColumnsAndPrefixOnlyKeys(bool reverse)
    {
        await using var server = new FakeRespServer("*1\r\n*2\r\n:10\r\n*3\r\n+1.5\r\n+nan\r\n+3\r\n"u8.ToArray());
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(owner.WithKeyPrefix("p:"));
        var options = new RespireTimeSeriesKeyRangeOptions
        {
            Latest = true, Count = 3, FilterByTimestamps = [10], FilterByValue = (0, 10),
            Aggregators = [[RespireTimeSeriesAggregation.Min, RespireTimeSeriesAggregation.Max], [RespireTimeSeriesAggregation.Sum]],
            BucketMilliseconds = 10, Align = 0, BucketTimestamp = RespireTimeSeriesBucketTimestamp.End, Empty = true,
        };
        var result = reverse
            ? await timeSeries.ReverseRangeKeysAsync(["a", "a"], new(0, 100), options)
            : await timeSeries.RangeKeysAsync(["a", "a"], new(0, 100), options);
        await Assert.That(result[0].Timestamp).IsEqualTo(10);
        await Assert.That(result[0].Values[0]).IsEqualTo(1.5);
        await Assert.That(double.IsNaN(result[0].Values[1])).IsTrue();
        await Assert.That(result[0].Values[2]).IsEqualTo(3);
        var verb = reverse ? "TS.NREVRANGE" : "TS.NRANGE";
        await Assert.That(server.ReceivedCommands.Single()).IsEqualTo(
            $"{verb} 2 p:a p:a 0 100 LATEST FILTER_BY_TS 10 FILTER_BY_VALUE 0 10 COUNT 3 ALIGN 0 AGGREGATION MIN,MAX SUM 10 BUCKETTIMESTAMP + EMPTY");
    }

    [Test]
    public async Task LabelQueriesHaveOptionalFiltersAndRejectPrefixedViews()
    {
        await using var server = new FakeRespServer("*1\r\n$4\r\nroom\r\n"u8.ToArray(), "*1\r\n$7\r\nkitchen\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);
        await Assert.That(await timeSeries.QueryLabelsAsync()).IsEquivalentTo(["room"]);
        await Assert.That(await timeSeries.QueryLabelValuesAsync("room", ["type=temperature"])).IsEquivalentTo(["kitchen"]);
        var prefixed = new RespireTimeSeriesClient(client.WithKeyPrefix("p:"));
        await Assert.That(async () => await prefixed.QueryLabelsAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["TS.QUERYLABELS LABELS", "TS.QUERYLABELS VALUES room FILTER type=temperature"]);
    }

    [Test]
    public async Task ReadBlocksOnDedicatedConnectionAndDoesNotTreatKeyAsOption()
    {
        await using var server = new FakeRespServer(2, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, _) => "*0\r\n"u8.ToArray(),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);
        await timeSeries.ReadAsync("BLOCK", 0);
        await timeSeries.ReadAsync("series", RespireTimeSeriesTimestamp.New,
            new RespireTimeSeriesReadOptions { BlockMilliseconds = 10, MinimumCount = 2, MaximumCount = 3 });
        await timeSeries.ReadAsync("BLOCK", 0);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "TS.READ BLOCK 0", "TS.READ series $ BLOCK 10 2 MAX_COUNT 3", "TS.READ BLOCK 0",
        ]);
        var ids = server.ReceivedConnectionIds;
        await Assert.That(ids[0]).IsEqualTo(ids[2]);
        await Assert.That(ids[1]).IsNotEqualTo(ids[0]);
    }

    [Test]
    public async Task FollowAdvancesInclusiveCursorAndStopsAtMaximumTimestamp()
    {
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "TS.READ series + MAX_COUNT 1" => "*1\r\n*2\r\n:9\r\n+0\r\n"u8.ToArray(),
                "TS.READ series 10 BLOCK 0 1 MAX_COUNT 2" => "*2\r\n*2\r\n:10\r\n+1\r\n*2\r\n:20\r\n+2\r\n"u8.ToArray(),
                _ => "*1\r\n*2\r\n:9223372036854775807\r\n+3\r\n"u8.ToArray(),
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var samples = new List<long>();
        await foreach (var sample in new RespireTimeSeriesClient(client).FollowAsync("series", RespireTimeSeriesTimestamp.New, batchSize: 2))
            samples.Add(sample.Timestamp);
        await Assert.That(samples).IsEquivalentTo([10L, 20L, long.MaxValue]);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "TS.READ series + MAX_COUNT 1", "TS.READ series 10 BLOCK 0 1 MAX_COUNT 2", "TS.READ series 21 BLOCK 0 1 MAX_COUNT 2",
        ]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewCursorIsResolvedOnceAcrossEmptyReplies(bool initiallyMissing)
    {
        var blockingReads = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) =>
            {
                if (command.Contains(" + ", StringComparison.Ordinal))
                    return initiallyMissing ? "*0\r\n"u8.ToArray() : "*1\r\n*2\r\n:10\r\n+1\r\n"u8.ToArray();
                return Interlocked.Increment(ref blockingReads) == 1 ? "*0\r\n"u8.ToArray()
                    : "*1\r\n*2\r\n:11\r\n+2\r\n"u8.ToArray();
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = client.TimeSeries.FollowAsync("series", RespireTimeSeriesTimestamp.New,
            cancellationToken: timeout.Token).GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync()).IsTrue();
        await Assert.That(reader.Current.Timestamp).IsEqualTo(11);
        var cursor = initiallyMissing ? 0 : 11;
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "TS.READ series + MAX_COUNT 1",
            $"TS.READ series {cursor} BLOCK 0 1 MAX_COUNT 256",
            $"TS.READ series {cursor} BLOCK 0 1 MAX_COUNT 256",
        ]);
    }

    [Test]
    public async Task InvalidOptionsAndCancellationSendNothing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var timeSeries = new RespireTimeSeriesClient(client);
        await Assert.That(async () => await timeSeries.RangeKeysAsync([], new(0, 10))).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await timeSeries.RangeKeysAsync(["a"], new(0, 10), new()
        {
            Aggregators = [], BucketMilliseconds = 10,
        })).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await timeSeries.ReadAsync("a", RespireTimeSeriesTimestamp.Now)).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await timeSeries.ReadAsync("a", 0, new()
        {
            BlockMilliseconds = 0, MinimumCount = 3, MaximumCount = 2,
        })).ThrowsExactly<ArgumentException>();
        await Assert.That(async () => await timeSeries.ReadAsync("a", 0, cancellationToken: new(true))).Throws<OperationCanceledException>();
        await using var iterator = timeSeries.FollowAsync("a", 0, cancellationToken: new(true)).GetAsyncEnumerator();
        await Assert.That(async () => await iterator.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task EmptyReplyLimitResetsAfterSamplesAndEndsEnumeration()
    {
        var calls = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, _) => Interlocked.Increment(ref calls) == 2
                ? "*1\r\n*2\r\n:10\r\n+1\r\n"u8.ToArray() : "*0\r\n"u8.ToArray(),
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var options = new RespireTimeSeriesFollowOptions { BatchSize = 1, MaximumConsecutiveEmptyReads = 2 };
        var samples = new List<long>();
        await foreach (var sample in client.TimeSeries.FollowAsync(options, "series", 0, timeout.Token))
            samples.Add(sample.Timestamp);
        await Assert.That(samples).IsEquivalentTo([10L]);
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task InvalidEmptyReplyLimitSendsNothing(int maximum)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var options = new RespireTimeSeriesFollowOptions { MaximumConsecutiveEmptyReads = maximum };
        await using var reader = client.TimeSeries.FollowAsync(options, "series", RespireTimeSeriesTimestamp.New).GetAsyncEnumerator();
        await Assert.That(async () => await reader.MoveNextAsync()).Throws<ArgumentOutOfRangeException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
