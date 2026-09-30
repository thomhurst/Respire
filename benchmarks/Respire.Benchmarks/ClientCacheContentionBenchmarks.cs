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
    private readonly string _key = $"respire:cache-contention:{Guid.NewGuid():N}";
    private readonly string _hash = $"respire:cache-contention:hash:{Guid.NewGuid():N}";
    private readonly string _value = new('x', 256);

    [Params(1, 32)]
    public int Callers { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379");
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(host, port) },
            Connections = 1,
            ClientSideCache = new(),
        });
        _pending = new Task<string?>[Callers];
        await _client.SetAsync(_key, _value);
        await _client.Hashes.SetAsync(_hash, "field", _value);
        if (await _client.GetStringAsync(_key) != _value
            || await _client.Hashes.GetStringAsync(_hash, "field") != _value)
            throw new InvalidOperationException("Failed to prime contention benchmark values.");
    }

    [Benchmark]
    public Task<string?[]> GetMissBurst()
    {
        _client.ClientSideCache!.Clear();
        for (var i = 0; i < _pending.Length; i++)
            _pending[i] = _client.GetStringAsync(_key).AsTask();
        return Task.WhenAll(_pending);
    }

    [Benchmark]
    public Task<string?[]> HashGetMissBurst()
    {
        _client.ClientSideCache!.Clear();
        for (var i = 0; i < _pending.Length; i++)
            _pending[i] = _client.Hashes.GetStringAsync(_hash, "field").AsTask();
        return Task.WhenAll(_pending);
    }

    [Benchmark]
    public ValueTask<string?> HotGet() => _client.GetStringAsync(_key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _client.DeleteAsync(_key);
        await _client.DeleteAsync(_hash);
        await _client.DisposeAsync();
    }
}
