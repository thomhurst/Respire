using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Guards the allocation-free invalidation path when applications register no observers.</summary>
[MemoryDiagnoser]
[OperationsPerSecond]
public class ClientCacheInvalidationBenchmarks
{
    private ClientSideCacheCoordinator _cache = null!;
    private readonly RespireKey _key = "cache:invalidation:key";
    private RespValue _push;
    private long _started;
    private TimeSpan _cpuStarted;

    [GlobalSetup]
    public void Setup()
    {
        _cache = new(new());
        _push = RespValue.Array([
            RespValue.BulkString("invalidate"u8.ToArray()),
            RespValue.Array([RespValue.BulkString("cache:invalidation:key"u8.ToArray())]),
        ]);
        _cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
        _started = Stopwatch.GetTimestamp();
    }

    // The cache is empty throughout: insertion would obscure the cost of dispatching an
    // invalidation when nothing is observed. Atomic counters make both operations observable.
    [Benchmark]
    public void LocalInvalidation() => _cache.Invalidate(in _key);

    [Benchmark]
    public void ServerInvalidation() => _cache.HandlePush(in _push);

    [GlobalCleanup]
    public void Cleanup()
    {
        Console.WriteLine("CACHE_INVALIDATION_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            elapsedSeconds = Stopwatch.GetElapsedTime(_started).TotalSeconds,
            cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - _cpuStarted).TotalMilliseconds,
        }));
        _push.Dispose();
    }
}
