namespace Respire.Internal;

// Endpoint identity uses EndpointComparer; Addresses are normalized DNS evidence for this
// source, not interchangeable owners. In particular, overlapping DNS sets do not prove identity.
internal readonly record struct SentinelSwitchSource(RespireEndpoint Endpoint, string[]? Addresses);

/// <summary>Advisory event evidence. Collection order never establishes failover chronology.</summary>
internal readonly record struct SentinelHint(
    string Key, RespireEndpoint[] Targets, SentinelSwitchSource[] Sources,
    RespireEndpoint[] Reporters, bool MustRediscover)
{
    internal static SentinelHint FromSwitchMaster(string key, RespireEndpoint? source,
        RespireEndpoint? target, RespireEndpoint reporter)
        => new(key, target is { } to ? [to] : [],
            source is { } from ? [new(from, null)] : [], [reporter], target is null);

    internal static SentinelHint FromDown(string key, RespireEndpoint reporter)
        => new(key, [], [], [reporter], true);

    internal static SentinelHint FromGap(RespireEndpoint reporter)
        => new("gap", [], [], [reporter], true);

    // Only one unambiguous target can satisfy the router's target-is-current shortcut.
    internal RespireEndpoint? Target
    {
        get
        {
            RespireEndpoint? result = null;
            foreach (var target in Targets)
            {
                var isSource = false;
                foreach (var source in Sources)
                    if (SentinelDiscoveryState.EndpointComparer.Instance.Equals(source.Endpoint, target)) { isSource = true; break; }
                if (isSource) continue;
                if (result is not null) return null;
                result = target;
            }
            return result;
        }
    }
    internal RespireEndpoint? OldPrimary => Sources.Length == 0 ? (RespireEndpoint?)null : Sources[0].Endpoint;
    internal RespireEndpoint? ReportingSentinel => Reporters.Length == 0 ? (RespireEndpoint?)null : Reporters[0];

    internal SentinelHint WithSourceAddresses(RespireEndpoint endpoint, string[] addresses)
        => this with { Sources = Sources.Select(source => SentinelDiscoveryState.EndpointComparer.Instance.Equals(source.Endpoint, endpoint)
            ? source with { Addresses = addresses } : source).ToArray() };
}

/// <summary>
/// Coalesces failover hints for the single notification rediscovery worker. At most one hint is
/// active (being discovered) and at most one is pending (waiting for the active attempt to end).
/// Hints never authorize a new primary: only rediscovery and successful ROLE validation can
/// publish one. This type accumulates evidence and does not own or publish primary generations.
/// </summary>
/// <remarks>Not thread-safe. The router calls every member while holding its gate.</remarks>
internal sealed class SentinelNotificationCoalescer
{
    private SentinelHint? _pending;
    private readonly HashSet<SourceResolution> _sourceResolutions = [];

    // A pending DNS lookup retains its switch evidence across worker completion and later
    // down/gap hints. Later targets remain visible so a failback is never retired by old DNS.
    internal sealed class SourceResolution(SentinelHint hint)
    {
        internal SentinelHint Hint = hint;
    }

    internal SourceResolution BeginSourceResolution(in SentinelHint hint)
    {
        var resolution = new SourceResolution(hint);
        _sourceResolutions.Add(resolution);
        return resolution;
    }

    internal void EndSourceResolution(SourceResolution resolution) => _sourceResolutions.Remove(resolution);

    /// <summary>The hint the worker is discovering, or null when no worker runs.</summary>
    internal SentinelHint? Active { get; private set; }

    /// <summary>The key of the hint the worker is discovering, or null when no worker runs.</summary>
    internal string? ActiveKey => Active?.Key;

    internal SentinelHint? Pending => _pending;

