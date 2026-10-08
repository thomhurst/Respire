using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public cold string reads, first local reuse, and one-off versus reused cache retention.</summary>
[MemoryDiagnoser]
public class ClientCachePublicationBenchmarks
{
    private const int Population = 512;
    private readonly string _value = new('x', 128);
    private readonly string _key = "cache:publication:measured";
    private IContainer? _container;
    private RespireClient _client = null!;

    [Params(false, true)]
    public bool Coalesce { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        ReportMemory("before-connect");
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
            _client = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new(host, port)], Connections = 1, Protocol = RespProtocol.Resp3,
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
                ClientSideCache = new() { CoalesceConcurrentMisses = Coalesce, MaxSizeBytes = 1_000_000 },
            });
            var keys = Enumerable.Range(0, Population).Select(index => "cache:publication:retained:" + index).ToArray();
            foreach (var key in keys)
                if (!await _client.SetAsync(key, _value)) throw new InvalidOperationException("Seed SET failed.");

            await ReadPopulation(keys);
            if (_client.ClientSideCache!.Count != Population)
                throw new InvalidOperationException("The one-off population must fit the configured byte limit.");
            // No returned strings are retained by this fixture. This stage measures
            // the cache after 512 independent string misses, before any local reuse.
            ReportMemory("connected-idle");
            await ReadPopulation(keys);
            ReportMemory("after-first-reuse");
            _client.ClientSideCache.Clear();

            if (!await _client.SetAsync(_key, _value) || await ColdStringMiss() != _value
                || await ColdMissAndFirstReuse() != 2 * _value.Length || await WarmStringHit() != _value)
                throw new InvalidOperationException("String publication benchmark validation failed.");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    private async Task ReadPopulation(string[] keys)
    {
        foreach (var key in keys)
            if (await _client.GetStringAsync(key) != _value)
                throw new InvalidOperationException("A population read returned the wrong value.");
    }

    [Benchmark]
    public ValueTask<string?> ColdStringMiss()
    {
        _client.ClientSideCache!.Clear();
        return _client.GetStringAsync(_key);
    }

    /// <summary>One logical miss-and-reuse pair, including clearing, tracked GET, and the first local string hit.</summary>
    [Benchmark]
    public async ValueTask<int> ColdMissAndFirstReuse()
    {
        _client.ClientSideCache!.Clear();
        var first = await _client.GetStringAsync(_key);
        var second = await _client.GetStringAsync(_key);
        return first!.Length + second!.Length;
    }

    [Benchmark]
    public ValueTask<string?> WarmStringHit() => _client.GetStringAsync(_key);

    private void ReportMemory(string stage)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, Coalesce, population = Population,
            cacheEntries = _client?.ClientSideCache?.Count ?? 0,
            cacheBytes = _client?.ClientSideCache?.GetStatistics().SizeBytes ?? 0,
            managedBytes = GC.GetTotalMemory(false), pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) { await _client.DisposeAsync(); _client = null!; }
        if (_container is not null) { await _container.DisposeAsync(); _container = null; }
        ReportMemory("after-dispose");
    }
}
