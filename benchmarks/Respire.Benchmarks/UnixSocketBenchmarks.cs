using BenchmarkDotNet.Attributes;

namespace Respire.Benchmarks;

/// <summary>Compares sequential GET latency over TCP loopback and a Unix socket to the same local Redis.</summary>
[MemoryDiagnoser]
public class UnixSocketBenchmarks
{
    private RespireClient _tcp = null!;
    private RespireClient _unix = null!;
    private readonly string _key = "respire:uds-benchmark:" + Guid.NewGuid().ToString("N");

    [GlobalSetup]
    public async Task Setup()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Run this transport comparison on the Linux benchmark runner.");
        var socketPath = Environment.GetEnvironmentVariable("RESPIRE_UNIX_SOCKET")
            ?? throw new InvalidOperationException("RESPIRE_UNIX_SOCKET must identify the benchmark Redis socket.");
        var tcpConnection = Environment.GetEnvironmentVariable("RESPIRE_TCP_CONNECTION") ?? "127.0.0.1:6379";
        try
        {
            _tcp = await RespireClient.ConnectAsync(tcpConnection);
            _unix = await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [RespireEndpoint.UnixSocket(socketPath)],
            });
            var value = new string('x', 128);
            await _tcp.SetAsync(_key, value);
            if (await _unix.GetStringAsync(_key) != value)
                throw new InvalidOperationException("TCP and Unix socket endpoints must reach the same Redis database.");
        }
        catch
        {
            await Cleanup();
            throw;
        }
    }

    [Benchmark(Baseline = true)]
    public ValueTask<string?> TcpLoopbackGet() => _tcp.GetStringAsync(_key);

    [Benchmark]
    public ValueTask<string?> UnixSocketGet() => _unix.GetStringAsync(_key);

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_unix is not null) await _unix.DisposeAsync();
        if (_tcp is not null) await _tcp.DisposeAsync();
    }
}
