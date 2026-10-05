using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Caller-owned binary GET results below and above the direct-fill threshold.</summary>
[MemoryDiagnoser]
public class ByteGetBenchmarks
{
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly string _key = "bench:byte-get";

    [Params(1024, 1048576)]
    public int PayloadLength { get; set; }

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
            host = "127.0.0.1";
            port = _container.GetMappedPublicPort(6379);
        }
        _client = await RespireClient.ConnectAsync($"{host}:{port}");
        var payload = new byte[PayloadLength];
        new Random(42).NextBytes(payload);
        await _client.SetAsync(_key, payload);
        if (!(await GetBytes())!.AsSpan().SequenceEqual(payload)
            || !(await GetTypedBytes())!.AsSpan().SequenceEqual(payload))
            throw new InvalidOperationException("GET did not preserve the seeded binary payload.");
    }

    [Benchmark]
    public ValueTask<byte[]?> GetBytes() => _client.GetBytesAsync(_key);

    [Benchmark]
    public ValueTask<byte[]?> GetTypedBytes() => _client.GetAsync<byte[]>(_key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
