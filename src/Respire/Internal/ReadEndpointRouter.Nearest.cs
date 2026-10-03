using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    // Lazily created only by Nearest. Tests can supply deterministic measurements before use.
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    private object? _nearestGate;

    private async ValueTask<Selection> GetNearestAsync(CancellationToken cancellationToken, bool retry = true,
        long? samplingDeadline = null, Exception? previousFailure = null, ReadAttempt? attempt = null)
    {
        var deadline = samplingDeadline ?? NearestReadSelection.CreateDeadline();
        var sampler = LazyInitializer.EnsureInitialized(ref NearestLatency, ref _nearestGate, static () => ReadLatencySampler.Create());
        if (Volatile.Read(ref _disposed) != 0)
        {
            await sampler.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }
        Selection? primary = null;
        Exception? lastError = previousFailure;
        var primaryCandidate = Core.Multiplexer;
        if (attempt?.ContainsFailure(primaryCandidate.ActiveConnectionEndpoint) != true && sampler.CanConnect(primaryCandidate))
        {
            try
            {
                primary = await GetPrimaryAsync(cancellationToken, attempt: attempt).ConfigureAwait(false);
                sampler.ConnectionSucceeded(primaryCandidate);
            }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
            {
                lastError = error;
                sampler.ConnectionFailed(primaryCandidate);
            }
        }

        var endpoints = Volatile.Read(ref _replicas);
        try { endpoints = await GetReplicaEndpointsAsync(cancellationToken, waitForUnknown: primary is null).ConfigureAwait(false); }
        catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
        {
            // Failed Sentinel discovery does not remove a usable primary or already-known replica.
            lastError = error;
        }
        var start = (uint)Interlocked.Increment(ref _nextReplica);
        var best = new NearestReadSelection<Selection>(start, endpoints.Length + 1);
        while (best.TryNext(out var index))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selection selection;
            if (index == 0)
            {
                if (primary is not { } selectedPrimary) continue;
                // The round-robin primary socket can have a pending probe while a sibling is idle. With no
                // idle sibling, keep that socket so its probe can still answer within the budget.
                selection = NearestReadSelection.AvoidPendingProbe(selectedPrimary.Primary!, selectedPrimary.Connection, sampler)
                    is { } idle ? selectedPrimary with { Connection = idle } : selectedPrimary;
            }
            else
            {
                if (attempt?.ContainsFailure(endpoints[index - 1]) == true) continue;
                var entry = await GetCurrentReplicaEntryAsync(endpoints[index - 1]).ConfigureAwait(false);
                if (entry is null || entry.IsCoolingDown) continue;
                try
                {
                    var connection = await entry.GetNearestConnectionAsync(sampler, cancellationToken).ConfigureAwait(false);
                    if (connection is null) continue;
                    selection = new(connection, entry, null);
                }
                catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not ObjectDisposedException)
                {
                    lastError = error;
                    TryRecordFailure(entry, error, "Nearest read candidate unavailable at {Endpoint}");
                    continue;
                }
            }
            if (!selection.Connection.IsAcceptingCommands) continue;
            var latency = sampler.GetLatencyAsync(selection.Connection, default, probeDeadline: deadline);
            if (selection.Connection.IsAcceptingCommands && selection.Replica?.IsRoleEligible(selection.Connection) != false)
                best.QueueSample(selection, latency, selection.Replica?.IsReplicationLinkDown != true);
        }
        // Start every eligible probe before waiting so a fast later candidate is visible even
        // when the first sample consumes the entire shared wait budget.
        using var samplingWait = best.HasPendingSamples
            ? NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken) : null;
        while (best.TryNextSample(out var pending))
        {
            var latency = await NearestReadSelection.GetLatencyAsync(pending.Latency, samplingWait, cancellationToken)
                .ConfigureAwait(false);
            var candidate = pending.Candidate;
            if (candidate.Connection.IsAcceptingCommands && candidate.Replica?.IsRoleEligible(candidate.Connection) != false)
                best.Consider(candidate, latency, candidate.Replica?.IsReplicationLinkDown != true, pending.Order);
        }
        if (best.TryGet(out var selected))
        {
            if (selected.Connection.IsAcceptingCommands && (selected.Replica is { } replica
                    ? IsCurrent(replica) && replica.IsRoleEligible(selected.Connection)
                    : ReferenceEquals(selected.Primary, Core.Multiplexer))) return selected;
            lastError ??= new RespireConnectionException("The read topology changed during selection.");
        }
        if (retry && Core.Sentinel is { } sentinel)
        {
            // A healthy cached candidate never waits for discovery. Once all candidates fail,
            // join a pending/due refresh before the one bounded retry. The same endpoint can
            // recover during that wait, so unchanged addresses do not suppress reselection.
            try { await RefreshSentinelReplicasAsync(sentinel, cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken)) { lastError = error; }
        }
        if (retry)
            return await GetNearestAsync(cancellationToken, retry: false, samplingDeadline: deadline,
                previousFailure: lastError, attempt: attempt).ConfigureAwait(false);
        if (attempt?.FirstFailure is { } original)
        {
            // Exclusions alone preserve the original exception. A later selection or
            // discovery failure remains visible alongside that acquisition failure.
            if (lastError is null || ReferenceEquals(lastError, original)) attempt.ThrowFirstFailure();
            lastError = new AggregateException(original, lastError);
        }
        throw new RespireConnectionException("No healthy eligible endpoint is available for Nearest reads.",
            lastError ?? new InvalidOperationException("The read topology changed during selection."));
    }

    private async ValueTask<RespireEndpoint[]> GetReplicaEndpointsAsync(CancellationToken cancellationToken,
        bool waitForUnknown = true)
    {
        var endpoints = Volatile.Read(ref _replicas);
        if (Core.Sentinel is { } sentinel && IsSentinelRefreshDue())
        {
            // Nearest can also serve its primary while the initial replica discovery runs.
            if (endpoints.Length == 0 && waitForUnknown)
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
            lock (_entriesGate)
            {
                ThrowIfDisposed();
                entry = _entries.GetOrAdd(endpoint, static (value, router) => new Entry(value, router.Core, router), this);
            }
            // The gate pairs insertion with publication and the terminal ownership snapshot.
        }
        if (!ContainsEndpoint(Volatile.Read(ref _replicas), endpoint))
        {
            RetireEntry(new(endpoint, entry));
            return null;
        }
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Leave ownership visible to the router and join the entry's shared cleanup.
            await entry.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }
        return entry;
    }
}
