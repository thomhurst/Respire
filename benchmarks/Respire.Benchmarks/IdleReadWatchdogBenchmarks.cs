using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Matched public GET paths with the idle-read watchdog enabled and disabled.</summary>
[MemoryDiagnoser]
public class IdleReadWatchdogBenchmarks
{
    private const int PipelineLength = 200;
    private const int Concurrency = 50;
    private const string PipelineValue = "benchmark-value";
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly RespireKey _pipelineKey = "watchdog:pipeline";
    private readonly RespireKey _smallKey = "watchdog:small";
    private readonly RespireKey _largeKey = "watchdog:large";
    private readonly ValueTask<string?>[][] _pending = Enumerable.Range(0, Concurrency)
        .Select(_ => new ValueTask<string?>[PipelineLength]).ToArray();
    private readonly Task<long>[] _workers = new Task<long>[Concurrency];

    [Params(false, true)]
    public bool WatchdogEnabled { get; set; }

    [GlobalSetup]
    public async Task Setup()
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
            ConnectionIdleReadTimeout = WatchdogEnabled ? TimeSpan.FromSeconds(30) : null,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        var small = new byte[1024];
        var large = new byte[1048576];
        new Random(42).NextBytes(small);
        new Random(43).NextBytes(large);
        if (!await _client.SetAsync(_pipelineKey, PipelineValue)
            || !await _client.SetAsync(_smallKey, small)
            || !await _client.SetAsync(_largeKey, large))
            throw new InvalidOperationException("Seed SET failed.");
        if (await GetPipeline() != PipelineLength * PipelineValue.Length
            || await ConcurrentGetPipelines() != Concurrency * PipelineLength * PipelineValue.Length
            || !(await GetSmallBytes())!.AsSpan().SequenceEqual(small)
            || !(await GetLargeBytes())!.AsSpan().SequenceEqual(large))
            throw new InvalidOperationException("GET did not preserve the seeded replies.");
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

    [Benchmark]
    public ValueTask<byte[]?> GetSmallBytes() => _client.GetBytesAsync(_smallKey);

    [Benchmark]
    public ValueTask<byte[]?> GetLargeBytes() => _client.GetBytesAsync(_largeKey);

    private async ValueTask<long> RunPipeline(int worker)
    {
        var pending = _pending[worker];
        for (var index = 0; index < PipelineLength; index++) pending[index] = _client.GetStringAsync(_pipelineKey);
        long length = 0;
        for (var index = 0; index < PipelineLength; index++)
        {
            var value = await pending[index];
            if (value != PipelineValue) throw new InvalidOperationException("Unexpected GET reply.");
            length += value.Length;
        }
        return length;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
