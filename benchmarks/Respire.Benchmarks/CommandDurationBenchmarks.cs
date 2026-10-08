using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

[MemoryDiagnoser]
public class CommandDurationBenchmarks
{
    private const string Key = "respire:benchmark:duration:missing";
    private RespireClient _client = null!;
    private readonly MeterListener _listener = new();
    private RespireMetricsOptions _previous = null!;
    private long _measurements;

    [Params(false, true)]
    public bool DurationEnabled { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = RespireMetricGroups.None });
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configured) ? configured : 6379;
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Database = 7, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = { new RespireEndpoint(host, port) },
        });
        await _client.Keys.DeleteAsync(Key);
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (DurationEnabled && instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref _measurements));
        _listener.Start();
        RespireMetrics.Configure(new() { Groups = DurationEnabled ? RespireMetricGroups.Command : RespireMetricGroups.None });
        if (await StringGetMiss() is not null || await BytesGetMiss() is not null || await StringLength() != 0)
            throw new InvalidOperationException("Duration benchmark key must be absent.");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            await _client.DisposeAsync();
            if (DurationEnabled != (Interlocked.Read(ref _measurements) != 0))
                throw new InvalidOperationException("Duration listener did not match the benchmark mode.");
        }
        finally
        {
            _listener.Dispose();
            RespireMetrics.Configure(_previous);
        }
    }

    [Benchmark]
    public ValueTask<string?> StringGetMiss() => _client.GetStringAsync(Key);

    [Benchmark]
    public ValueTask<byte[]?> BytesGetMiss() => _client.GetBytesAsync(Key);

    [Benchmark]
    public ValueTask<long> StringLength() => _client.Strings.LengthAsync(Key);
}
