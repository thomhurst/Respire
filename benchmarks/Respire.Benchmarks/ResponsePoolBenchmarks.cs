using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public GET workloads and process retention after mixed owned-result bursts.</summary>
[MemoryDiagnoser]
public class ResponsePoolBenchmarks
{
    private const int PipelineLength = 200;
    private const int Concurrency = 50;
    private const int LargeLength = 1024 * 1024;
    private const string PipelineValue = "benchmark-value";
    private readonly ValueTask<string?>[][] _pending = Enumerable.Range(0, Concurrency)
        .Select(_ => new ValueTask<string?>[PipelineLength]).ToArray();
    private readonly Task<long>[] _workers = new Task<long>[Concurrency];
    private IContainer? _container;
    private RespireClient _client = null!;

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
                Endpoints = { new RespireEndpoint(host, port) }, Connections = 1, Protocol = RespProtocol.Resp2,
                MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
            });
            if (!await _client.SetAsync("pool:pipeline", PipelineValue)
                || !await _client.SetAsync("pool:small", new string('x', 1024))
                || !await _client.SetAsync("pool:large", new string('x', LargeLength)))
                throw new InvalidOperationException("Response-pool seed SET failed.");
            if (await GetPipeline() != PipelineLength * PipelineValue.Length
                || await ConcurrentGetPipelines() != Concurrency * PipelineLength * PipelineValue.Length
                || await GetSmallValue() != 1024 || await GetLargeValue() != LargeLength)
                throw new InvalidOperationException("Response-pool GET validation failed.");
            // Hold roots until each burst ends: sequentially awaiting the commands
            // still forces all payload/element arrays to remain simultaneously owned.
            await RetainBurst("GET", ["pool:large"], 32, LargeLength, aggregate: false);
            await RetainBurst("MGET", ["pool:pipeline", "pool:pipeline"], PipelineLength, 2, aggregate: true);
            ReportMemory("connected-idle");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    private async Task RetainBurst(RespireCommand command, RespireValue[] arguments, int count, int expected, bool aggregate)
    {
        var results = new RespireResult[count];
        try
        {
            for (var index = 0; index < results.Length; index++)
            {
                results[index] = await _client.ExecuteAsync(command, arguments);
                var actual = aggregate ? results[index].Count : results[index].AsSpan().Length;
                if (actual != expected) throw new InvalidOperationException("Unexpected retained result.");
            }
        }
        finally
        {
            for (var index = 0; index < results.Length; index++)
            {
                results[index].Dispose();
                results[index] = default;
            }
        }
    }

    [Benchmark(OperationsPerInvoke = PipelineLength)]
    public ValueTask<long> GetPipeline() => RunPipeline(0);

    [Benchmark(OperationsPerInvoke = Concurrency * PipelineLength)]
    public async Task<long> ConcurrentGetPipelines()
    {
        for (var index = 0; index < Concurrency; index++)
        {
            var worker = index;
            _workers[index] = Task.Run(() => RunPipeline(worker).AsTask());
        }
        return (await Task.WhenAll(_workers)).Sum();
    }

    private async ValueTask<long> RunPipeline(int worker)
    {
        var pending = _pending[worker];
        for (var index = 0; index < pending.Length; index++) pending[index] = _client.GetStringAsync("pool:pipeline");
        long length = 0;
        for (var index = 0; index < pending.Length; index++)
        {
            var value = await pending[index];
            if (value != PipelineValue) throw new InvalidOperationException("Unexpected pipeline GET reply.");
            length += value.Length;
        }
        return length;
    }

    [Benchmark] public async ValueTask<int> GetSmallValue() => Check(await _client.GetStringAsync("pool:small"), 1024);
    [Benchmark] public async ValueTask<int> GetLargeValue() => Check(await _client.GetStringAsync("pool:large"), LargeLength);

    private static int Check(string? value, int expected)
    {
        if (value is null || value.Length != expected || value[0] != 'x' || value[^1] != 'x')
            throw new InvalidOperationException("Unexpected value GET reply.");
        return value.Length;
    }

    private static void ReportMemory(string stage)
    {
        // Includes only this benchmark process: its client, pools and fixture fields.
        // Redis is external. Full-GC snapshots are descriptive, not leak assertions.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var info = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        Console.WriteLine("RECEIVE_MEMORY " + JsonSerializer.Serialize(new
        {
            stage, managedBytes = GC.GetTotalMemory(false), pohBytes = info.GenerationInfo[4].SizeAfterBytes,
            pohFragmentedBytes = info.GenerationInfo[4].FragmentationAfterBytes,
            workingSetBytes = process.WorkingSet64,
        }));
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) { await _client.DisposeAsync(); _client = null!; }
        if (_container is not null) { await _container.DisposeAsync(); _container = null; }
        foreach (var pending in _pending) Array.Clear(pending);
        Array.Clear(_workers);
        ReportMemory("after-dispose");
    }
}
