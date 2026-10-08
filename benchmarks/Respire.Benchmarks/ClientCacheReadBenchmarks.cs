using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public local GET hits, concurrent hits, and eviction followed by a standalone miss.</summary>
[MemoryDiagnoser]
public class ClientCacheReadBenchmarks
{
    private const int Callers = 50;
    private const int ReadsPerCaller = 64;
    private readonly Task<long>[] _workers = new Task<long>[Callers];
    private readonly string _value = new('x', 128);
    private IContainer? _container;
    private RespireClient _client = null!;
    private string _key = "";

    [Params(false, true)]
    public bool UnicodeKey { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        try
        {
            var host = Environment.GetEnvironmentVariable("REDIS_HOST");
            var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
                ? configuredPort : 6379;
            if (string.IsNullOrEmpty(host))
            {
                _container = new RedisBuilder("redis:8.10").Build();
                await _container.StartAsync();
                host = _container.Hostname;
                port = _container.GetMappedPublicPort(6379);
            }
            _key = UnicodeKey ? "cache:read:tenant-é😀:product:0123456789" : "cache:read:tenant-ascii:product:0123456789";
            _client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new(host, port)], Connections = 1, Protocol = RespProtocol.Resp3,
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled, ClientSideCache = new(),
            });
            if (!await _client.SetAsync(_key, _value) || await EvictAndReadMiss() != _value
                || await _client.GetStringAsync(_key) != _value || await StringHit() != _value)
                throw new InvalidOperationException("Failed to prime the cached GET.");
            var hits = _client.ClientSideCache!.GetStatistics().Hits;
            if (await ConcurrentStringHits() != Callers * ReadsPerCaller * _value.Length
                || _client.ClientSideCache.GetStatistics().Hits != hits + Callers * ReadsPerCaller)
                throw new InvalidOperationException("Concurrent reads did not all hit the local cache.");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark]
    public ValueTask<string?> StringHit() => _client.GetStringAsync(_key);

    /// <summary>One lookup, including its share of scheduling a 50-caller burst.</summary>
    [Benchmark(OperationsPerInvoke = Callers * ReadsPerCaller)]
    public async Task<long> ConcurrentStringHits()
    {
        // The loop creates the concurrent workload. It is not a replacement for
        // BenchmarkDotNet's iteration loop; every lookup is counted explicitly.
        for (var index = 0; index < _workers.Length; index++)
            _workers[index] = Task.Run(() =>
            {
                long length = 0;
                for (var read = 0; read < ReadsPerCaller; read++)
                {
                    var operation = _client.GetStringAsync(_key);
                    if (!operation.IsCompletedSuccessfully)
                        throw new InvalidOperationException("Expected a synchronous cache hit.");
                    length += operation.Result!.Length;
                }
                return length;
            });
        var lengths = await Task.WhenAll(_workers);
        return lengths.Sum();
    }

    /// <summary>Includes explicit eviction, tracked wire GET, entry publication and string conversion.</summary>
    [Benchmark]
    public ValueTask<string?> EvictAndReadMiss()
    {
        _client.ClientSideCache!.Clear();
        return _client.GetStringAsync(_key);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
