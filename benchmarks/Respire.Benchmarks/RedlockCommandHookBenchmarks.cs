using BenchmarkDotNet.Attributes;
using Respire.Coordination;

namespace Respire.Benchmarks;

/// <summary>
/// Group lifecycle cost with and without command hooks. Three separately prefixed connections
/// share one Redis service to isolate client orchestration cost. This is a performance fixture,
/// not a fault-tolerant Redlock deployment or a measurement of cross-host network latency.
/// </summary>
[MemoryDiagnoser]
public class RedlockCommandHookBenchmarks
{
    private readonly List<RespireClient> _clients = [];
    private IRespireClient[] _nodes = [];
    private RespireRedlockGroup _group = null!;
    private RespireKey _key = "lease";
    private readonly TimeSpan _duration = TimeSpan.FromSeconds(30);

    [Params(false, true)]
    public bool Hooks { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        var prefix = $"redlock-bench:{Guid.NewGuid():N}:";
#pragma warning disable RESP010 // Three clients are created once in setup and reused by every invocation.
        for (var i = 0; i < 3; i++)
            _clients.Add(await RespireClient.ConnectAsync(new RespireOptions
            {
                Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                    int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
                Connections = 1,
                Protocol = RespireProtocol.Resp3,
                ThreadPoolMonitoring = false,
                CommandHook = Hooks ? new EmptyHook() : null,
            }));
#pragma warning restore RESP010
        _nodes = _clients.Select((client, i) => client.WithKeyPrefix($"{prefix}{i}:")).ToArray();
        _group = new RespireRedlockGroup(_nodes);
        // Resolve capability probes, scripts and initial connections outside measurement.
        await AcquireRelease();
        await AcquireRenewDispose();
        foreach (var node in _nodes)
            if (await node.Locks.GetOwnerTokenAsync(_key) is not null)
                throw new InvalidOperationException("Benchmark disposal left a lease behind.");
    }

    [Benchmark]
    public async Task<bool> AcquireRelease()
    {
        var attempt = await _group.TryAcquireAsync(_key, _duration);
        if (!attempt.Acquired) throw new InvalidOperationException("Uncontended quorum was not acquired.");
        var released = await attempt.Lock.ReleaseAsync();
        if (!released) throw new InvalidOperationException("Quorum release was not confirmed.");
        return released;
    }

    [Benchmark]
    public async Task<bool> AcquireRenewDispose()
    {
        await using var attempt = await _group.TryAcquireAsync(_key, _duration);
        if (!attempt.Acquired) throw new InvalidOperationException("Uncontended quorum was not acquired.");
        var renewed = await attempt.Lock.ResetExpiryAsync(_duration);
        if (!renewed) throw new InvalidOperationException("Quorum renewal was not confirmed.");
        return renewed;
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        try
        {
            foreach (var node in _nodes) await node.DeleteAsync(_key);
        }
        finally
        {
            foreach (var client in _clients) await client.DisposeAsync();
        }
    }

    private sealed class EmptyHook : RespireCommandHook
    {
        public override object? OnExecuting(RespireCommandContext context) => null;
        public override void OnCompleted(RespireCommandContext context, object? state,
            RespireCommandOutcome outcome, Exception? error) { }
    }
}
