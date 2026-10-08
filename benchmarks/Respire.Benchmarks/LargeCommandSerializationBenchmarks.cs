using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public binary SET sizes, mixed large/small admission, and competing small producers.</summary>
[MemoryDiagnoser]
public class LargeCommandSerializationBenchmarks
{
    private const int SmallPipelineLength = 64;
    private const int Concurrency = 50;
    private const int WorkerPipelineLength = 32;
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly RespireKey _key = "large-serialization:SET";
    private readonly RespireValue _tenKiB = new byte[10 * 1024];
    private readonly RespireValue _oneMiB = new byte[1024 * 1024];
    private readonly RespireValue _fiveMiB = new byte[5 * 1024 * 1024];
    private readonly RespireValue _small = new byte[128];
    private readonly ValueTask<TimeSpan>[] _pings = new ValueTask<TimeSpan>[SmallPipelineLength];
    private readonly ValueTask<bool>[][] _pending = Enumerable.Range(0, Concurrency)
        .Select(_ => new ValueTask<bool>[WorkerPipelineLength]).ToArray();
    private readonly Task<int>[] _workers = new Task<int>[Concurrency];

    [GlobalSetup]
    public async Task Setup()
    {
        ReportMemory("before-connect");
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
        await Set10KiB();
        await Set1MiB();
        await Set5MiB();
        if (await LargeThenSmallPipeline() != SmallPipelineLength
            || await ConcurrentSmallSetPipelines() != Concurrency * WorkerPipelineLength)
            throw new InvalidOperationException("Large/small pipelines lost replies.");
        ReportMemory("connected-idle");
        // A bounded, real public-command workload records GC/retention separately from BDN timing.
        // Take collection counts after the snapshot's forced collections and before the next snapshot.
        var gen2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        for (var index = 0; index < 64; index++) await Set5MiB();
        Console.WriteLine("LARGE_SET_STRESS " + JsonSerializer.Serialize(new
        {
            operations = 64, payloadBytes = 5 * 1024 * 1024,
            gen2Collections = GC.CollectionCount(2) - gen2,
            allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated,
        }));
        ReportMemory("after-sustained");
    }

    [Benchmark]
    public ValueTask<bool> Set10KiB() => SetChecked(_tenKiB);

    [Benchmark]
    public ValueTask<bool> Set1MiB() => SetChecked(_oneMiB);

    [Benchmark]
    public ValueTask<bool> Set5MiB() => SetChecked(_fiveMiB);

    /// <summary>A bounded sustained public SET workload, normalized per command.</summary>
    [Benchmark(OperationsPerInvoke = 32)]
    public async ValueTask<int> SustainedSet5MiB()
    {
        for (var index = 0; index < 32; index++) await Set5MiB();
        return 32;
    }

    private async ValueTask<bool> SetChecked(RespireValue value)
    {
        if (!await _client.Strings.SetAsync(_key, value)) throw new InvalidOperationException("SET failed.");
        return true;
    }

    // Report the whole mixed batch: a 5 MiB SET followed by 64 small commands on one producer.
    [Benchmark]
    public async ValueTask<int> LargeThenSmallPipeline()
    {
        var large = Set5MiB();
        for (var index = 0; index < SmallPipelineLength; index++) _pings[index] = _client.PingAsync();
        await large;
        for (var index = 0; index < SmallPipelineLength; index++) await _pings[index];
        return SmallPipelineLength;
    }

    [Benchmark(OperationsPerInvoke = Concurrency * WorkerPipelineLength)]
    public async Task<int> ConcurrentSmallSetPipelines()
    {
        for (var index = 0; index < Concurrency; index++)
        {
            var worker = index;
            _workers[index] = Task.Run(() => RunSmallPipeline(worker).AsTask());
        }
        return (await Task.WhenAll(_workers)).Sum();
    }

    private async ValueTask<int> RunSmallPipeline(int worker)
    {
        var pending = _pending[worker];
        for (var index = 0; index < WorkerPipelineLength; index++)
            pending[index] = _client.Strings.SetAsync(_key, _small);
        for (var index = 0; index < WorkerPipelineLength; index++)
            if (!await pending[index]) throw new InvalidOperationException("Concurrent SET failed.");
        return WorkerPipelineLength;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) { await _client.DisposeAsync(); _client = null!; }
        if (_container is not null) { await _container.DisposeAsync(); _container = null; }
        Array.Clear(_pings);
        foreach (var pending in _pending) Array.Clear(pending);
        Array.Clear(_workers);
        ReportMemory("after-dispose");
    }

    private static void ReportMemory(string stage)
    {
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
}
