using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Respire.Internal;

namespace Respire.Benchmarks;

/// <summary>Prepared role selection and public replica reads against a real, replicated cluster.</summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class ClusterReplicaRoutingBenchmarks
{
    private readonly RespireKey _key = "cluster-replica:read";
    private readonly string _payload = new('x', 32);
    private readonly Task<string?>[] _reads = new Task<string?>[50];
    private readonly Process _process = Process.GetCurrentProcess();
    private RespireClient _client = null!;
    private IRespireClient _replica = null!;
    private int _slot;
    private int _replicaPort;
    private TimeSpan _cpuAt;
    private long _startedAt;
    private long _operations;
    private string? _operation;

    [GlobalSetup]
    public async Task Setup()
    {
        var host = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1";
        var port = int.TryParse(Environment.GetEnvironmentVariable("REDIS_PORT"), out var configured) ? configured : 6380;
        _replicaPort = int.TryParse(Environment.GetEnvironmentVariable("REDIS_REPLICA_PORT"), out configured) ? configured : 6381;
        _client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new(host, port)], Protocol = RespProtocol.Resp2,
            Connections = 1, ThreadPoolMonitoring = false, UseCluster = true,
        }).ConfigureAwait(false);
        _replica = _client.WithReadFrom(RespireReadFrom.Replica);
        _slot = _key.ClusterSlot;
        if (RespireTelemetry.IsOperationEnabled("GET"))
            throw new InvalidOperationException("Cluster replica benchmarks require uninstrumented command dispatch.");
        if (!await _client.Strings.SetAsync(_key, _payload).ConfigureAwait(false))
            throw new InvalidOperationException("Failed to initialize the cluster benchmark payload.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // Replication is asynchronous. Confirm the actual replica has this setup write before measuring reads.
        while (await _replica.GetStringAsync(_key, deadline.Token).ConfigureAwait(false) != _payload)
            await Task.Delay(10, deadline.Token).ConfigureAwait(false);
        for (var i = 0; i < 50; i++)
        {
            if (await _client.Strings.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || await _replica.GetStringAsync(_key).ConfigureAwait(false) != _payload
                || PreparedReplicaRoute() != _replicaPort || PreparedPrimaryRoute() != port)
                throw new InvalidOperationException("A cluster benchmark selected the wrong role or returned an unexpected reply.");
        }
        if ((await ReplicaStringConcurrent50().ConfigureAwait(false)).Any(value => value != _payload)
            || (await PrimaryStringConcurrent50().ConfigureAwait(false)).Any(value => value != _payload))
            throw new InvalidOperationException("A concurrent cluster read returned an unexpected reply.");
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
        return PreparedRoute(RespireReadFrom.Replica);
    }

    [Benchmark]
    public int PreparedPrimaryRoute()
    {
        Count(nameof(PreparedPrimaryRoute), 1);
        return PreparedRoute(RespireReadFrom.Primary);
    }

    private int PreparedRoute(RespireReadFrom policy)
    {
        var pending = _client.Core.Cluster!.GetReadConnectionAsync(_slot, policy, default);
        // Refresh timing remains unchanged. A healthy prepared read still completes inline
        // when periodic revalidation starts in the background.
        if (!pending.IsCompletedSuccessfully)
            throw new InvalidOperationException("Prepared cluster selection did not complete synchronously.");
        return pending.GetAwaiter().GetResult().Port;
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
        return _client.Strings.GetStringAsync(_key);
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
        for (var i = 0; i < _reads.Length; i++) _reads[i] = _client.Strings.GetStringAsync(_key).AsTask();
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
        // Keep raw counters for each launch. This includes warmup, calibration and completion
        // work, rather than isolating only measured iterations or server CPU.
        Console.WriteLine("CLUSTER_REPLICA_PROCESS_METRICS " + JsonSerializer.Serialize(new
        {
            operation = _operation, operations = _operations,
            cpuMilliseconds = (_process.TotalProcessorTime - _cpuAt).TotalMilliseconds,
            elapsedSeconds = Stopwatch.GetElapsedTime(_startedAt).TotalSeconds,
        }));
        try { if (_client is not null) await _client.DisposeAsync().ConfigureAwait(false); }
        finally { _process.Dispose(); }
    }
}
