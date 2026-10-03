using Microsoft.Extensions.Logging;

namespace Respire.Internal;

// Reusable discovery state for runtime failover. Configured endpoints are never evicted;
// learned peers are bounded, deduplicated by host/port, and copied before asynchronous work.
internal sealed partial class SentinelDiscoveryState
{
    internal const int MaximumDiscoveredEndpoints = 64;
    private readonly object _gate = new();
    private readonly List<RespireEndpoint> _endpoints = [];
    private readonly Dictionary<RespireEndpoint, long> _known = new(SentinelEndpointIdentity.EndpointComparer.Instance);
    private long _membershipVersion;
    private readonly int _configuredCount;
    // Observe raises the epoch floor before transport/ROLE validation. Commit advances the
    // accepted epoch only after validation. Failed validation never lowers either floor;
    // equal/missing epochs may only reuse the observed owner or one unambiguous address alias.
    private long? _acceptedEpoch;
    private RespireEndpoint? _observedPrimary;
    private long? _observedEpoch;
    private SentinelAddressEvidence _observedEvidence;
    private RespireEndpoint? _observedValidatedPeer;
    private int _missingEpochWarning;

    internal bool IsNewerConfiguration(long? epoch)
    {
        lock (_gate) return epoch is { } candidate && _acceptedEpoch is { } accepted && candidate > accepted;
    }

    internal bool IsCurrentConfiguration(RespireEndpoint primary, long? epoch)
    {
        lock (_gate) return IsCurrentConfigurationLocked(primary, epoch);
    }

    private bool IsCurrentConfigurationLocked(RespireEndpoint primary, long? epoch, string[]? addresses = null)
    {
        // Servers that never expose epochs retain ROLE/switch-evidence discovery. Once an
        // epoch is observed, a missing epoch cannot erase that ordering evidence.
        if (_observedEpoch is not { } observed) return true;
        if (epoch is { } candidate && candidate > observed) return true;
        return (epoch is null || epoch == observed) && _observedPrimary is { } current
            && (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(primary, current)
                || new SentinelAddressEvidence(primary, addresses).ConfirmsSameAddress(
                    _observedValidatedPeer is { } peer ? new(peer, null) : _observedEvidence));
    }

    internal bool TryObserveConfiguration(RespireEndpoint primary, long? epoch, string[]? addresses = null)
    {
        lock (_gate) return TryObserveConfigurationLocked(primary, epoch, addresses);
    }

    private bool TryObserveConfigurationLocked(RespireEndpoint primary, long? epoch, string[]? addresses)
    {
        if (!IsCurrentConfigurationLocked(primary, epoch, addresses)) return false;
        if (epoch is { } candidate && (_observedEpoch is null || candidate > _observedEpoch))
        {
            _observedEpoch = candidate;
            _observedPrimary = primary;
            _observedEvidence = new(primary, addresses);
            _observedValidatedPeer = null;
        }
        return true;
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
            if (!TryObserveConfigurationLocked(primary, epoch, addresses))
                throw new RespireConnectionException($"Sentinel configuration for {primary} was superseded during validation.");
            if (_observedEpoch is not null && validatedPeer is { } peer)
            {
                if (_observedValidatedPeer is { } acceptedPeer && !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(peer, acceptedPeer))
                    throw new RespireConnectionException($"Sentinel configuration for {primary} connected to a different owner at the same epoch.");
                // DNS only proposed candidates. ROLE established this physical owner, which
                // a numeric fallback can confirm even when the original DNS set was ambiguous.
                _observedValidatedPeer ??= peer;
            }
            _acceptedEpoch = epoch ?? _observedEpoch;
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
            _endpoints.RemoveAt(index);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return true;
    }

}
