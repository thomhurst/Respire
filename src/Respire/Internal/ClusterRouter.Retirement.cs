using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly Dictionary<RespireConnectionMultiplexer, RetiredGeneration> _retiringNodes = [];
    private readonly HashSet<DedicatedConnectionPool> _ownedPools = [];
    private readonly CancellationTokenSource _stopRetirement = new();

    private sealed class RetiredGeneration(RespireConnectionMultiplexer node, DedicatedConnectionPool? dedicatedPool)
    {
        internal readonly RespireConnectionMultiplexer Node = node;
        internal readonly DedicatedConnectionPool? DedicatedPool = dedicatedPool;
        internal readonly long StartedAt = Stopwatch.GetTimestamp();
        private bool _cleanupFailed;
        internal bool CleanupFailed => Volatile.Read(ref _cleanupFailed);
        internal void MarkCleanupFailed() => Volatile.Write(ref _cleanupFailed, true);
        internal readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private List<RetiredGeneration> DetachGenerationsLocked(RespireConnectionMultiplexer[] nodes)
    {
        List<RetiredGeneration> retirements = [];
        foreach (var node in nodes)
        {
            if (_retiringNodes.ContainsKey(node)) continue;
            _dedicatedPools.Remove(node, out var pool);
            _redirectVersions.Remove(node);
            if (_nodeStateHandlers.Remove(node, out var handler)) node.SlotStateChanged -= handler;
            var retirement = new RetiredGeneration(node, pool);
            _retiringNodes.Add(node, retirement);
            retirements.Add(retirement);
        }
        return retirements;
    }

    private async Task DrainGenerationAsync(RetiredGeneration retirement)
    {
        var node = retirement.Node;
        // Start both drains before awaiting either. A borrowed blocking lease remains
        // owned until it returns; explicit client disposal can abort both kinds of work.
        var poolDrain = retirement.DedicatedPool is { } pool ? DrainPoolAsync(pool) : Task.CompletedTask;
        try
        {
            try
            {
                await node.RetireAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (node.RetirementDrained && !_stopRetirement.IsCancellationRequested)
            {
                _logger?.LogDebug(error, "Cluster generation retirement needs correction cleanup at {Host}:{Port}", node.Host, node.Port);
            }

            const int maximumRetrySeconds = 30;
            var retrySeconds = 1;
            while (node.HasPendingCorrectionFences && !_stopRetirement.IsCancellationRequested)
            {
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(_stopRetirement.Token);
                    attempt.CancelAfter(_options.ConnectTimeout);
                    await node.FenceRetiredConnectionsAsync(attempt.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (!_stopRetirement.IsCancellationRequested)
                {
                    // An unacknowledged kill never releases generation ownership.
                    if (retrySeconds == maximumRetrySeconds)
                    {
                        int retained;
                        lock (_nodesGate) retained = _retiringNodes.Count;
                        _logger?.LogWarning(error,
                            "Cluster fence still unavailable at {Host}:{Port}; retrying in {DelaySeconds}s; {RetiringGenerationCount} generations remain in retirement",
                            node.Host, node.Port, retrySeconds, retained);
                    }
                    else
                        _logger?.LogDebug(error, "Cluster fence retry failed at {Host}:{Port}; retrying in {DelaySeconds}s",
                            node.Host, node.Port, retrySeconds);
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _stopRetirement.Token).ConfigureAwait(false);
                    retrySeconds = Math.Min(maximumRetrySeconds, retrySeconds * 2);
                }
            }
        }
        catch (OperationCanceledException) when (_stopRetirement.IsCancellationRequested)
        {
            // Explicit disposal cancels retries and has already started abortive cleanup.
        }
        catch (Exception error)
        {
            // Unexpected cleanup failure is not permission to forget an owed fence.
            retirement.MarkCleanupFailed();
            // Join the pool started above even when the node path failed first.
            Exception failure = error;
            try { await poolDrain.ConfigureAwait(false); }
            catch (Exception poolError) { failure = new AggregateException(error, poolError); }
            if (!_stopRetirement.IsCancellationRequested)
                _logger?.LogWarning(failure, "Cluster generation retirement failed at {Host}:{Port}", node.Host, node.Port);
            retirement.Completion.TrySetException(failure);
            return;
        }

        try
        {
            await poolDrain.ConfigureAwait(false);
            List<DedicatedConnectionPool> corrections;
            lock (_nodesGate) corrections = DetachCorrectionPoolsLocked(node);
            await Task.WhenAll(corrections.Select(DrainPoolAsync)).ConfigureAwait(false);
            lock (_nodesGate)
            {
                _retiringNodes.Remove(node);
                _identities.Forget(node);
            }
            retirement.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            retirement.MarkCleanupFailed();
            if (!_stopRetirement.IsCancellationRequested)
                _logger?.LogWarning(error, "Cluster generation cleanup failed at {Host}:{Port}", node.Host, node.Port);
            retirement.Completion.TrySetException(error);
        }
    }

    private async Task RetirePoolAsync(DedicatedConnectionPool pool)
    {
        try
        {
            await DrainPoolAsync(pool).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "A retired Cluster pool reported a cleanup failure");
        }
    }

    private async Task DrainPoolAsync(DedicatedConnectionPool pool)
    {
        await pool.RetireAsync().ConfigureAwait(false);
        // Failed cleanup stays owned so explicit client disposal can still visit it.
        lock (_nodesGate) _ownedPools.Remove(pool);
    }

    internal Task WaitForRetirementAsync()
    {
        lock (_nodesGate) return Task.WhenAll(_retiringNodes.Values.Select(value => value.Completion.Task));
    }
}
