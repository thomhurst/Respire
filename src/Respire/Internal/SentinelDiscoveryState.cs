using Microsoft.Extensions.Logging;

namespace Respire.Internal;

// Reusable discovery state for runtime failover. Configured endpoints are never evicted;
// learned peers are bounded, deduplicated by host/port, and copied before asynchronous work.
internal sealed partial class SentinelDiscoveryState
{
    internal const int MaximumDiscoveredEndpoints = 64;
    private readonly object _gate = new();
    private readonly List<RespireEndpoint> _endpoints = [];
    private readonly HashSet<RespireEndpoint> _known = new(EndpointComparer.Instance);
    private readonly int _configuredCount;
    // Observe raises the epoch floor before transport/ROLE validation. Commit advances the
    // accepted epoch only after validation. Failed validation never lowers either floor;
    // equal/missing epochs may only reuse the observed owner or one unambiguous address alias.
    private long? _acceptedEpoch;
    private RespireEndpoint? _observedPrimary;
    private long? _observedEpoch;
    private string[]? _observedAddresses;
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
            && (EndpointComparer.Instance.Equals(primary, current)
                || primary.Port == current.Port && SameAddress(primary, current, addresses));
    }

    private bool SameAddress(RespireEndpoint primary, RespireEndpoint current, string[]? addresses)
        => SingleAddress(primary, addresses) is { } candidate
            && SingleAddress(current, _observedAddresses) is { } observed
            && StringComparer.OrdinalIgnoreCase.Equals(candidate, observed);

    internal static string? SingleAddress(RespireEndpoint endpoint, string[]? addresses)
    {
        if (System.Net.IPAddress.TryParse(endpoint.Host, out var literal)) return SentinelResolver.NormalizeAddress(literal);
        if (addresses is not { Length: > 0 }) return null;
        var address = SentinelResolver.NormalizeHost(addresses[0]);
        for (var index = 1; index < addresses.Length; index++)
            if (!StringComparer.OrdinalIgnoreCase.Equals(address, SentinelResolver.NormalizeHost(addresses[index]))) return null;
        return address;
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
            _observedAddresses = addresses;
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
        catch { /* Diagnostic providers must not prevent failover. */ }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinel {Sentinel} did not provide a configuration epoch. Discovery relies on ROLE and switch evidence; any previously observed epoch remains enforced.")]
    private static partial void LogMissingEpoch(ILogger logger, RespireEndpoint sentinel);

    internal void AcceptConfiguration(RespireEndpoint primary, long? epoch, string[]? addresses = null)
    {
        lock (_gate)
        {
            if (!TryObserveConfigurationLocked(primary, epoch, addresses))
                throw new RespireConnectionException($"Sentinel configuration for {primary} was superseded during validation.");
            _acceptedEpoch = epoch ?? _observedEpoch;
        }
    }


    internal SentinelDiscoveryState(IEnumerable<RespireEndpoint> configured)
    {
        foreach (var endpoint in configured)
            if (_known.Add(endpoint)) _endpoints.Add(endpoint);
        _configuredCount = _known.Count;
    }

    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal RespireEndpoint[] Snapshot() { lock (_gate) return _endpoints.ToArray(); }

    // Returns the endpoints and a task that completes when a later TryAdd learns a new endpoint.
    // Endpoints are never removed, so consumers only need to react to additions.
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
            if (_known.Count - _configuredCount == MaximumDiscoveredEndpoints || !_known.Add(endpoint)) return false;
            _endpoints.Add(endpoint);
            changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        changed.TrySetResult();
        return true;
    }

    internal sealed class EndpointComparer : IEqualityComparer<RespireEndpoint>
    {
        internal static readonly EndpointComparer Instance = new();
        public bool Equals(RespireEndpoint x, RespireEndpoint y)
            => x.Port == y.Port && StringComparer.OrdinalIgnoreCase.Equals(
                SentinelResolver.NormalizeHost(x.Host), SentinelResolver.NormalizeHost(y.Host));
        public int GetHashCode(RespireEndpoint endpoint)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(SentinelResolver.NormalizeHost(endpoint.Host)), endpoint.Port);
    }
}
