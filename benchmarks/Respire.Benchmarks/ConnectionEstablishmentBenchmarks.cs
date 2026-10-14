using BenchmarkDotNet.Attributes;
using Respire.Commands;
using Respire.Networking;

namespace Respire.Benchmarks;

/// <summary>Ongoing coverage of unconfigured physical establishment and steady-state command costs.</summary>
[MemoryDiagnoser]
public class ConnectionEstablishmentBenchmarks
{
    private string _host = null!;
    private int _port;
    private RespireConnectionOptions _options = null!;
    private RespireConnection _connection = null!;
    private readonly RawCommand _ping = new("*1\r\n$4\r\nPING\r\n"u8.ToArray());

    [Params(RespProtocol.Resp2, RespProtocol.Resp3)]
    public RespProtocol Protocol { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        _port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var port) ? port : 6379;
        // This fixture is copied unchanged to the control revision, before hooks exist.
        _options = new RespireConnectionOptions { Protocol = Protocol };
        _connection = await RespireConnection.ConnectAsync(_host, _port, _options);
        if (await HealthyPing() != "PONG") throw new InvalidOperationException("Redis did not answer PING.");
    }

    [Benchmark]
    public async Task<bool> ConnectAndDispose()
    {
        await using var connection = await RespireConnection.ConnectAsync(_host, _port, _options);
        return connection.IsConnected;
    }

    [Benchmark]
    public async ValueTask<string?> HealthyPing()
    {
        using var reply = await _connection.SendAsync(_ping);
        return reply.AsString();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_connection is not null) await _connection.DisposeAsync();
    }
}
