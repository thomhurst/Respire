using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Benchmarks;

/// <summary>Measures the normal selection and accepted-send paths against a live Redis server.</summary>
[MemoryDiagnoser]
public class TransportAcceptanceBenchmarks
{
    private const int BatchSize = 32;
    private readonly ValueTask<RespValue>[] _replies = new ValueTask<RespValue>[BatchSize];
    private static readonly RawCommand Ping = new("*1\r\n$4\r\nPING\r\n"u8.ToArray());
    private RespireConnectionMultiplexer _multiplexer = null!;

    [Params(1, 2)]
    public int Connections { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configured) ? configured : 6379;
        _multiplexer = await RespireConnectionMultiplexer.CreateAsync(host, port, Connections);
        await Pipeline();
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _multiplexer.DisposeAsync();

    [Benchmark]
    public object SelectConnection() => _multiplexer.GetConnection();

    [Benchmark(OperationsPerInvoke = BatchSize)]
    public async Task Pipeline()
    {
        for (var i = 0; i < BatchSize; i++) _replies[i] = _multiplexer.SendAsync(Ping);
        for (var i = 0; i < BatchSize; i++)
        {
            using var reply = await _replies[i];
            if (!reply.AsSpan().SequenceEqual("PONG"u8))
                throw new InvalidOperationException("Unexpected pipelined PING response.");
        }
    }
}
