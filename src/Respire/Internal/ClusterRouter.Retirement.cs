using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Respire.Infrastructure;

namespace Respire.Internal;

internal sealed partial class ClusterRouter
{
    private readonly Dictionary<RespireConnectionMultiplexer, RetiredGeneration> _retiringNodes = [];
    private readonly DedicatedPoolLedger _ownedPools;
    internal DedicatedPoolLedger OwnedPools => _ownedPools;
    private readonly CancellationTokenSource _stopRetirement = new();

    private sealed class RetiredGeneration(RespireConnectionMultiplexer node, DedicatedConnectionPool? dedicatedPool,
        MaintenanceNotificationHandler? maintenanceHandler)
    {
        internal readonly RespireConnectionMultiplexer Node = node;
        internal readonly DedicatedConnectionPool? DedicatedPool = dedicatedPool;
        internal readonly MaintenanceNotificationHandler? MaintenanceHandler = maintenanceHandler;
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
            if (_dedicatedMovingHandlers.Remove(node, out var movingHandler)) node.MovingHandoffPublished -= movingHandler;
            _redirectVersions.Remove(node);
            if (_nodeStateHandlers.Remove(node, out var handler)) node.SlotStateChanged -= handler;
            _nodeMaintenanceHandlers.Remove(node, out var maintenanceHandler);
            var retirement = new RetiredGeneration(node, pool, maintenanceHandler);
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
                _logger?.ClusterRetirementCorrectionNeeded(node.Host, node.Port, error);
            }
            finally
            {
                if (retirement.MaintenanceHandler is { } handler)
                    node.MaintenanceNotificationReceived -= handler;
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
                        if (_logger?.IsEnabled(LogLevel.Warning) == true)
                        {
                            int retained;
                            lock (_nodesGate) retained = _retiringNodes.Count;
                            _logger.ClusterFenceUnavailable(node.Host, node.Port, retrySeconds, retained, error);
                        }
                    }
                    else
                        _logger?.ClusterFenceRetry(node.Host, node.Port, retrySeconds, error);
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), _stopRetirement.Token).ConfigureAwait(false);
                    retrySeconds = Math.Min(maximumRetrySeconds, retrySeconds * 2);
                }
            }
        }
        catch (Exception error) when (_stopRetirement.IsCancellationRequested
            && error is (OperationCanceledException or RespireConnectionMultiplexer.CorrectionFenceDisposedException))
        {
            // Explicit disposal cancels retries and disposes owned nodes. Retirement can
            // race between its disposed check and entering the fence, so either shutdown
            // signal is expected here. Only the fence-entry guard emits the dedicated
            // disposal signal; generic disposal failures and pool failures still propagate.
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
                _logger?.ClusterRetirementFailed(node.Host, node.Port, failure);
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
                // Shutdown releases router ownership after abortive cleanup. Unacknowledged
                // fence IDs remain on the disposed node; this does not mark them successful.
                _retiringNodes.Remove(node);
                _identities.Forget(node);
            }
            retirement.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            retirement.MarkCleanupFailed();
            if (!_stopRetirement.IsCancellationRequested)
                _logger?.ClusterGenerationCleanupFailed(node.Host, node.Port, error);
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
            try { _logger?.ClusterRetiredPoolCleanupFailed(error); }
            catch { /* Background retirement remains observed even if logging fails. */ }
        }
    }

    private Task DrainPoolAsync(DedicatedConnectionPool pool) => _ownedPools.RetireAsync(pool);

    internal Task WaitForRetirementAsync()
    {
        lock (_nodesGate) return Task.WhenAll(_retiringNodes.Values.Select(value => value.Completion.Task));
    }
}
