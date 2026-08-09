using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using DotNet.Testcontainers.Containers;
using Respire.Infrastructure;
using Respire.Networking;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

[SimpleJob(warmupCount: 3, iterationCount: 10)]
[MemoryDiagnoser]
public class ConnectionSelectionBenchmarks
{
    private IContainer? _redisContainer;
    private RespireConnectionMultiplexer _multiplexer = null!;

    [Params(1, 2)]
    public int Connections { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        int port;

        if (string.IsNullOrEmpty(host))
        {
            _redisContainer = new RedisBuilder("redis:8.0").Build();
            await _redisContainer.StartAsync();
            host = "localhost";
            port = _redisContainer.GetMappedPublicPort(6379);
        }
        else
        {
            port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort)
                ? configuredPort
                : 6379;
        }

        _multiplexer = await RespireConnectionMultiplexer.CreateAsync(
            host, port, Connections);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _multiplexer.DisposeAsync();
        if (_redisContainer is not null)
        {
            await _redisContainer.DisposeAsync();
        }
    }

    [Benchmark]
    public RespireConnection SelectHealthyConnection() => _multiplexer.GetConnection();
}
