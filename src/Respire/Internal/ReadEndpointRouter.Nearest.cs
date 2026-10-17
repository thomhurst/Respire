using Respire.Networking;

namespace Respire.Internal;

internal sealed partial class ReadEndpointRouter
{
    // Lazily created by Nearest or latency-band reads. Tests can inject deterministic measurements.
    internal ReadLatencySampler<RespireConnection>? NearestLatency;
    internal RespireReadRoutingSnapshot? LastReadSelection;
    // LazyInitializer's ref-object overload uses Monitor internally; do not replace with Lock.
    private object? _nearestGate;

    private async ValueTask<Selection> GetNearestAsync(CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool retry = true,
        long? samplingDeadline = null, Exception? previousFailure = null, ReadAttempt? attempt = null, bool readyOnly = false,
        RespireReadFrom readFrom = RespireReadFrom.Nearest, RespireEndpoint[]? replicaEndpoints = null,
        RespireConnection? excludedPeer = null)
    {
        var preferredZone = ReadFallbackPolicy.UsesAvailabilityZone(readFrom) ? core.Options.ClientAvailabilityZone : null;
        var deadline = samplingDeadline ?? NearestReadSelection.CreateDeadline();
        var sampler = LazyInitializer.EnsureInitialized(ref NearestLatency, ref _nearestGate, static () => ReadLatencySampler.Create());
        if (Volatile.Read(ref _disposed) != 0)
        {
            await sampler.DisposeAsync().ConfigureAwait(false);
            ThrowIfDisposed();
        }
        Selection? primary = null;
        Exception? lastError = previousFailure;
        if (readFrom == RespireReadFrom.Nearest)
        {
            var candidate = await GetNearestPrimaryAsync(sampler, cancellationToken, observation, preferredZone,
                attempt, readyOnly, excludedPeer).ConfigureAwait(false);
            primary = candidate.Primary;
            lastError = ConnectionFailures.Combine(lastError, candidate.Failure);
        }

        var endpoints = replicaEndpoints ?? Volatile.Read(ref _replicas);
        try
        {
            if (replicaEndpoints is null)
                endpoints = await GetReplicaEndpointsAsync(cancellationToken, waitForUnknown: primary is null, readyOnly: readyOnly).ConfigureAwait(false);
        }
        catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
        {
            RecordCandidateFailure(observation, error);
            // Failed Sentinel discovery does not remove a usable primary or already-known replica.
            lastError = ConnectionFailures.Combine(lastError, error);
        }
        var start = (uint)Interlocked.Increment(ref _nextReplica);
        try
        {
            var best = new NearestReadSelection<Selection>(start, endpoints.Length + 1, core.Options.ReplicaLatencyTolerance);
            while (best.TryNext(out var index))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Selection selection;
                if (index == 0)
                {
                    if (primary is not { } selectedPrimary) continue;
                    // The round-robin primary socket can have a pending probe while a sibling is idle. With no
                    // idle sibling, keep that socket so its probe can still answer within the budget.
                    selection = NearestReadSelection.AvoidPendingProbe(selectedPrimary.Primary!, selectedPrimary.Connection, sampler, preferredZone)
                        is { } idle ? selectedPrimary with { Connection = idle } : selectedPrimary;
                }
                else
                {
                    if (attempt?.ContainsFailure(endpoints[index - 1]) == true) continue;
                    if (excludedPeer is not null && HedgedReadPolicy.IsOriginalEndpoint(endpoints[index - 1], excludedPeer)) continue;
                    var entry = await GetCurrentReplicaEntryAsync(endpoints[index - 1]).ConfigureAwait(false);
                    if (entry is null || entry.IsCoolingDown) continue;
                    observation.UseEndpoint(entry.Endpoint, cancellationToken);
                    try
                    {
                        var connection = await entry.GetNearestConnectionAsync(sampler, cancellationToken, readyOnly, preferredZone).ConfigureAwait(false);
                        if (connection is null) continue;
                        if (excludedPeer is not null && !HedgedReadPolicy.IsDifferentPeer(excludedPeer, connection)) continue;
                        selection = new(connection, entry, null);
                    }
                    catch (BacklogSelectionRequiredException) when (readyOnly) { continue; }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is not (ObjectDisposedException or BacklogSelectionRequiredException))
                    {
                        RecordCandidateFailure(observation, error, entry.Endpoint);
                        lastError = ConnectionFailures.Combine(lastError, ConnectionFailures.AtEndpoint(error, entry.Endpoint));
                        TryRecordFailure(entry, error, nearest: true);
                        continue;
                    }
                }
                if (!selection.Connection.IsAcceptingCommands) continue;
                var latency = sampler.GetLatencyAsync(selection.Connection, default, probeDeadline: deadline);
                if (selection.Connection.IsAcceptingCommands && selection.Replica?.IsRoleEligible(selection.Connection) != false)
                    best.QueueSample(selection, latency, priority: ReadFallbackPolicy.LatencyPriority(readFrom,
                        selection.Replica is null, ReadFallbackPolicy.IsSameZone(selection.Connection, preferredZone),
                        selection.Replica?.IsReplicationLinkDown != true));
            }
            // Start every eligible probe before waiting so a fast later candidate is visible even
            // when the first sample consumes the entire shared wait budget.
            // A pending probe excludes only its own socket, not an already measured candidate.
            using var samplingWait = !readyOnly && best.HasPendingSamples
                ? NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken) : null;
            while (best.TryNextSample(out var pending))
            {
                var latency = await NearestReadSelection.GetLatencyAsync(pending.Latency, samplingWait, cancellationToken)
                    .ConfigureAwait(false);
                var candidate = pending.Candidate;
                if (candidate.Connection.IsAcceptingCommands && candidate.Replica?.IsRoleEligible(candidate.Connection) != false)
                    best.Consider(candidate, latency, order: pending.Order, priority: ReadFallbackPolicy.LatencyPriority(readFrom,
                        candidate.Replica is null, ReadFallbackPolicy.IsSameZone(candidate.Connection, preferredZone),
                        candidate.Replica?.IsReplicationLinkDown != true));
            }
            // The AZ policy must discover and sample preferred replicas before attempting a
            // potentially unavailable primary. A known remote primary cannot outrank any replica.
            if (!best.HasPreferredCandidate
                && ReadFallbackPolicy.ShouldProbeLocalPrimary(readFrom, Core.Multiplexer, preferredZone))
            {
                var candidate = await GetNearestPrimaryAsync(sampler, cancellationToken, observation, preferredZone,
                    attempt, readyOnly, excludedPeer).ConfigureAwait(false);
                lastError = ConnectionFailures.Combine(lastError, candidate.Failure);
                if (candidate.Primary is { } fallback)
                {
                    var connection = NearestReadSelection.AvoidPendingProbe(fallback.Primary!, fallback.Connection, sampler, preferredZone)
                        ?? fallback.Connection;
                    using var wait = !readyOnly ? NearestReadSelection.CreateWaitCancellation(deadline, cancellationToken) : null;
                    var latency = await NearestReadSelection.GetLatencyAsync(
                        sampler.GetLatencyAsync(connection, default, probeDeadline: deadline), wait, cancellationToken).ConfigureAwait(false);
                    if (connection.IsAcceptingCommands)
                        best.Consider(fallback with { Connection = connection }, latency, order: 0,
                            priority: ReadFallbackPolicy.LatencyPriority(readFrom, true,
                                ReadFallbackPolicy.IsSameZone(connection, preferredZone), true));
                }
            }
            if (best.TryGet(out var selected))
            {
                // An earlier sample can become stale while a later probe awaits. Retry the
                // current topology if any published band member is no longer eligible.
                if (IsCurrentNearestSelection(selected) && (core.Options.ReplicaLatencyTolerance is null
                    || best.AllBandCandidatesMatch(this, static (candidate, router) => router.IsCurrentNearestSelection(candidate)
                        && (candidate.Replica is not { } replica || ContainsEndpoint(Volatile.Read(ref router._replicas), replica.Endpoint)))))
                {
                    if (core.Options.ReplicaLatencyTolerance is not null)
                    {
                        var previous = Volatile.Read(ref LastReadSelection);
                        var snapshot = best.CaptureSnapshot(readFrom, null,
                            static value => new(value.Connection.Host, value.Connection.Port), previous);
                        if (!ReferenceEquals(previous, snapshot)) Volatile.Write(ref LastReadSelection, snapshot);
                    }
                    return selected;
                }
                lastError ??= new RespireConnectionException("The read topology changed during selection.");
            }
            if (readyOnly) throw new BacklogSelectionRequiredException();
            if (retry && Core.Sentinel is { } sentinel)
            {
                // A healthy cached candidate never waits for discovery. Once all candidates fail,
                // join a pending/due refresh before the one bounded retry. The same endpoint can
                // recover during that wait, so unchanged addresses do not suppress reselection.
                try { await RefreshSentinelReplicasAsync(sentinel, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
                {
                    RecordCandidateFailure(observation, error);
                    lastError = ConnectionFailures.Combine(lastError, error);
                }
            }
            if (retry)
                return await GetNearestAsync(cancellationToken, observation, retry: false, samplingDeadline: deadline,
                    previousFailure: lastError, attempt: attempt, readyOnly: readyOnly, readFrom: readFrom,
                    replicaEndpoints: replicaEndpoints, excludedPeer: excludedPeer).ConfigureAwait(false);
            if (attempt?.FirstFailure is { } original)
            {
                // Exclusions alone preserve the original exception. A later selection or
                // discovery failure remains visible alongside that acquisition failure.
                if (lastError is null || ReferenceEquals(lastError, original)) attempt.ThrowFirstFailure();
                lastError = ConnectionFailures.Combine(original, lastError);
            }
            throw new RespireConnectionException($"No healthy eligible endpoint is available for {readFrom} reads.",
                lastError ?? new InvalidOperationException("The read topology changed during selection.")) { HasAmbiguousEndpoint = true };
        }
        catch (BacklogSelectionRequiredException)
        {
            // Handoff retries selection; keep its unused ticket unless a peer advanced it.
            Interlocked.CompareExchange(ref _nextReplica, unchecked((int)start - 1), unchecked((int)start));
            throw;
        }
    }

    private bool IsCurrentNearestSelection(Selection selection)
        => selection.Connection.IsAcceptingCommands && (selection.Replica is { } replica
            ? IsCurrent(replica) && replica.IsRoleEligible(selection.Connection)
            : ReferenceEquals(selection.Primary, Core.Multiplexer));

    private async ValueTask<(Selection? Primary, Exception? Failure)> GetNearestPrimaryAsync(
        ReadLatencySampler<RespireConnection> sampler, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, string? preferredZone, ReadAttempt? attempt,
        bool readyOnly, RespireConnection? excludedPeer)
    {
        var candidate = Core.Multiplexer;
        if (attempt?.ContainsFailure(candidate.ActiveConnectionEndpoint) == true || !sampler.CanConnect(candidate)) return default;
        try
        {
            var primary = await GetPrimaryAsync(cancellationToken, observation, preferredZone, attempt, readyOnly).ConfigureAwait(false);
            sampler.ConnectionSucceeded(candidate);
            return excludedPeer is not null && !HedgedReadPolicy.IsDifferentPeer(excludedPeer, primary.Connection)
                ? default : (primary, null);
        }
        catch (BacklogSelectionRequiredException) when (readyOnly) { return default; }
        catch (Exception error) when (IsReadCandidateFailure(error, cancellationToken))
        {
            RecordCandidateFailure(observation, error, Core.Sentinel is null ? candidate.ActiveConnectionEndpoint : observation.Endpoint);
            sampler.ConnectionFailed(candidate);
            return (null, Core.Sentinel is null ? ConnectionFailures.AtEndpoint(error, candidate.ActiveConnectionEndpoint) : error);
        }
    }

    private async ValueTask<RespireEndpoint[]> GetReplicaEndpointsAsync(CancellationToken cancellationToken,
        bool waitForUnknown = true, bool readyOnly = false)
    {
        var endpoints = Volatile.Read(ref _replicas);
        if (Core.Sentinel is { } sentinel && IsSentinelRefreshDue())
        {
            // Nearest can also serve its primary while the initial replica discovery runs.
            if (endpoints.Length == 0 && waitForUnknown)
            {
                if (readyOnly) throw new BacklogSelectionRequiredException();
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
