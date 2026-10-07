using Microsoft.Extensions.Logging;

namespace Respire.Internal;

/// <summary>
/// Reusable discovery state for runtime failover. Configured endpoints are never evicted;
/// learned peers are bounded, deduplicated by host/port, and copied before asynchronous work.
/// </summary>
/// <remarks>
/// <para>
/// A learned endpoint is removed only after <see cref="MissedDiscoveriesBeforeRemoval"/> completed rounds omit it
/// <em>and</em> its last recorded connection attempt failed. A round counts only with at least one complete
/// <c>SENTINEL SENTINELS</c> reply; permission errors, malformed lists and caller-cancelled rounds supply no omission
/// evidence, though valid rows of a partially malformed list still add or refresh peers. Any reporter listing a peer,
/// or the peer reporting its own list, resets its missing count; a successful connection clears failure evidence.
/// There is no periodic discovery: aging advances only when primary discovery runs.
/// </para>
/// <para>
/// The omitted rounds are hysteresis; connection failure is a separate health gate, so one failed attempt can remove
/// a peer already omitted three times. A majority rule was rejected because it would block cleanup while most old
/// addresses are unreachable. Tradeoff: one partitioned reporter can supply omissions; healthy peers stay protected
/// by successful connections, while an unreachable peer may be removed and later rediscovered.
/// </para>
/// <para>
/// Each <see cref="DiscoveryRound"/> keeps a membership snapshot and report counters, so overlapping rounds cannot age
/// a peer refreshed by a newer report (a single per-peer round epoch would lose that evidence). The snapshot is bounded
/// by <see cref="MaximumDiscoveredEndpoints"/> and taken only during discovery, never on command routing.
/// <see cref="Membership"/> versions stop retired monitors changing a re-added endpoint's health. Removal signals the
/// monitor supervisor; configured seeds, the learned cap and accepted primary/epoch evidence are untouched by it.
/// </para>
/// </remarks>
internal sealed partial class SentinelDiscoveryState
{
    internal const int MaximumDiscoveredEndpoints = 64;
    internal const int MissedDiscoveriesBeforeRemoval = 3;
    private readonly Dictionary<RespireEndpoint, LearnedEndpoint> _learned = new(SentinelEndpointIdentity.EndpointComparer.Instance);

    private sealed class LearnedEndpoint
    {
        internal int MissedDiscoveries;
        internal long Reports;
        internal bool ConnectionFailed;
    }

    // Each resolution counts once, even when several Sentinels answer. A report from
    // an overlapping resolution protects that endpoint from older omission evidence.
    internal DiscoveryRound BeginDiscovery(CancellationToken cancellationToken = default)
    {
        lock (_gate) return new(this, cancellationToken);
    }

    internal sealed class DiscoveryRound : IDisposable
    {
        private readonly SentinelDiscoveryState _owner;
        private readonly CancellationToken _cancellationToken;
        private readonly (RespireEndpoint Endpoint, LearnedEndpoint State, long Reports)[] _initial;
        private bool _hasReport;

        internal DiscoveryRound(SentinelDiscoveryState owner, CancellationToken cancellationToken)
        {
            _owner = owner;
            _cancellationToken = cancellationToken;
            _initial = owner._learned.Select(pair => (pair.Key, pair.Value, pair.Value.Reports)).ToArray();
        }

        internal void Report(RespireEndpoint reporter, IEnumerable<RespireEndpoint> peers, bool complete = true)
        {
            lock (_owner._gate)
            {
                _hasReport |= complete;
                // SENTINEL SENTINELS excludes the reporting Sentinel itself.
                Refresh(reporter);
                foreach (var peer in peers) Refresh(peer);
            }
        }

