using BenchmarkDotNet.Attributes;
using Respire.Coordination;

namespace Respire.Benchmarks;

/// <summary>Same-build lease overhead and a deterministic two-waiter FIFO handoff scenario.</summary>
[MemoryDiagnoser]
public class FairLockBenchmarks
{
    private RespireClient _holderClient = null!;
    private RespireClient _firstClient = null!;
    private RespireClient _secondClient = null!;
    private RespireClient _observer = null!;
    private RespireCoordination _coordination = null!;
    private RespireFairLock _holder = null!;
    private RespireFairLock _first = null!;
    private RespireFairLock _second = null!;
    private RespireKey _leaseKey;
    private RespireKey _counterKey;
    private RespireKey _queueKey;
    private readonly TimeSpan _duration = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

    [GlobalSetup]
    public async Task Setup()
    {
        var options = new RespireOptions
        {
            Endpoints = [new(Environment.GetEnvironmentVariable("REDIS_HOST") ?? "127.0.0.1",
                int.Parse(Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379"))],
            Connections = 1,
            Protocol = RespireProtocol.Resp3,
            ClientSideCache = new(),
        };
        _holderClient = await RespireClient.ConnectAsync(options);
        _firstClient = await RespireClient.ConnectAsync(options);
        _secondClient = await RespireClient.ConnectAsync(options);
        // Queue rendezvous must read Redis rather than a locally cached cardinality.
        _observer = await RespireClient.ConnectAsync(options with { ClientSideCache = null });
        var tag = Guid.NewGuid().ToString("N");
        _leaseKey = $"{{{tag}}}:lease";
        _counterKey = $"{{{tag}}}:counter";
        _queueKey = $"{{{tag}}}:queue";
        _coordination = new RespireCoordination(_holderClient);
        _holder = _coordination.CreateFairLock(_leaseKey, _counterKey, _queueKey);
        _first = new RespireCoordination(_firstClient).CreateFairLock(_leaseKey, _counterKey, _queueKey);
        _second = new RespireCoordination(_secondClient).CreateFairLock(_leaseKey, _counterKey, _queueKey);
        // Resolve scripts, capabilities and tracking before measurement.
        await FencedUncontended();
        await FairUncontended();
        await FairContendedFifoHandoff();
    }

    [Benchmark(Baseline = true)]
    public async Task<long> FencedUncontended()
    {
        await using var attempt = await _coordination.TryAcquireFencedLockAsync(_leaseKey, _counterKey, _duration);
        return attempt.Lock.FencingToken;
    }

    [Benchmark]
    public async Task<long> FairUncontended()
    {
        await using var lease = await _holder.AcquireAsync(_duration, _waitTimeout);
        return lease.FencingToken;
    }

    /// <summary>One operation is the whole three-owner scenario, including queue-observation round trips.</summary>
    [Benchmark]
    public async Task<long> FairContendedFifoHandoff()
    {
        using var timeout = new CancellationTokenSource(_waitTimeout);
        await using var holder = await _holder.AcquireAsync(_duration, _waitTimeout, timeout.Token);
        Task<RespireFencedLock>? first = null;
        Task<RespireFencedLock>? second = null;
        try
        {
            first = _first.AcquireAsync(_duration, _waitTimeout, timeout.Token).AsTask();
            await WaitForQueueAsync(1, timeout.Token);
            second = _second.AcquireAsync(_duration, _waitTimeout, timeout.Token).AsTask();
            await WaitForQueueAsync(2, timeout.Token);
            await holder.DisposeAsync();

            await using var firstLease = await first;
            if (second.IsCompleted || firstLease.FencingToken <= holder.FencingToken)
                throw new InvalidOperationException("FIFO handoff did not grant the first waiter exclusive ownership.");
            await firstLease.DisposeAsync();
            await using var secondLease = await second;
            if (secondLease.FencingToken <= firstLease.FencingToken)
                throw new InvalidOperationException("FIFO handoff did not advance the fencing token.");
            return secondLease.FencingToken;
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            await DrainAsync(second);
        }
    }

    private async Task WaitForQueueAsync(int waiters, CancellationToken cancellationToken)
    {
        // Queue layout: head + tail + one deadline per waiter + two links per adjacent pair = 3n.
        // This synchronization is part of the scenario measurement, not the fair-lock wait algorithm.
        while (await _observer.Hashes.CountAsync(_queueKey, cancellationToken) < 3 * waiters)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task DrainAsync(Task<RespireFencedLock>? pending)
    {
        if (pending is null) return;
        try { await (await pending).DisposeAsync(); }
        catch (Exception) { /* Preserve the scenario failure after best-effort owned lease cleanup. */ }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        // These keys belong only to this benchmark; production counters retain their history.
        await _observer.DeleteAsync(_leaseKey);
        await _observer.DeleteAsync(_counterKey);
        await _observer.DeleteAsync(_queueKey);
        await _secondClient.DisposeAsync();
        await _firstClient.DisposeAsync();
        await _holderClient.DisposeAsync();
        await _observer.DisposeAsync();
    }
}
