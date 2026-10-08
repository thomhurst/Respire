using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

/// <summary>Validated standalone replica routes and actual reads, with primary controls.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class StandaloneReplicaRoutingBenchmarks
{
    private readonly RespireKey _key = "standalone-replica:read";
    private readonly string _payload = new('x', 32);
    private readonly Task<string?>[] _reads = new Task<string?>[50];
    private readonly Process _process = Process.GetCurrentProcess();
    private RespireClient _client = null!;
    private IRespireClient _replica = null!;
    private int _primaryPort;
    private int _replicaPort;
    private TimeSpan _cpuAt;
    private long _startedAt;
    private long _operations;
    private string? _operation;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        _primaryPort = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var port) ? port : 6380;
        _replicaPort = int.TryParse(Environment.GetEnvironmentVariable("REDIS_REPLICA_PORT"), out port) ? port : 6381;
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, _primaryPort)], ReplicaEndpoints = [new(host, _replicaPort)],
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            // These cases isolate prepared reads. Expiry/revalidation is covered by routing tests.
            ReplicaRefreshInterval = TimeSpan.FromHours(1),
        }).ConfigureAwait(false);
        _replica = _client.WithReadFrom(RespireReadFrom.Replica);
        if (RespireTelemetry.IsOperationEnabled("GET"))
            throw new InvalidOperationException("Standalone replica benchmarks require uninstrumented dispatch.");
        if (!await _client.SetAsync(_key, _payload).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to initialize the replica benchmark payload.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await _replica.GetStringAsync(_key, deadline.Token).ConfigureAwait(false) != _payload)
            await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        for (var i = 0; i < 50; i++)
            if (await _client.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || await _replica.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || PreparedReplicaRoute() != _replicaPort || PreparedPrimaryRoute() != _primaryPort)
                throw new InvalidOperationException("A standalone read selected the wrong role or returned a wrong reply.");
        if ((await ReplicaStringConcurrent50().ConfigureAwait(false)).Any(value => value != _payload)
            || (await PrimaryStringConcurrent50().ConfigureAwait(false)).Any(value => value != _payload))
            throw new InvalidOperationException("A concurrent standalone read returned a wrong reply.");
        _operation = null;
        _operations = 0;
        _process.Refresh();
        _cpuAt = _process.TotalProcessorTime;
        _startedAt = Stopwatch.GetTimestamp();
    }

    [Benchmark]
    public int PreparedReplicaRoute()
    {
        Count(nameof(PreparedReplicaRoute), 1);
        return PreparedRoute(RespireReadFrom.Replica, _replicaPort);
    }

    [Benchmark]
    public int PreparedPrimaryRoute()
    {
        Count(nameof(PreparedPrimaryRoute), 1);
        return PreparedRoute(RespireReadFrom.Primary, _primaryPort);
    }

    private int PreparedRoute(RespireReadFrom policy, int expectedPort)
    {
        var pending = _client.Core.ReadRouter.GetConnectionAsync(policy, default);
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("A prepared standalone selection did not complete synchronously.");
        var connection = pending.GetAwaiter().GetResult();
        if (connection.Port != expectedPort)
            throw new InvalidOperationException("A prepared standalone selection chose the wrong role.");
        return connection.Port;
    }

    [Benchmark]
    public ValueTask<string?> ReplicaStringSingle()
    {
        Count(nameof(ReplicaStringSingle), 1);
        return _replica.GetStringAsync(_key);
    }

    [Benchmark]
    public ValueTask<string?> PrimaryStringSingle()
    {
        Count(nameof(PrimaryStringSingle), 1);
        return _client.GetStringAsync(_key);
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public Task<string?[]> ReplicaStringConcurrent50()
    {
        Count(nameof(ReplicaStringConcurrent50), 50);
        for (var i = 0; i < _reads.Length; i++) _reads[i] = _replica.GetStringAsync(_key).AsTask();
        return Task.WhenAll(_reads);
    }

    [Benchmark(OperationsPerInvoke = 50)]
    public Task<string?[]> PrimaryStringConcurrent50()
    {
        Count(nameof(PrimaryStringConcurrent50), 50);
        for (var i = 0; i < _reads.Length; i++) _reads[i] = _client.GetStringAsync(_key).AsTask();
        return Task.WhenAll(_reads);
    }

    private void Count(string operation, int count)
    {
        _operation = operation;
        _operations += count;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _process.Refresh();
        // Raw per-launch counters include calibration, warmup and completion work, not server CPU.
        Console.WriteLine("STANDALONE_REPLICA_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            operation = _operation, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
        }));
        try { if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }
}
