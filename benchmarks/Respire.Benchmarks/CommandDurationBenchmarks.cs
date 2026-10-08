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
    private string _expectedOperation = null!;

    [Params(false, true)]
    public bool DurationEnabled { get; set; }

    /// <summary>Prepares a GET case and verifies subsequent GET duration observations.</summary>
    [GlobalSetup(Targets = new[] { nameof(StringGetMiss), nameof(BytesGetMiss) })]
    public Task SetupGet() => Setup("GET");

    /// <summary>Prepares a STRLEN case and verifies subsequent STRLEN duration observations.</summary>
    [GlobalSetup(Target = nameof(StringLength))]
    public Task SetupLength() => Setup("STRLEN");

    private async Task Setup(string expectedOperation)
    {
        _expectedOperation = expectedOperation;
        _previous = RespireMetrics.Configuration;
        RespireMetrics.Configure(new() { Groups = DurationEnabled ? RespireMetricGroups.Command : RespireMetricGroups.None });
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
        _listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "db.operation.name" && tag.Value is string operation && operation == _expectedOperation)
                {
                    Interlocked.Increment(ref _measurements);
                    break;
                }
        });
        _listener.Start();
        if (await StringGetMiss() is not null || await BytesGetMiss() is not null || await StringLength() != 0)
            throw new InvalidOperationException("Duration benchmark key must be absent.");
        // Setup exercises every command; only later measurements of this case count.
        Interlocked.Exchange(ref _measurements, 0);
    }

    /// <summary>Releases the client and requires this case's operation to match the listener mode.</summary>
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
