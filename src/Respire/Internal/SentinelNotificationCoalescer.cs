namespace Respire.Internal;

internal readonly record struct SentinelSwitchSource(RespireEndpoint Endpoint, string[]? Addresses);

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
/// <param name="AdditionalSources">Other switch sources paired with their resolved addresses.</param>
/// <param name="AdditionalTargets">Other announced targets retained while pending hints merge.</param>
/// <param name="ReportingSentinel">The Sentinel that delivered the switch hint.</param>
/// <param name="AdditionalReportingSentinels">Other reporting Sentinels retained during coalescing.</param>
internal readonly record struct SentinelHint(
    string Key,
    RespireEndpoint? Target = null,
    RespireEndpoint? OldPrimary = null,
    bool MustRediscover = false,
    string[]? OldPrimaryAddresses = null,
    SentinelSwitchSource[]? AdditionalSources = null,
    RespireEndpoint? ReportingSentinel = null,
    RespireEndpoint[]? AdditionalTargets = null,
    RespireEndpoint[]? AdditionalReportingSentinels = null);

/// <summary>
/// Coalesces failover hints for the single notification rediscovery worker. At most one hint is
/// active (being discovered) and at most one is pending (waiting for the active attempt to end).
/// </summary>
/// <remarks>Not thread-safe. The router calls every member while holding its gate.</remarks>
internal sealed class SentinelNotificationCoalescer
{
    private SentinelHint? _pending;

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
        if (ActiveKey == hint.Key && _pending is { } intervening && intervening.Key != hint.Key)
        {
            _pending = Merge(intervening, in hint);
            return false;
        }
        if (ActiveKey == hint.Key || _pending?.Key == hint.Key)
        {
            // Duplicates coalesce. A fault report must still outlive the active attempt, because
            // that attempt may have queried Sentinel before the fault happened, or may fail.
            var newReporter = HasNewReporter(in hint);
            if (hint.MustRediscover || newReporter)
                _pending = _pending is { } pending
                    ? Merge(pending, in hint) with { MustRediscover = pending.MustRediscover || hint.MustRediscover || newReporter }
                    : Active is { } active ? Merge(active, in hint) with { MustRediscover = active.MustRediscover || hint.MustRediscover || newReporter } : hint;
            return false;
        }
        if (!hint.MustRediscover && targetIsCurrent) return false;
        if (ActiveKey is not null)
        {
            _pending = Merge(_pending, hint);
            return false;
        }
        _pending = null;
        Active = hint;
        return true;
    }

    private bool HasNewReporter(in SentinelHint hint)
    {
        if (hint.ReportingSentinel is { } reporter && !IsKnownReporter(reporter)) return true;
        if (hint.AdditionalReportingSentinels is { } reporters)
            foreach (var additional in reporters)
                if (!IsKnownReporter(additional)) return true;
        return false;
    }

    private bool IsKnownReporter(RespireEndpoint reporter)
        => ContainsReporter(Active, reporter) || ContainsReporter(_pending, reporter);

    private static bool ContainsReporter(SentinelHint? hint, RespireEndpoint reporter)
    {
        if (hint is not { } value) return false;
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        if (value.ReportingSentinel is { } first && comparer.Equals(first, reporter)) return true;
        if (value.AdditionalReportingSentinels is { } additional)
            foreach (var endpoint in additional)
                if (comparer.Equals(endpoint, reporter)) return true;
        return false;
    }

    /// <summary>
    /// Merges a hint into the pending slot. A hint that names a switch source replaces a pending
    /// hint without one, keeping the earlier target when it has none, unless the later hint names
    /// that target as its own switch source. A pending switch is never
    /// replaced by a hint without a source, so a later down event cannot erase its retirement.
    /// Fault flags accumulate, and two different targets always require fresh discovery.
    /// </summary>
    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
    {
        if (pending is not { } previous) return hint;
        // Two Sentinels can deliver one failover sequence in different orders, so arrival order
        // cannot say which of two different targets is newer. Neither may end the worker early
        // through the target-is-current shortcut; fresh discovery decides between them.
        var conflictingTargets = previous.Target is { } previousTarget && hint.Target is { } hintTarget
            && !SentinelDiscoveryState.EndpointComparer.Instance.Equals(previousTarget, hintTarget);
        var mustRediscover = previous.MustRediscover || hint.MustRediscover || conflictingTargets
            || hint.OldPrimary is not null && hint.Target is null;
        var merged = hint.OldPrimary is not null || previous.OldPrimary is null
            ? hint with
            {
                // A later switch away from the earlier target supersedes it rather than inheriting it.
                Target = hint.Target ?? (previous.Target is { } target && hint.OldPrimary is { } source
                    && SentinelDiscoveryState.EndpointComparer.Instance.Equals(target, source) ? null : previous.Target),
                MustRediscover = mustRediscover,
            }
            : previous with { MustRediscover = mustRediscover };
        var sources = new Dictionary<RespireEndpoint, string[]?>(SentinelDiscoveryState.EndpointComparer.Instance);
        AddSources(previous);
        AddSources(hint);
        var selectedAddresses = merged.OldPrimary is { } selected
            ? sources.GetValueOrDefault(selected)
            : null;
        var additionalSources = new List<SentinelSwitchSource>();
        foreach (var (endpoint, addresses) in sources)
            if (merged.OldPrimary is not { } primary || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(endpoint, primary))
                additionalSources.Add(new(endpoint, addresses));
        var targets = UnionEndpoints(EnumerateTargets(previous), EnumerateTargets(hint));
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        RespireEndpoint? selectedTarget = null;
        // Arrival order cannot establish chronology. A target that is also a retained switch
        // source is ambiguous; let fresh discovery choose instead of promoting it by edge shape.
        if (merged.Target is { } candidate && !sources.ContainsKey(candidate))
            selectedTarget = candidate;
        else if (previous.Target is { } priorCandidate && !sources.ContainsKey(priorCandidate)) selectedTarget = priorCandidate;
        else selectedTarget = targets.Where(target => !sources.ContainsKey(target))
            .Select(static target => (RespireEndpoint?)target).FirstOrDefault();
        RespireEndpoint? reportingSentinel = null;
        var previousTargetMatches = previous.Target is { } previousTargetForReporter
            && selectedTarget is { } targetForReporter
            && comparer.Equals(targetForReporter, previousTargetForReporter);
        if (selectedTarget is { } survivingTarget)
        {
            if (hint.Target is { } reportedTarget && comparer.Equals(survivingTarget, reportedTarget))
                reportingSentinel = hint.ReportingSentinel
                    ?? (previousTargetMatches ? previous.ReportingSentinel : null);
            else if (previousTargetMatches)
                reportingSentinel = previous.ReportingSentinel;
        }
        var reporters = UnionEndpoints(EnumerateReportingSentinels(previous), EnumerateReportingSentinels(hint));
        if (reportingSentinel is null && reporters.Length > 0) reportingSentinel = reporters[0];
        return merged with
        {
            MustRediscover = mustRediscover || targets.Any(sources.ContainsKey),
            Target = selectedTarget,
            AdditionalSources = additionalSources.Count == 0 ? null : additionalSources.ToArray(),
            OldPrimaryAddresses = selectedAddresses,
            ReportingSentinel = reportingSentinel,
            AdditionalReportingSentinels = reporters.Where(reporter => reportingSentinel is not { } selected
                || !comparer.Equals(reporter, selected)).ToArray() is { Length: > 0 } additionalReporters
                ? additionalReporters : null,
            AdditionalTargets = targets.Where(target => selectedTarget is not { } primaryTarget
                || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(target, primaryTarget))
                .Where(target => !sources.ContainsKey(target))
                .ToArray() is { Length: > 0 } extraTargets
                ? extraTargets : null,
        };

        void AddSources(SentinelHint value)
        {
            foreach (var source in EnumerateOldPrimaries(value))
                if (!sources.TryGetValue(source.Endpoint, out var addresses) || addresses is null)
                    sources[source.Endpoint] = source.Addresses;
        }
    }

    /// <summary>Retains a completed DNS lookup for its source in active and pending hints.</summary>
    internal void RetainResolvedOldPrimaryAddresses(RespireEndpoint oldPrimary, string[] addresses)
    {
        if (Active is { } active) Active = AddResolvedAddresses(active, oldPrimary, addresses);
        if (_pending is { } pending) _pending = AddResolvedAddresses(pending, oldPrimary, addresses);
    }

    private static SentinelHint AddResolvedAddresses(SentinelHint hint, RespireEndpoint oldPrimary, string[] addresses)
    {
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        if (hint.OldPrimary is { } primary && comparer.Equals(primary, oldPrimary))
            return hint with { OldPrimaryAddresses = addresses };
        if (hint.AdditionalSources is not { } additional) return hint;
        for (var i = 0; i < additional.Length; i++)
        {
            if (!comparer.Equals(additional[i].Endpoint, oldPrimary)) continue;
            var sources = additional.ToArray();
            sources[i] = new(oldPrimary, addresses);
            return hint with { AdditionalSources = sources };
        }
        return hint;
    }

    private static IEnumerable<RespireEndpoint> EnumerateTargets(SentinelHint hint)
    {
        if (hint.Target is { } target) yield return target;
        if (hint.AdditionalTargets is { } additional)
            foreach (var endpoint in additional) yield return endpoint;
    }

    private static IEnumerable<SentinelSwitchSource> EnumerateOldPrimaries(SentinelHint hint)
    {
        if (hint.OldPrimary is { } oldPrimary) yield return new(oldPrimary, hint.OldPrimaryAddresses);
        if (hint.AdditionalSources is { } additional)
            foreach (var source in additional) yield return source;
    }

    private static IEnumerable<RespireEndpoint> EnumerateReportingSentinels(SentinelHint hint)
    {
        if (hint.ReportingSentinel is { } reporter) yield return reporter;
        if (hint.AdditionalReportingSentinels is { } additional)
            foreach (var endpoint in additional) yield return endpoint;
    }

    private static SentinelHint PrioritizeReportingSentinels(
        SentinelHint hint,
        IEnumerable<RespireEndpoint> prioritized)
    {
        var reporters = UnionEndpoints(prioritized, EnumerateReportingSentinels(hint));
        return hint with
        {
            ReportingSentinel = reporters[0],
            AdditionalReportingSentinels = reporters.Length < 2 ? null : reporters[1..],
        };
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
    internal SentinelHint? TakePending(bool activeFailed = false)
    {
        if (_pending is not { } next)
        {
            if (Active is not { AdditionalReportingSentinels: { Length: > 0 } reporters } active) return null;
            next = (activeFailed ? active : ForReporterReconciliation(active)) with
            {
                MustRediscover = true,
                ReportingSentinel = reporters[0],
                AdditionalReportingSentinels = reporters.Length == 1 ? null : reporters[1..],
            };
            Active = next;
            return next;
        }
        if (Active is { } activeHint)
        {
            // A successful pass consumed this switch's source evidence. An alternate reporter
            // still needs querying, but cannot retire the generation that was just validated.
            if (!activeFailed && next.Key == activeHint.Key) next = ForReporterReconciliation(next);
            if (activeFailed)
            {
                var unqueriedReporters = EnumerateReportingSentinels(next)
                    .Where(reporter => activeHint.ReportingSentinel is not { } activeReporter
                        || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(reporter, activeReporter)).ToArray();
                next = Merge(activeHint, in next) with { MustRediscover = true };
                if (unqueriedReporters.Length > 0)
                {
                    var reporters = UnionEndpoints(unqueriedReporters, EnumerateReportingSentinels(next), activeHint.ReportingSentinel);
                    next = next with
                    {
                        ReportingSentinel = reporters[0],
                        AdditionalReportingSentinels = reporters.Length < 2 ? null : reporters[1..],
                    };
                }
            }
            else if (activeHint.AdditionalReportingSentinels is { Length: > 0 } unqueriedReporters)
            {
                var unqueried = ForReporterReconciliation(activeHint) with
                {
                    ReportingSentinel = unqueriedReporters[0],
                    AdditionalReportingSentinels = unqueriedReporters.Length == 1
                        ? null : unqueriedReporters[1..],
                };
                next = Merge(unqueried, in next);
                next = PrioritizeReportingSentinels(next, unqueriedReporters);
            }
        }
        _pending = null;
        Active = next;
        return next;
    }

    private static SentinelHint ForReporterReconciliation(SentinelHint hint)
        => new(hint.Key, MustRediscover: true, ReportingSentinel: hint.ReportingSentinel,
            AdditionalReportingSentinels: hint.AdditionalReportingSentinels);

    /// <summary>Ends the worker: no hint is active or pending.</summary>
    internal void Complete()
    {
        Active = null;
        _pending = null;
    }
}
