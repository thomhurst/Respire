using BenchmarkDotNet.Attributes;
using DotNet.Testcontainers.Containers;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using Testcontainers.Redis;

namespace Respire.Benchmarks;

/// <summary>Permanent coverage of hot RESP headers, signed integer framing and fixed SET options.</summary>
[MemoryDiagnoser]
public class RespFramingSerializationBenchmarks
{
    private WriteBuffer _buffer = null!;
    private RespireValue _key = "framing:key";
    private RespireValue _value = "framing-value";
    private RespireExpiry _expiry = RespireExpiry.In(TimeSpan.FromMinutes(1));

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new WriteBuffer(256);
        if (WriteGetCommand() <= 0 || WriteSetOptions() <= 0 || WriteBulkInteger(long.MinValue) != 27)
            throw new InvalidOperationException("Unexpected serialized frame length.");
    }

    [Benchmark]
    [Arguments(0L)]
    [Arguments(1024L)]
    [Arguments(long.MinValue)]
    [Arguments(long.MaxValue)]
    public int WriteBulkInteger(long value)
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        writer.WriteBulkInteger(value);
        return _buffer.Count;
    }

    [Benchmark]
    public int WriteGetCommand()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        new Cmd1(Verbs.Get, _key).Write(ref writer);
        return _buffer.Count;
    }

    [Benchmark]
    public int WriteSetOptions()
    {
        _buffer.Reset();
        var writer = new RespWriter(_buffer);
        new SetCommand(_key, _value, _expiry, SetWhen.Exists, returnOld: true).Write(ref writer);
        return _buffer.Count;
    }

    [GlobalCleanup]
    public void Cleanup() => _buffer.Release();
}

/// <summary>Real public SET PX and ZRANGE REV WITHSCORES paths, singly and with eight commands in flight.</summary>
[MemoryDiagnoser]
public class RespFramingPublicBenchmarks
{
    private IContainer? _container;
    private RespireClient _client = null!;
    private RespireKey _setKey = "framing:set";
    private RespireKey _rangeKey = "framing:range";
    private RespireValue _value = "framing-value";
    private RespireExpiry _expiry = RespireExpiry.In(TimeSpan.FromMinutes(1));

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
        await _client.SortedSets.AddAsync(_rangeKey, "one", 1);
        await _client.SortedSets.AddAsync(_rangeKey, "two", 2);
        if (!await SetWithExpiry() || (await ReverseRangeWithScores()).Length != 2
            || await SetWithExpiryPipeline() != 8 || await ReverseRangeWithScoresPipeline() != 16)
            throw new InvalidOperationException("Public framing fixture did not preserve all replies.");
    }

    [Benchmark]
    public ValueTask<bool> SetWithExpiry() => _client.Strings.SetAsync(_setKey, _value, _expiry);

    [Benchmark]
    public ValueTask<SortedSetEntry[]> ReverseRangeWithScores()
        => _client.SortedSets.RangeWithScoresAsync(_rangeKey, 0, -1, descending: true);

    [Benchmark(OperationsPerInvoke = 8)]
    public async ValueTask<int> SetWithExpiryPipeline()
    {
        var first = SetWithExpiry();
        var second = SetWithExpiry();
        var third = SetWithExpiry();
        var fourth = SetWithExpiry();
        var fifth = SetWithExpiry();
        var sixth = SetWithExpiry();
        var seventh = SetWithExpiry();
        var eighth = SetWithExpiry();
        return (await first ? 1 : 0) + (await second ? 1 : 0) + (await third ? 1 : 0) + (await fourth ? 1 : 0)
            + (await fifth ? 1 : 0) + (await sixth ? 1 : 0) + (await seventh ? 1 : 0) + (await eighth ? 1 : 0);
    }

    [Benchmark(OperationsPerInvoke = 8)]
    public async ValueTask<int> ReverseRangeWithScoresPipeline()
    {
        var first = ReverseRangeWithScores();
        var second = ReverseRangeWithScores();
        var third = ReverseRangeWithScores();
        var fourth = ReverseRangeWithScores();
        var fifth = ReverseRangeWithScores();
        var sixth = ReverseRangeWithScores();
        var seventh = ReverseRangeWithScores();
        var eighth = ReverseRangeWithScores();
        return (await first).Length + (await second).Length + (await third).Length + (await fourth).Length
            + (await fifth).Length + (await sixth).Length + (await seventh).Length + (await eighth).Length;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
