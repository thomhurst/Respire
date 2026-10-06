using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>End-to-end typed SET/GET allocations, including owned payloads and response objects.</summary>
[MemoryDiagnoser]
public class TypedSerializationBenchmarks
{
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly Payload _small = new("Ada", 42);
    private readonly Payload _large = new(new string('x', 4096), 42);
    private readonly NumberPair _pair = new(42, 123456789);

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort) ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _container = new RedisBuilder("redis:8.10").Build();
            await _container.StartAsync();
            host = "127.0.0.1";
            port = _container.GetMappedPublicPort(6379);
        }
        _client = await RespireClient.ConnectAsync($"{host}:{port}");
        await SetSmallPoco();
        await SetLargePoco();
        await SetInt32();
        await SetStruct();
        if (await GetSmallPoco() != _small || await GetLargePoco() != _large
            || await GetInt32() != 42 || await GetStruct() != _pair)
            throw new InvalidOperationException("Typed serialization did not preserve the seeded values.");
    }

    [Benchmark] public ValueTask<bool> SetSmallPoco() => _client.SetAsync("typed:small", _small);
    [Benchmark] public ValueTask<Payload?> GetSmallPoco() => _client.GetAsync<Payload>("typed:small");
    [Benchmark] public ValueTask<bool> SetLargePoco() => _client.SetAsync("typed:large", _large);
    [Benchmark] public ValueTask<Payload?> GetLargePoco() => _client.GetAsync<Payload>("typed:large");
    [Benchmark] public ValueTask<bool> SetInt32() => _client.SetAsync<int>("typed:int", 42);
    [Benchmark] public ValueTask<int> GetInt32() => _client.GetAsync<int>("typed:int");
    [Benchmark] public ValueTask<bool> SetStruct() => _client.SetAsync("typed:struct", _pair);
    [Benchmark] public ValueTask<NumberPair> GetStruct() => _client.GetAsync<NumberPair>("typed:struct");

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }

    public sealed record Payload(string Name, int Age);
    public readonly record struct NumberPair(int First, long Second);
}
