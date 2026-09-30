using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Ongoing coverage of idle and command costs with the process-wide probe enabled or disabled.</summary>
[MemoryDiagnoser]
public class ThreadPoolMonitoringBenchmarks
{
    private readonly RedisContainer _redis = new RedisBuilder("redis:7.2-alpine").Build();
    private readonly RespireKey _key = "thread-pool-monitoring-benchmark";
    private RespireClient _client = null!;
    private readonly Process _process = Process.GetCurrentProcess();
    private long _operations;
    private long _startedAt;
    private long _allocatedAt;
    private TimeSpan _cpuAt;

    [Params(false, true)]
    public bool MonitoringEnabled { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        await _redis.StartAsync();
        var options = new RespireOptions
        {
            Endpoints = { new RespireEndpoint(_redis.Hostname, _redis.GetMappedPublicPort(6379)) },
            Connections = 1, CommandTimeout = TimeSpan.FromSeconds(5)
        };
        // The same source runs against a pinned baseline predating this option.
        var monitoring = typeof(RespireOptions).GetProperty("ThreadPoolMonitoring");
        monitoring?.SetValue(options, MonitoringEnabled);
        _client = await RespireClient.ConnectAsync(options);
        await _client.SetAsync(_key, "value");
        await Task.Delay(TimeSpan.FromSeconds(2));
        StartCounters();
        await Task.Delay(TimeSpan.FromSeconds(10));
        ReportCounters("idle", monitoring is not null);
        StartCounters();
    }

    [Benchmark]
    public async ValueTask<long> ReadLength()
    {
        _operations++;
        return await _client.Strings.LengthAsync(_key);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // These process-wide counters include background threads and BDN warmup/overhead.
        // MemoryDiagnoser remains the operation-level allocation measurement.
        ReportCounters("workload", typeof(RespireOptions).GetProperty("ThreadPoolMonitoring") is not null);
        if (_client is not null) await _client.DisposeAsync();
        await _redis.DisposeAsync();
        _process.Dispose();
    }

    private void StartCounters()
    {
        _process.Refresh();
        _cpuAt = _process.TotalProcessorTime;
        _allocatedAt = GC.GetTotalAllocatedBytes(precise: true);
        _startedAt = Stopwatch.GetTimestamp();
    }

    private void ReportCounters(string phase, bool probeSupported)
    {
        var elapsed = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds;
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - _allocatedAt;
        _process.Refresh();
        var cpuMs = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds;
        Console.WriteLine("THREAD_POOL_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            phase, MonitoringEnabled, probeSupported, elapsedSeconds = elapsed,
            cpuMilliseconds = cpuMs, allocatedBytes = bytes, operations = _operations
        }));
    }
}
