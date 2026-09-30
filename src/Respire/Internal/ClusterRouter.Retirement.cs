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
        var poolDrain = retirement.DedicatedPool is { } pool ? RetirePoolAsync(pool) : Task.CompletedTask;
        try
        {
            try
            {
                await node.RetireAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (!_stopRetirement.IsCancellationRequested)
            {
                _logger?.LogDebug(error, "Cluster generation retirement needs correction cleanup at {Host}:{Port}", node.Host, node.Port);
            }

            var retryDelay = TimeSpan.FromSeconds(1);
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
                    _logger?.LogDebug(error, "Cluster fence retry failed at {Host}:{Port}; retrying in {DelaySeconds}s",
                        node.Host, node.Port, retryDelay.TotalSeconds);
                    await Task.Delay(retryDelay, _stopRetirement.Token).ConfigureAwait(false);
                    retryDelay = TimeSpan.FromSeconds(Math.Min(30, retryDelay.TotalSeconds * 2));
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
            _logger?.LogWarning(error, "Cluster generation retirement failed at {Host}:{Port}", node.Host, node.Port);
            retirement.Completion.TrySetException(error);
            return;
        }

        try
        {
            await poolDrain.ConfigureAwait(false);
            List<DedicatedConnectionPool> corrections;
            lock (_nodesGate) corrections = DetachCorrectionPoolsLocked(node);
            await Task.WhenAll(corrections.Select(RetirePoolAsync)).ConfigureAwait(false);
            lock (_nodesGate)
            {
                _retiringNodes.Remove(node);
                _identities.Forget(node);
            }
            retirement.Completion.TrySetResult();
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Cluster generation cleanup failed at {Host}:{Port}", node.Host, node.Port);
            retirement.Completion.TrySetException(error);
        }
    }

    private async Task RetirePoolAsync(DedicatedConnectionPool pool)
    {
        try
        {
            await pool.RetireAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "A retired Cluster pool reported a cleanup failure");
        }
        finally
        {
            lock (_nodesGate) _ownedPools.Remove(pool);
        }
    }

    internal Task WaitForRetirementAsync()
    {
        lock (_nodesGate) return Task.WhenAll(_retiringNodes.Values.Select(value => value.Completion.Task));
    }
}
