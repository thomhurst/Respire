using System.Diagnostics.Metrics;

namespace Respire.Benchmarks;

/// <summary>Selects error instrumentation before connecting, outside measured operations.</summary>
internal sealed class ErrorMetricBenchmarkScope : IDisposable
{
    private readonly RespireMetricsOptions _previous = RespireMetrics.Configuration;
    private readonly MeterListener _listener = new();
    private bool _published;
    private readonly bool _enabled;

    internal ErrorMetricBenchmarkScope()
    {
        _enabled = Environment.GetEnvironmentVariable("RESPIRE_BENCH_ERRORS") == "enabled";
        RespireMetrics.Configure(new() { Groups = _enabled ? RespireMetricGroups.Resiliency : RespireMetricGroups.None });
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name != "Respire" || instrument.Name != "redis.client.errors") return;
            _published = true;
            if (_enabled) listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
        _listener.Start();
    }

    internal async Task WarmAsync(RespireClient client, RespireKey nonIntegerKey)
    {
        try
        {
            await client.Strings.IncrementAsync(nonIntegerKey);
            throw new InvalidOperationException("The metric warm-up must receive Redis ERR.");
        }
        catch (RespireServerException error) when (error.Code == "ERR") { }
        // The pinned pre-feature baseline has no errors instrument. Keep that absence visible;
        // it supplies the same successful traffic under the same requested metric configuration.
        Console.WriteLine($"ERROR_METRICS requested={_enabled}; published={_published}");
    }

    public void Dispose()
    {
        _listener.Dispose();
        RespireMetrics.Configure(_previous);
    }
}
