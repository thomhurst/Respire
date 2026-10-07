using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Public integer conversion with Cmd1/Cmd3, singly and eight commands in flight.</summary>
[MemoryDiagnoser]
public class CommandConversionBenchmarks
{
    private IContainer? _container;
    private RespireClient _client = null!;
    private readonly RespireKey _key = "conversion:integer";
    private readonly RespireValue _value = "benchmark-value";
    private long _offset = 0;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST");
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configuredPort) ? configuredPort : 6379;
        if (string.IsNullOrEmpty(host))
        {
            _container = new RedisBuilder("redis:8.10").Build();
            await _container.StartAsync();
            host = _container.Hostname;
            port = _container.GetMappedPublicPort(6379);
        }
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint(host, port) }, Protocol = RespProtocol.Resp2, Connections = 1,
            MaintenanceNotifications = RespireMaintenanceNotificationMode.Disabled,
        });
        if (!await _client.SetAsync(_key, _value)) throw new InvalidOperationException("Seed SET failed.");
        var expected = "benchmark-value".Length;
        if (await SmallLength() != expected || await LargeSetRange() != expected
            || await SmallLengthPipeline() != 8 * expected || await LargeSetRangePipeline() != 8 * expected)
            throw new InvalidOperationException("Conversion fixture did not preserve all replies.");
    }

    [Benchmark] public ValueTask<long> SmallLength() => _client.Strings.LengthAsync(_key);
    [Benchmark] public ValueTask<long> LargeSetRange() => _client.Strings.SetRangeAsync(_key, _offset, _value);

    [Benchmark(OperationsPerInvoke = 8)]
    public async ValueTask<long> SmallLengthPipeline()
    {
        // Eight real public calls before the first await measure pipelining, not a synthetic copy loop.
        var first = _client.Strings.LengthAsync(_key);
        var second = _client.Strings.LengthAsync(_key);
        var third = _client.Strings.LengthAsync(_key);
        var fourth = _client.Strings.LengthAsync(_key);
        var fifth = _client.Strings.LengthAsync(_key);
        var sixth = _client.Strings.LengthAsync(_key);
        var seventh = _client.Strings.LengthAsync(_key);
        var eighth = _client.Strings.LengthAsync(_key);
        return await first + await second + await third + await fourth
            + await fifth + await sixth + await seventh + await eighth;
    }

    [Benchmark(OperationsPerInvoke = 8)]
    public async ValueTask<long> LargeSetRangePipeline()
    {
        var first = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var second = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var third = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var fourth = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var fifth = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var sixth = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var seventh = _client.Strings.SetRangeAsync(_key, _offset, _value);
        var eighth = _client.Strings.SetRangeAsync(_key, _offset, _value);
        return await first + await second + await third + await fourth
            + await fifth + await sixth + await seventh + await eighth;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