    /// <summary>
    /// Offers a hint. Returns true when no worker is running and the caller must start one;
    /// the hint is then active. Otherwise the hint is dropped or merged into the pending slot.
    /// </summary>
    /// <param name="hint">The hint to offer.</param>
    /// <param name="targetIsCurrent">Whether the hint's target is already the healthy current primary.</param>
    internal bool Offer(in SentinelHint hint, bool targetIsCurrent)
    {
        foreach (var resolution in _sourceResolutions) resolution.Hint = Merge(resolution.Hint, in hint);
        // State table: idle starts one worker; active coalesces duplicates; active+pending unions evidence.
        if (Active is null)
        {
            if (!hint.MustRediscover && targetIsCurrent) return false;
            _pending = null;
            Active = hint;
            return true;
        }
        var duplicate = ActiveKey == hint.Key || _pending?.Key == hint.Key;
        // A discovery already in flight can publish a different primary. Preserve even a
        // switch confirming Current so its source fence and reporter survive that result.
        var needsAnotherPass = hint.MustRediscover || HasNewReporter(in hint);
        if (duplicate && !needsAnotherPass && (_pending is null || _pending.Value.Key == hint.Key)) return false;
        var basis = _pending ?? (duplicate ? Active : null);
        _pending = Merge(basis, in hint);
        if (duplicate && needsAnotherPass) _pending = _pending.Value with { MustRediscover = true };
        return false;
    }

    private bool HasNewReporter(in SentinelHint hint)
    {
        foreach (var reporter in hint.Reporters)
            if (!IsKnownReporter(reporter)) return true;
        return false;
    }

    private bool IsKnownReporter(RespireEndpoint reporter)
        => ContainsReporter(Active, reporter) || ContainsReporter(_pending, reporter);

    private static bool ContainsReporter(SentinelHint? hint, RespireEndpoint reporter)
    {
        if (hint is not { } value) return false;
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        foreach (var endpoint in value.Reporters)
            if (comparer.Equals(endpoint, reporter)) return true;
        return false;
    }

