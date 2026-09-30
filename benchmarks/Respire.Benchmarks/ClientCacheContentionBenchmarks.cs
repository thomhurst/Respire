using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>
/// Measures a burst of callers after local eviction, with the same key and query arguments.
/// One invocation is one burst (not one caller). The loop creates the workload's concurrent
/// callers; BenchmarkDotNet controls repetition. The one-caller case measures miss overhead.
/// This fixture also compiles against the implementation before request coalescing.
/// </summary>
[MemoryDiagnoser]
[OperationsPerSecond]
public class ClientCacheContentionBenchmarks
{
    private RespireClient _client = null!;
    private Task<string?>[] _pending = null!;
    private Task<string?[]>[] _pendingMany = null!;
    private readonly string _key = $"respire:cache-contention:{Guid.NewGuid():N}";
    private readonly string _secondKey = $"respire:cache-contention:{Guid.NewGuid():N}";
    private readonly string _hash = $"respire:cache-contention:hash:{Guid.NewGuid():N}";
    private readonly string _value = new('x', 256);

    public enum Workload { DefaultSingle, CoalescedSingle, CoalescedBurst }

    [Params(Workload.DefaultSingle, Workload.CoalescedSingle, Workload.CoalescedBurst)]
    public Workload Scenario { get; set; }

    private int Callers => Scenario == Workload.CoalescedBurst ? 32 : 1;
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _cpuAt;
    private long _startedAt;
    private long _operations;
    private string _operation = "";
    private bool _sharingSupported;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");
        var cacheOptions = new RespireClientSideCacheOptions();
        var sharing = typeof(RespireClientSideCacheOptions).GetProperty("CoalesceConcurrentMisses");
        _sharingSupported = sharing is not null;
        if (Scenario != Workload.DefaultSingle)
        {
            if (sharing is null && Environment.GetEnvironmentVariable("RESPIRE_CONTENTION_BASELINE") != "1")
                throw new InvalidOperationException("Candidate does not expose CoalesceConcurrentMisses.");
            sharing?.SetValue(cacheOptions, true);
        }
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(host, port) },
            Connections = 1,
            ClientSideCache = cacheOptions,
        });
        _pending = new Task<string?>[Callers];
        _pendingMany = new Task<string?[]>[Callers];
        await _client.SetAsync(_key, _value);
        await _client.SetAsync(_secondKey, _value);
        await _client.Hashes.SetAsync(_hash, "field", _value);
        if (await _client.GetStringAsync(_key) != _value
            || await _client.Hashes.GetStringAsync(_hash, "field") != _value)
            throw new InvalidOperationException("Failed to prime contention benchmark values.");
        _process.Refresh();
        _cpuAt = _process.TotalProcessorTime;
        _startedAt = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public Task<string?[]> GetMissBurst()
    {
        _operation = nameof(GetMissBurst);
        _operations++;
        _client.ClientSideCache!.Clear();
        for (var i = 0; i < _pending.Length; i++)
            _pending[i] = _client.GetStringAsync(_key).AsTask();
        return Task.WhenAll(_pending);
    }

    [Benchmark]
    public Task<string?[][]> GetManyMissBurst()
    {
        _operation = nameof(GetManyMissBurst);
        _operations++;
        _client.ClientSideCache!.Clear();
        for (var index = 0; index < _pendingMany.Length; index++)
            _pendingMany[index] = _client.Strings.GetManyAsync(_key, _secondKey).AsTask();
        return Task.WhenAll(_pendingMany);
    }

    [Benchmark]
    public Task<string?[]> HashGetMissBurst()
    {
        _operation = nameof(HashGetMissBurst);
        _operations++;
        _client.ClientSideCache!.Clear();
        for (var i = 0; i < _pending.Length; i++)
            _pending[i] = _client.Hashes.GetStringAsync(_hash, "field").AsTask();
        return Task.WhenAll(_pending);
    }

    [Benchmark]
    public ValueTask<string?> HotGet()
    {
        _operation = nameof(HotGet);
        _operations++;
        return _client.GetStringAsync(_key);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        Console.WriteLine("CACHE_CONTENTION_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            operation = _operation, scenario = Scenario.ToString(), callers = Callers, sharingSupported = _sharingSupported,
            elapsedSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds,
            operations = _operations
        }));
        await _client.DeleteAsync(_key);
        await _client.DeleteAsync(_secondKey);
        await _client.DeleteAsync(_hash);
        await _client.DisposeAsync();
        _process.Dispose();
    }
}