        private void Refresh(RespireEndpoint endpoint)
        {
            if (!_owner._learned.TryGetValue(endpoint, out var learned)) return;
            learned.Reports++;
            learned.MissedDiscoveries = 0;
        }

        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (!_hasReport || _cancellationToken.IsCancellationRequested) return;
                _hasReport = false;
                foreach (var (endpoint, state, reports) in _initial)
                {
                    if (!_owner._learned.TryGetValue(endpoint, out var current)
                        || !ReferenceEquals(state, current) || state.Reports != reports) continue;
                    state.MissedDiscoveries = Math.Min(MissedDiscoveriesBeforeRemoval, state.MissedDiscoveries + 1);
                    if (state.ConnectionFailed && state.MissedDiscoveries == MissedDiscoveriesBeforeRemoval)
                        _owner.TryRemove(endpoint);
                }
            }
        }
    }

    internal void RecordConnection(Membership membership, bool succeeded)
    {
        lock (_gate)
        {
            if (!_known.TryGetValue(membership.Endpoint, out var version) || version != membership.Version
                || !_learned.TryGetValue(membership.Endpoint, out var learned)) return;
            learned.ConnectionFailed = !succeeded;
            if (!succeeded && learned.MissedDiscoveries == MissedDiscoveriesBeforeRemoval)
                TryRemove(membership.Endpoint);
        }
    }
    private readonly Lock _gate = new();
    private readonly List<RespireEndpoint> _endpoints = [];
    private readonly Dictionary<RespireEndpoint, long> _known = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private long _membershipVersion;
    private readonly int _configuredCount;
    // Observe raises the epoch floor before transport/ROLE validation. Commit advances the
    // accepted epoch only after validation. Failed validation never lowers either floor;
    // equal/missing epochs may only reuse the observed owner or one unambiguous address alias.
    private SentinelEpochEvidence _epochEvidence;
    private int _missingEpochWarning;

    internal SentinelEpochEvidence EpochEvidence
    {
        get { lock (_gate) return _epochEvidence; }
    }

    internal bool IsNewerConfiguration(long? epoch)
    {
        lock (_gate) return _epochEvidence.IsNewer(epoch);
    }

    internal bool IsCurrentConfiguration(RespireEndpoint primary, long? epoch)
    {
        lock (_gate) return _epochEvidence.IsCurrent(primary, epoch);
    }

    internal bool TryObserveConfiguration(RespireEndpoint primary, long? epoch, string[]? addresses = null)
    {
        lock (_gate)
        {
            var accepted = _epochEvidence.TryObserve(primary, epoch, addresses, out var next);
            _epochEvidence = next;
            return accepted;
        }
    }

    internal void WarnMissingEpoch(ILogger? logger, RespireEndpoint sentinel)
    {
        if (logger is null || Interlocked.Exchange(ref _missingEpochWarning, 1) != 0) return;
        try
        {
            LogMissingEpoch(logger, sentinel);
        }
        catch (Exception error) when (SentinelExceptionPolicy.IsRecoverable(error))
        { /* Diagnostic providers must not prevent failover. */ }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel {Sentinel} did not provide a configuration epoch. Discovery relies on ROLE and switch evidence; any previously observed epoch remains enforced.")]
    private static partial void LogMissingEpoch(ILogger logger, RespireEndpoint sentinel);

    internal void AcceptConfiguration(RespireEndpoint primary, long? epoch, string[]? addresses = null,
        RespireEndpoint? validatedPeer = null)
    {
        lock (_gate)
        {
            var rejection = _epochEvidence.Accept(primary, epoch, addresses, validatedPeer, out var next);
            _epochEvidence = next;
            if (rejection == SentinelConfigurationRejection.Superseded)
                throw new RespireConnectionException($"Sentinel configuration for {primary} was superseded during validation.");
            if (rejection == SentinelConfigurationRejection.DifferentPeer)
                throw new RespireConnectionException($"Sentinel configuration for {primary} connected to a different owner at the same epoch.");
        }
    }


    internal SentinelDiscoveryState(IEnumerable<RespireEndpoint> configured)
    {
        foreach (var endpoint in configured)
            if (_known.TryAdd(endpoint, _membershipVersion + 1))
            {
                _membershipVersion++;
                _endpoints.Add(endpoint);
            }
        _configuredCount = _known.Count;
    }

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RespireEndpoint[] Snapshot() { lock (_gate) return _endpoints.ToArray(); }

    internal readonly record struct Membership(RespireEndpoint Endpoint, long Version);

    // Versions preserve removal/re-addition even when notifications coalesce before
    // the supervisor reads its next snapshot. Only current memberships are retained.
    internal Membership[] MembershipSnapshot(out Task changed)
    {
        lock (_gate)
        {
            changed = _changed.Task;
            var result = new Membership[_endpoints.Count];
            for (var i = 0; i < result.Length; i++)
                result[i] = new(_endpoints[i], _known[_endpoints[i]]);
            return result;
        }
    }

    // Returns the endpoints and a task that completes after a membership change.
    internal RespireEndpoint[] Snapshot(out Task changed)
    {
        lock (_gate)
        {
            changed = _changed.Task;
            return _endpoints.ToArray();
        }
    }

    internal bool TryAdd(RespireEndpoint endpoint)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            if (_known.Count - _configuredCount == MaximumDiscoveredEndpoints
                || !_known.TryAdd(endpoint, _membershipVersion + 1)) return false;
            _membershipVersion++;
            _endpoints.Add(endpoint);
            _learned.Add(endpoint, new());
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return true;
    }

    // Configured endpoints occupy the immutable prefix. Removal only affects learned
    // membership; evidence about accepted owners and epochs remains intact.
    internal bool TryRemove(RespireEndpoint endpoint)
    {
        TaskCompletionSource changed;
        lock (_gate)
        {
            var index = _endpoints.FindIndex(_configuredCount,
                candidate => SentinelEndpointIdentity.EndpointComparer.Instance.Equals(candidate, endpoint));
            if (index < 0) return false;
            _known.Remove(_endpoints[index]);
            _learned.Remove(_endpoints[index]);
            _endpoints.RemoveAt(index);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return true;
    }

}
