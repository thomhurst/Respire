using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

/// <summary>Stable primary replies at one and fifty concurrent operations, against a real cluster node.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ClusterReadyRoutingBenchmarks
{
    private readonly RespireKey _key = "cluster-ready:read";
    private readonly string _payload = new('x', 32);
    private readonly Task<string?>[] _strings = new Task<string?>[50];
    private readonly Task<byte[]?>[] _bytes = new Task<byte[]?>[50];
    private readonly Task<long>[] _integers = new Task<long>[50];
    private readonly Process _process = Process.GetCurrentProcess();
    private RespireClient _cluster = null!;
    private RespireClient _standalone = null!;
    private TimeSpan _cpuAt;
    private long _startedAt;
    private long _operations;
    private string? _operation;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configured) ? configured : 6380;
        var options = new RespireOptions
        {
            Endpoints = [new(host, port)], Protocol = RespProtocol.Resp2,
            Connections = 1, ThreadPoolMonitoring = false,
        };
        _cluster = await RespireClient.ConnectAsync(options with { UseCluster = true }).ConfigureAwait(false);
        _standalone = await RespireClient.ConnectAsync(options).ConfigureAwait(false);
        if (RespireTelemetry.IsOperationEnabled("GET") || RespireTelemetry.IsOperationEnabled("STRLEN"))
            throw new InvalidOperationException("Cluster ready benchmarks require uninstrumented command dispatch.");
        if (!await _standalone.Strings.SetAsync(_key, _payload).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to initialize the cluster benchmark payload.");

        for (var index = 0; index < 50; index++)
        {
            if (await _cluster.Strings.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || (await _cluster.Strings.GetBytesAsync(_key).ConfigureAwait(false))?.Length != _payload.Length
                || await _cluster.Strings.LengthAsync(_key).ConfigureAwait(false) != _payload.Length
                || await _standalone.Strings.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || await _standalone.Strings.LengthAsync(_key).ConfigureAwait(false) != _payload.Length)
                throw new InvalidOperationException("A stable cluster benchmark returned an unexpected reply.");
        }
        if ((await ClusterStringConcurrent50().ConfigureAwait(false)).Any(value => value != _payload)
            || (await ClusterBytesConcurrent50().ConfigureAwait(false)).Any(value => value?.Length != _payload.Length)
            || (await ClusterIntegerConcurrent50().ConfigureAwait(false)).Any(value => value != _payload.Length))
            throw new InvalidOperationException("A concurrent cluster benchmark returned an unexpected reply.");
        _operation = null;
        _operations = 0;
        _process.Refresh();
        _cpuAt = _process.TotalProcessorTime;
        _startedAt = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public ValueTask<string?> ClusterStringSingle()
    {
        Count(nameof(ClusterStringSingle), 1);
        return _cluster.Strings.GetStringAsync(_key);
    }

    [Benchmark]
    public ValueTask<byte[]?> ClusterBytesSingle()
    {
        Count(nameof(ClusterBytesSingle), 1);
        return _cluster.Strings.GetBytesAsync(_key);
    }

    [Benchmark]
    public ValueTask<long> ClusterIntegerSingle()
    {
        Count(nameof(ClusterIntegerSingle), 1);
        return _cluster.Strings.LengthAsync(_key);
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public Task<string?[]> ClusterStringConcurrent50()
    {
        Count(nameof(ClusterStringConcurrent50), 50);
        for (var index = 0; index < _strings.Length; index++)
            _strings[index] = _cluster.Strings.GetStringAsync(_key).AsTask();
        return Task.WhenAll(_strings);
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public Task<byte[]?[]> ClusterBytesConcurrent50()
    {
        Count(nameof(ClusterBytesConcurrent50), 50);
        for (var index = 0; index < _bytes.Length; index++)
            _bytes[index] = _cluster.Strings.GetBytesAsync(_key).AsTask();
        return Task.WhenAll(_bytes);
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public Task<long[]> ClusterIntegerConcurrent50()
    {
        Count(nameof(ClusterIntegerConcurrent50), 50);
        for (var index = 0; index < _integers.Length; index++)
            _integers[index] = _cluster.Strings.LengthAsync(_key).AsTask();
        return Task.WhenAll(_integers);
    }

    [Benchmark]
    public ValueTask<string?> StandaloneStringSingle()
    {
        Count(nameof(StandaloneStringSingle), 1);
        return _standalone.Strings.GetStringAsync(_key);
    }

    [Benchmark]
    public ValueTask<long> StandaloneIntegerSingle()
    {
        Count(nameof(StandaloneIntegerSingle), 1);
        return _standalone.Strings.LengthAsync(_key);
    }

    private void Count(string operation, int count)
    {
        _operation = operation;
        _operations += count;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        // Whole-process CPU includes BDN warmup/calibration and completion work. Keep
        // each launch's raw counters; compare identical fixtures and both baseline controls.
        Console.WriteLine("CLUSTER_READY_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            operation = _operation, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
        }));
        try { if (_cluster is not null) await _cluster.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { if (_standalone is not null) await _standalone.DisposeAsync().ConfigureAwait(false); }
            finally { _process.Dispose(); }
        }
    }
}