    /// <summary>Unions event evidence without inferring chronology from targets, sources, or reporters.</summary>
    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
    {
        if (pending is not { } previous) return hint;
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        var targets = UnionEndpoints(previous.Targets, hint.Targets);
        var reporters = UnionEndpoints(previous.Reporters, hint.Reporters);
        var sources = new Dictionary<RespireEndpoint, string[]?>(comparer);
        foreach (var source in hint.Sources.Concat(previous.Sources))
        {
            if (!sources.TryGetValue(source.Endpoint, out var known)) sources.Add(source.Endpoint, source.Addresses);
            else if (source.Addresses is { } addresses)
                sources[source.Endpoint] = (known ?? []).Union(addresses, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        var mustRediscover = previous.MustRediscover || hint.MustRediscover
            || targets.Length > 1 || targets.Any(sources.ContainsKey);
        // Keep the switch key when a down/gap event contributes no source. This is only deduplication identity.
        var key = hint.Sources.Length > 0 || previous.Sources.Length == 0 ? hint.Key : previous.Key;
        return new(key, targets, sources.Select(pair => new SentinelSwitchSource(pair.Key, pair.Value)).ToArray(),
            reporters, mustRediscover);
    }

    /// <summary>Retains a completed DNS lookup for its source in active and pending hints.</summary>
    internal void RetainResolvedOldPrimaryAddresses(RespireEndpoint oldPrimary, string[] addresses)
    {
        if (Active is { } active) Active = active.WithSourceAddresses(oldPrimary, addresses);
        if (_pending is { } pending) _pending = pending.WithSourceAddresses(oldPrimary, addresses);
    }

    // Set union preserves first-seen reporter order without assigning event chronology.
    private static RespireEndpoint[] UnionEndpoints(
        IEnumerable<RespireEndpoint> first, IEnumerable<RespireEndpoint> second, RespireEndpoint? excluded = null)
    {
        var seen = new HashSet<RespireEndpoint>(SentinelDiscoveryState.EndpointComparer.Instance);
        if (excluded is { } endpoint) seen.Add(endpoint);
        var result = new List<RespireEndpoint>();
        Add(first);
        Add(second);
        return result.ToArray();

        void Add(IEnumerable<RespireEndpoint> endpoints)
        {
            foreach (var value in endpoints)
                if (seen.Add(value)) result.Add(value);
        }
    }

    /// <summary>Takes the pending hint and makes it active. Returns null when nothing is pending.</summary>
    /// <param name="activeFailed">
    /// Whether the active attempt failed. Its hint is then merged into the next one and marked
    /// must-rediscover, so a newer hint of a different kind cannot silently drop the failed one.
    /// </param>
    /// <param name="validatedPrimary">The primary validated by a successful active attempt, if any.</param>
    /// <param name="validatedPeer">The physical peer that answered ROLE for that primary.</param>
    internal SentinelHint? TakePending(bool activeFailed = false, RespireEndpoint? validatedPrimary = null,
        RespireEndpoint? validatedPeer = null)
    {
        if (_pending is not { } next)
        {
            if (Active is not { Reporters.Length: > 1 } active) return null;
            next = (activeFailed ? active : ForReporterReconciliation(active, validatedPrimary, validatedPeer)) with
            {
                MustRediscover = true,
                Reporters = active.Reporters[1..],
            };
            Active = next;
            return next;
        }
        if (Active is { } activeHint)
        {
            // Reconciliation preserves demoted sources and consumes only the validated primary's
            // source evidence, so an alternate reporter cannot retire that generation again.
            if (!activeFailed && (next.Key == activeHint.Key
                || next.Target is null && IsValidatedTarget(next, validatedPrimary, validatedPeer)))
                next = ForReporterReconciliation(next, validatedPrimary, validatedPeer);
            if (activeFailed)
            {
                var unqueriedReporters = next.Reporters
                    .Where(reporter => activeHint.ReportingSentinel is not { } activeReporter
                        || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(reporter, activeReporter)).ToArray();
                next = Merge(activeHint, in next) with { MustRediscover = true };
                if (unqueriedReporters.Length > 0)
                {
                    var reporters = UnionEndpoints(unqueriedReporters, next.Reporters, activeHint.ReportingSentinel);
                    next = next with
                    {
                        Reporters = reporters,
                    };
                }
            }
            else if (activeHint.Reporters.Length > 1)
            {
                var unqueriedReporters = activeHint.Reporters[1..];
                var unqueried = ForReporterReconciliation(activeHint, validatedPrimary, validatedPeer) with
                {
                    Reporters = unqueriedReporters,
                };
                next = Merge(unqueried, in next);
                next = next with { Reporters = UnionEndpoints(unqueriedReporters, next.Reporters) };
            }
        }
        _pending = null;
        Active = next;
        return next;
    }

    // A conflicting cycle has no unique target. A successful discovery can consume the
    // confirmed target's source fence, but never a source demoted toward one distinct target.
    private static bool IsValidatedTarget(SentinelHint hint, RespireEndpoint? primary, RespireEndpoint? peer)
    {
        if (primary is not { } endpoint) return false;
        foreach (var target in hint.Targets)
            if (MatchesValidatedSource(endpoint, peer, new(target, null))) return true;
        return false;
    }

    private static SentinelHint ForReporterReconciliation(SentinelHint hint, RespireEndpoint? validatedPrimary,
        RespireEndpoint? validatedPeer)
        => hint with
        {
            MustRediscover = true,
            // Successful validation consumes only that primary's source evidence. Keep every
            // other demotion fence: metadata-free reporters can still advertise a stale master.
            Sources = validatedPrimary is { } primary ? hint.Sources.Where(source =>
                !MatchesValidatedSource(primary, validatedPeer, source)).ToArray() : hint.Sources,
        };

    private static bool MatchesValidatedSource(RespireEndpoint primary, RespireEndpoint? peer, SentinelSwitchSource source)
    {
        if (SentinelDiscoveryState.EndpointComparer.Instance.Equals(primary, source.Endpoint)) return true;
        // Demotion matching is conservative, but consuming its fence requires unambiguous
        // identity. A DNS set containing several servers does not identify the validated one.
        return peer is { } validated && source.Endpoint.Port == validated.Port
            && SentinelDiscoveryState.SingleAddress(source.Endpoint, source.Addresses) is { } address
            && StringComparer.OrdinalIgnoreCase.Equals(address, SentinelResolver.NormalizeHost(validated.Host));
    }

    /// <summary>Discards superseded active evidence while retaining hints offered during its discovery.</summary>
    internal SentinelHint? SupersedeActive()
    {
        Active = _pending;
        _pending = null;
        return Active;
    }

    /// <summary>Ends the worker: no hint is active or pending.</summary>
    internal void Complete()
    {
        Active = null;
        _pending = null;
    }
}
