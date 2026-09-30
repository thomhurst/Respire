using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ServerDiagnosticsParserTests
{
    [Test]
    public async Task HistogramsPreserveDuplicateCommandsCumulativeBucketsAndOwnedFutureFields()
    {
        byte[] payload = [255, 0, 128];
        var fields = RespValue.Array(Text("calls"), Number(7), Text("histogram_usec"),
            RespValue.Array(Number(1), Number(2), Number(16), Number(7)),
            Text("future"), RespValue.Array(RespValue.BulkString(payload)));
        var reply = RespValue.Array(Text("get"), fields, Text("get"), fields);
        var histograms = ServerDiagnosticsParser.Histograms(in reply);
        reply.Dispose();
        payload.AsSpan().Clear();
        await Assert.That(histograms.Length).IsEqualTo(2);
        foreach (var histogram in histograms)
        {
            await Assert.That(histogram.Command).IsEqualTo("get");
            await Assert.That(histogram.Calls).IsEqualTo(7);
            await Assert.That(histogram.Buckets).IsEquivalentTo([
                new RespireLatencyHistogramBucket(1, 2), new RespireLatencyHistogramBucket(16, 7)]);
            await Assert.That(histogram.AdditionalFields["future"][0].AsBytes()).IsEquivalentTo((byte[])[255, 0, 128]);
        }
        histograms[0].Buckets[0] = default;
        await Assert.That(histograms[1].Buckets[0]).IsEqualTo(new RespireLatencyHistogramBucket(1, 2));
    }

    [Test]
    public async Task HistoryUsesUnixSecondsAndIntegerMilliseconds()
    {
        var reply = RespValue.Array(RespValue.Array(Number(1_700_000_000), Number(123)), RespValue.Array(Number(0), Number(0)));
        var history = ServerDiagnosticsParser.History(in reply);
        reply.Dispose();
        await Assert.That(history).IsEquivalentTo([
            new RespireLatencyHistorySample(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), TimeSpan.FromMilliseconds(123)),
            new RespireLatencyHistorySample(DateTimeOffset.UnixEpoch, TimeSpan.Zero)]);
        await Assert.That(ServerDiagnosticsParser.History(RespValue.Array())).IsEmpty();
        await Assert.That(ServerDiagnosticsParser.Histograms(RespValue.Array())).IsEmpty();
    }

    [Test]
    [Arguments("outer")]
    [Arguments("row")]
    [Arguments("negative-time")]
    [Arguments("negative-latency")]
    [Arguments("string")]
    [Arguments("time-overflow")]
    [Arguments("latency-overflow")]
    public async Task MalformedHistoryFailsWithProtocolError(string shape)
    {
        using var reply = shape switch
        {
            "outer" => Text("not rows"),
            "row" => RespValue.Array(RespValue.Array(Number(1))),
            "negative-time" => RespValue.Array(RespValue.Array(Number(-1), Number(1))),
            "negative-latency" => RespValue.Array(RespValue.Array(Number(1), Number(-1))),
            "string" => RespValue.Array(RespValue.Array(Text("1"), Number(1))),
            "time-overflow" => RespValue.Array(RespValue.Array(Number(long.MaxValue), Number(1))),
            _ => RespValue.Array(RespValue.Array(Number(1), Number(long.MaxValue))),
        };
        await Assert.That(() => ServerDiagnosticsParser.History(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    [Test]
    [Arguments("outer")]
    [Arguments("odd")]
    [Arguments("missing-calls")]
    [Arguments("missing-buckets")]
    [Arguments("negative-calls")]
    [Arguments("duplicate-calls")]
    [Arguments("duplicate-extra")]
    [Arguments("string-bound")]
    [Arguments("duplicate-bound")]
    [Arguments("decreasing-count")]
    [Arguments("excess-count")]
    public async Task MalformedHistogramsFailWithProtocolError(string shape)
    {
        var fields = shape switch
        {
            "missing-calls" => RespValue.Array(Text("histogram_usec"), RespValue.Array()),
            "missing-buckets" => RespValue.Array(Text("calls"), Number(0)),
            "negative-calls" => HistogramFields(-1),
            "duplicate-calls" => RespValue.Array(Text("calls"), Number(0), Text("calls"), Number(0), Text("histogram_usec"), RespValue.Array()),
            "duplicate-extra" => RespValue.Array(Text("calls"), Number(0), Text("histogram_usec"), RespValue.Array(), Text("future"), Number(1), Text("future"), Number(2)),
            "string-bound" => HistogramFields(1, Text("1"), Number(1)),
            "duplicate-bound" => HistogramFields(2, Number(1), Number(1), Number(1), Number(2)),
            "decreasing-count" => HistogramFields(2, Number(1), Number(2), Number(2), Number(1)),
            "excess-count" => HistogramFields(1, Number(1), Number(2)),
            _ => HistogramFields(0),
        };
        using var reply = shape switch
        {
            "outer" => Number(0),
            "odd" => RespValue.Array(Text("get")),
            _ => RespValue.Array(Text("get"), fields),
        };
        await Assert.That(() => ServerDiagnosticsParser.Histograms(in reply)).ThrowsExactly<RespireProtocolException>();
    }

    private static RespValue HistogramFields(long calls, params RespValue[] buckets)
        => RespValue.Array(Text("calls"), Number(calls), Text("histogram_usec"), RespValue.Array(buckets));
    private static RespValue Number(long value) => RespValue.Integer(value);
    private static RespValue Text(string value) => RespValue.BulkString(value);
}
