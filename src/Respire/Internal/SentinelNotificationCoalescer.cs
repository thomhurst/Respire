namespace Respire.Internal;

/// <summary>A failover hint queued for notification-triggered Sentinel rediscovery.</summary>
/// <param name="Key">Deduplication identity. Identical keys coalesce while a discovery is active.</param>
/// <param name="Target">The endpoint the hint names as primary, or null for untargeted rediscovery.</param>
/// <param name="OldPrimary">
/// The endpoint a switch hint says lost the role. Only a hint whose source is the current
/// generation retires it before discovery.
/// </param>
/// <param name="MustRediscover">
/// The hint reports a fault or a delivery gap, so a discovery that started before it arrived cannot satisfy it.
/// </param>
/// <param name="OldPrimaryAddresses">Resolved addresses of a hostname <paramref name="OldPrimary"/>, if any.</param>
internal readonly record struct SentinelHint(
    string Key,
    RespireEndpoint? Target = null,
    RespireEndpoint? OldPrimary = null,
    bool MustRediscover = false,
    string[]? OldPrimaryAddresses = null);

/// <summary>
/// Coalesces failover hints for the single notification rediscovery worker. At most one hint is
/// active (being discovered) and at most one is pending (waiting for the active attempt to end).
/// </summary>
/// <remarks>Not thread-safe. The router calls every member while holding its gate.</remarks>
internal sealed class SentinelNotificationCoalescer
{
    private SentinelHint? _pending;

    /// <summary>The key of the hint the worker is discovering, or null when no worker runs.</summary>
    internal string? ActiveKey { get; private set; }

    internal SentinelHint? Pending => _pending;

    /// <summary>
    /// Offers a hint. Returns true when no worker is running and the caller must start one;
    /// the hint is then active. Otherwise the hint is dropped or merged into the pending slot.
    /// </summary>
    /// <param name="hint">The hint to offer.</param>
    /// <param name="targetIsCurrent">Whether the hint's target is already the healthy current primary.</param>
    internal bool Offer(in SentinelHint hint, bool targetIsCurrent)
    {
        if (ActiveKey == hint.Key || _pending?.Key == hint.Key)
        {
            // Duplicates coalesce. A fault report must still outlive the active attempt, because
            // that attempt may have queried Sentinel before the fault happened, or may fail.
            if (hint.MustRediscover)
                _pending = _pending is { } pending ? pending with { MustRediscover = true } : hint;
            return false;
        }
        if (!hint.MustRediscover && targetIsCurrent) return false;
        if (ActiveKey is not null)
        {
            _pending = Merge(_pending, hint);
            return false;
        }
        _pending = null;
        ActiveKey = hint.Key;
        return true;
    }

    /// <summary>
    /// Merges a hint into the pending slot. A hint that names a switch source replaces a pending
    /// hint without one, keeping the earlier target when it has none. A pending switch is never
    /// replaced by a hint without a source, so a later down event cannot erase its retirement.
    /// Fault flags accumulate.
    /// </summary>
    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
    {
        if (pending is not { } previous) return hint;
        var mustRediscover = previous.MustRediscover || hint.MustRediscover;
        return hint.OldPrimary is not null || previous.OldPrimary is null
            ? hint with { Target = hint.Target ?? previous.Target, MustRediscover = mustRediscover }
            : previous with { MustRediscover = mustRediscover };
    }

    /// <summary>Takes the pending hint and makes it active. Returns null when nothing is pending.</summary>
    internal SentinelHint? TakePending()
    {
        if (_pending is not { } next) return null;
        _pending = null;
        ActiveKey = next.Key;
        return next;
    }

    /// <summary>Ends the worker: no hint is active or pending.</summary>
    internal void Complete()
    {
        ActiveKey = null;
        _pending = null;
    }
}
