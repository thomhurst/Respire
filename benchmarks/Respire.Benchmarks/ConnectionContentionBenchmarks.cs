using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Real GET pipelines and 50 competing producers on one multiplexed connection.</summary>
[MemoryDiagnoser]
public class ConnectionContentionBenchmarks
{
    private const int PipelineLength = 200;
    private const int Concurrency = 50;
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly RespireKey _key = "contention:GET";
    private readonly RespireValue _value = "benchmark-value";
    private readonly ValueTask<string?>[][] _pending = Enumerable.Range(0, Concurrency)
        .Select(_ => new ValueTask<string?>[PipelineLength]).ToArray();
    private readonly Task<long>[] _workers = new Task<long>[Concurrency];

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
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        if (!await _client.SetAsync(_key, _value)) throw new InvalidOperationException("Seed SET failed.");
        if (await GetPipeline() != PipelineLength * "benchmark-value".Length
            || await ConcurrentGetPipelines() != Concurrency * PipelineLength * "benchmark-value".Length)
            throw new InvalidOperationException("GET pipelines lost replies.");
    }

    [Benchmark(OperationsPerInvoke = PipelineLength)]
    public ValueTask<long> GetPipeline() => RunPipeline(0);

    [Benchmark(OperationsPerInvoke = Concurrency * PipelineLength)]
    public async Task<long> ConcurrentGetPipelines()
    {
        for (var i = 0; i < Concurrency; i++)
        {
            var worker = i;
            _workers[i] = Task.Run(() => RunPipeline(worker).AsTask());
        }
        var lengths = await Task.WhenAll(_workers);
        return lengths.Sum();
    }

    private async ValueTask<long> RunPipeline(int worker)
    {
        var pending = _pending[worker];
        // Issue 200 real public commands before awaiting any reply. These loops
        // describe application pipelining, not amplification of a synthetic operation.
        for (var i = 0; i < PipelineLength; i++) pending[i] = _client.GetStringAsync(_key);
        long length = 0;
        for (var i = 0; i < PipelineLength; i++)
        {
            var value = await pending[i];
            if (value != "benchmark-value") throw new InvalidOperationException("Unexpected GET reply.");
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
