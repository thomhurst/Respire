using Microsoft.Extensions.Logging;
using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    // Lazily created only by Nearest. Tests can supply deterministic measurements before use.
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    private object? _nearestGate;

    private async ValueTask<Selection> GetNearestAsync(CancellationToken cancellationToken, bool retry = true)
    {
        var sampler = LazyInitializer.EnsureInitialized(ref NearestLatency, ref _nearestGate, static () => ReadLatencySampler.Create());
        if (Volatile.Read(ref _disposed) != 0)
        {
            await sampler.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }
        var endpoints = Volatile.Read(ref _replicas);
        try { endpoints = await GetReplicaEndpointsAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (IsNearestCandidateFailure(error, cancellationToken))
        {
            // Failed Sentinel discovery does not remove a usable primary or already-known replica.
        }
        Selection? primary = null;
        Exception? lastError = null;
        var primaryCandidate = Core.Multiplexer;
        if (sampler.CanConnect(primaryCandidate))
        {
            try
            {
                primary = await GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
                sampler.ConnectionSucceeded(primaryCandidate);
            }
            catch (Exception error) when (IsNearestCandidateFailure(error, cancellationToken))
            {
                lastError = error;
                sampler.ConnectionFailed(primaryCandidate);
            }
        }

        var best = new NearestReadSelection<Selection>();
        var start = (uint)Interlocked.Increment(ref _nextReplica);
        for (var offset = 0; offset <= endpoints.Length; offset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = (int)((start + (uint)offset) % (uint)(endpoints.Length + 1));
            Selection selection;
            if (index == 0)
            {
                if (primary is not { } selectedPrimary) continue;
                selection = selectedPrimary;
            }
            else
            {
                var entry = await GetCurrentReplicaEntryAsync(endpoints[index - 1]).ConfigureAwait(false);
                if (entry is null || entry.IsCoolingDown) continue;
                try
                {
                    selection = new(await entry.GetConnectionAsync(cancellationToken).ConfigureAwait(false), entry, null);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ObjectDisposedException)
                {
                    lastError = error;
                    entry.MarkFailed();
                    try { Core.Logger?.LogDebug(error, "Nearest read candidate unavailable at {Endpoint}", entry.Endpoint); }
                    catch (Exception) { }
                    continue;
                }
            }
            if (!selection.Connection.IsAcceptingCommands) continue;
            var latency = await sampler.GetLatencyAsync(selection.Connection, cancellationToken).ConfigureAwait(false);
            if (selection.Connection.IsAcceptingCommands && selection.Replica?.IsRoleEligible(selection.Connection) != false)
                best.Consider(selection, latency, selection.Replica?.IsReplicationLinkDown != true);
        }
        if (best.TryGet(out var selected))
        {
            if (selected.Connection.IsAcceptingCommands && (selected.Replica is { } replica
                    ? IsCurrent(replica) && replica.IsRoleEligible(selected.Connection)
                    : ReferenceEquals(selected.Primary, Core.Multiplexer))) return selected;
            if (retry) return await GetNearestAsync(cancellationToken, retry: false).ConfigureAwait(false);
        }
        throw new RespireConnectionException("No healthy eligible endpoint is available for Nearest reads.",
            lastError ?? new InvalidOperationException("The read topology changed during selection."));
    }

    // A connection's own connect/handshake deadline can surface as cancellation. Only the
    // caller's token stops selection; an unavailable candidate must not hide healthy peers.
    private static bool IsNearestCandidateFailure(Exception error, CancellationToken cancellationToken)
        => IsUnavailable(error, cancellationToken)
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private async ValueTask<RespireEndpoint[]> GetReplicaEndpointsAsync(CancellationToken cancellationToken)
    {
        var endpoints = Volatile.Read(ref _replicas);
        if (Core.Sentinel is { } sentinel && IsSentinelRefreshDue())
        {
            // Serve known replicas while refreshing in the background; wait only when none are known.
            if (endpoints.Length == 0)
            {
                await RefreshSentinelReplicasAsync(sentinel, cancellationToken).ConfigureAwait(false);
                endpoints = Volatile.Read(ref _replicas);
            }
            else if (Interlocked.CompareExchange(ref _backgroundRefresh, 1, 0) == 0)
                _ = RefreshSentinelReplicasInBackgroundAsync(sentinel);
        }
        return endpoints;
    }

    private async ValueTask<Entry?> GetCurrentReplicaEntryAsync(RespireEndpoint endpoint)
    {
        if (!_entries.TryGetValue(endpoint, out var entry))
        {
            entry = _entries.GetOrAdd(endpoint, static (value, router) => new Entry(value, router.Core, router), this);
            // Pairs with SetEndpoints so a late insertion sees the newer topology on recheck.
            Interlocked.MemoryBarrier();
        }
        if (!ContainsEndpoint(Volatile.Read(ref _replicas), endpoint))
        {
            if (_entries.TryRemove(new KeyValuePair<RespireEndpoint, Entry>(endpoint, entry)))
            {
                _retiring.TryAdd(entry, 0);
                _ = RetireAsync(entry);
            }
            return null;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            if (_entries.TryRemove(new KeyValuePair<RespireEndpoint, Entry>(endpoint, entry)))
                await entry.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }
        return entry;
    }
}
