using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;

namespace Respire.Benchmarks;

/// <summary>Tracks local-hit cost and an external write/invalidation/read cycle in each tracking mode.</summary>
[MemoryDiagnoser]
[OperationsPerSecond]
public class ClientCacheTrackingBenchmarks
{
    private RespireClient? _reader;
    private RespireClient? _writer;
    private readonly string _key = "cache:benchmark:key";
    private readonly string _value = "cache benchmark value";
    private long _started;
    private TimeSpan _cpuStarted;
    private long _allocatedStarted;

    [ParamsSource(nameof(Modes))]
    public string Mode { get; set; } = "OptIn";

    // Baseline builds predate BCAST. Their OPTIN cases still use this identical fixture.
    public IEnumerable<string> Modes => Environment.GetEnvironmentVariable("RESPIRE_BENCH_BASELINE") == "1"
        ? ["OptIn"] : ["OptIn", "Broadcast", "BroadcastPrefix"];

    [GlobalSetup]
    public async Task Setup()
    {
        var options = new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var port) ? port : 6379)],
            Connections = 1,
        };
        var cache = new RespireClientSideCacheOptions();
        if (Mode != "OptIn")
        {
            // Reflection is confined to setup so this fixture compiles against the old API.
            var mode = typeof(RespireClientSideCacheOptions).GetProperty("TrackingMode")
                ?? throw new InvalidOperationException("The selected build has no broadcast tracking support.");
            mode.SetValue(cache, Enum.Parse(mode.PropertyType, "Broadcast"));
            if (Mode == "BroadcastPrefix")
                typeof(RespireClientSideCacheOptions).GetProperty("BroadcastPrefixes")!
                    .SetValue(cache, new RespireKey[] { "cache:benchmark:" });
        }
        _writer = await RespireClient.ConnectAsync(options);
        await _writer.SetAsync(_key, _value);
        _reader = await RespireClient.ConnectAsync(options with { ClientSideCache = cache });
        if (await _reader.GetStringAsync(_key) != _value || _reader.ClientSideCache!.Count != 1)
            throw new InvalidOperationException("Cache setup did not retain the expected value.");
        var hits = _reader.ClientSideCache.GetStatistics().Hits;
        await _reader.GetStringAsync(_key);
        if (_reader.ClientSideCache.GetStatistics().Hits != hits + 1)
            throw new InvalidOperationException("The hit benchmark is not using a local cache entry.");
        _cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
        _allocatedStarted = GC.GetTotalAllocatedBytes();
        _started = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public ValueTask<string?> Hit() => _reader!.GetStringAsync(_key);

    [Benchmark]
    public async Task<string?> WriteInvalidateRead()
    {
        await _writer!.SetAsync(_key, _value);
        var started = Stopwatch.GetTimestamp();
        while (_reader!.ClientSideCache!.Count != 0)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Tracking invalidation did not evict the measured entry.");
            await Task.Yield();
        }
        return await _reader.GetStringAsync(_key);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_started != 0)
            Console.WriteLine("CACHE_TRACKING_PROCESS_METRICS " + JsonSerializer.Serialize(new
            {
                Mode,
                elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
                cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStarted).TotalMilliseconds,
                allocatedBytes = GC.GetTotalAllocatedBytes() - _allocatedStarted,
            }));
        if (_reader is not null) await _reader.DisposeAsync();
        if (_writer is not null)
        {
            await _writer.DeleteAsync(_key);
            await _writer.DisposeAsync();
        }
    }
}
