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
/// <param name="AdditionalOldPrimaries">Other switch sources retained while pending hints merge.</param>
/// <param name="AdditionalOldPrimaryAddresses">Resolved addresses aligned with <paramref name="AdditionalOldPrimaries"/>.</param>
/// <param name="AdditionalTargets">Other announced targets retained while pending hints merge.</param>
/// <param name="ReportingSentinel">The Sentinel that delivered the switch hint.</param>
/// <param name="AdditionalReportingSentinels">Other reporting Sentinels retained during coalescing.</param>
internal readonly record struct SentinelHint(
    string Key,
    RespireEndpoint? Target = null,
    RespireEndpoint? OldPrimary = null,
    bool MustRediscover = false,
    string[]? OldPrimaryAddresses = null,
    RespireEndpoint[]? AdditionalOldPrimaries = null,
    RespireEndpoint? ReportingSentinel = null,
    RespireEndpoint[]? AdditionalTargets = null,
    string[]?[]? AdditionalOldPrimaryAddresses = null,
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
        if (ActiveKey == hint.Key || _pending?.Key == hint.Key)
        {
            // Duplicates coalesce. A fault report must still outlive the active attempt, because
            // that attempt may have queried Sentinel before the fault happened, or may fail.
            var knownReporters = (Active is { } activeHintForReporter ? EnumerateReportingSentinels(activeHintForReporter) : [])
                .Concat(_pending is { } pendingReporters ? EnumerateReportingSentinels(pendingReporters) : [])
                .ToHashSet(SentinelDiscoveryState.EndpointComparer.Instance);
            var newReporter = EnumerateReportingSentinels(hint).Any(reporter => !knownReporters.Contains(reporter));
            if (hint.MustRediscover || newReporter)
                _pending = _pending is { } pending
                    ? Merge(pending, in hint) with { MustRediscover = pending.MustRediscover || hint.MustRediscover }
                    : Active is { } active ? Merge(active, in hint) with { MustRediscover = active.MustRediscover || hint.MustRediscover } : hint;
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
        var sources = EnumerateOldPrimaries(previous).Concat(EnumerateOldPrimaries(hint))
            .GroupBy(static source => source.Endpoint, SentinelDiscoveryState.EndpointComparer.Instance)
            .Select(static group => (Endpoint: group.Key, Addresses: group.Select(static source => source.Addresses)
                .FirstOrDefault(static addresses => addresses is not null)))
            .ToArray();
        var selectedAddresses = merged.OldPrimary is { } selected
            ? sources.FirstOrDefault(source => SentinelDiscoveryState.EndpointComparer.Instance.Equals(source.Endpoint, selected)).Addresses
            : null;
        var additionalSources = merged.OldPrimary is { } primary
            ? sources.Where(source => !SentinelDiscoveryState.EndpointComparer.Instance.Equals(source.Endpoint, primary)).ToArray()
            : sources;
        var sourceEndpoints = sources.Select(static source => source.Endpoint)
            .ToHashSet(SentinelDiscoveryState.EndpointComparer.Instance);
        var targets = EnumerateTargets(previous).Concat(EnumerateTargets(hint))
            .Distinct(SentinelDiscoveryState.EndpointComparer.Instance).ToArray();
        var comparer = SentinelDiscoveryState.EndpointComparer.Instance;
        RespireEndpoint? selectedTarget = null;
        // Arrival order cannot establish chronology. A target that is also a retained switch
        // source is ambiguous; let fresh discovery choose instead of promoting it by edge shape.
        if (merged.Target is { } candidate && !sourceEndpoints.Contains(candidate))
            selectedTarget = candidate;
        else if (previous.Target is { } priorCandidate && !sourceEndpoints.Contains(priorCandidate)) selectedTarget = priorCandidate;
        else selectedTarget = targets.Where(target => !sourceEndpoints.Contains(target))
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
        var reporters = EnumerateReportingSentinels(previous).Concat(EnumerateReportingSentinels(hint))
            .Distinct(SentinelDiscoveryState.EndpointComparer.Instance).ToArray();
        if (reportingSentinel is null && reporters.Length > 0) reportingSentinel = reporters[0];
        return merged with
        {
            MustRediscover = mustRediscover || targets.Any(target => sourceEndpoints.Contains(target)),
            Target = selectedTarget,
            AdditionalOldPrimaries = additionalSources.Length == 0 ? null
                : additionalSources.Select(static source => source.Endpoint).ToArray(),
            AdditionalOldPrimaryAddresses = additionalSources.Length == 0 ? null
                : additionalSources.Select(static source => source.Addresses).ToArray(),
            OldPrimaryAddresses = selectedAddresses,
            ReportingSentinel = reportingSentinel,
            AdditionalReportingSentinels = reporters.Where(reporter => reportingSentinel is not { } selected
                || !comparer.Equals(reporter, selected)).ToArray() is { Length: > 0 } additionalReporters
                ? additionalReporters : null,
            AdditionalTargets = targets.Where(target => selectedTarget is not { } primaryTarget
                || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(target, primaryTarget))
                .Where(target => !sourceEndpoints.Contains(target))
                .ToArray() is { Length: > 0 } extraTargets
                ? extraTargets : null,
        };
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
        if (hint.AdditionalOldPrimaries is not { } additional) return hint;
        for (var i = 0; i < additional.Length; i++)
        {
            if (!comparer.Equals(additional[i], oldPrimary)) continue;
            var allAddresses = hint.AdditionalOldPrimaryAddresses is { } existing
                ? existing.ToArray()
                : new string[]?[additional.Length];
            allAddresses[i] = addresses;
            return hint with { AdditionalOldPrimaryAddresses = allAddresses };
        }
        return hint;
    }

    private static IEnumerable<RespireEndpoint> EnumerateTargets(SentinelHint hint)
    {
        if (hint.Target is { } target) yield return target;
        if (hint.AdditionalTargets is { } additional)
            foreach (var endpoint in additional) yield return endpoint;
    }

    private static IEnumerable<(RespireEndpoint Endpoint, string[]? Addresses)> EnumerateOldPrimaries(SentinelHint hint)
    {
        if (hint.OldPrimary is { } oldPrimary) yield return (oldPrimary, hint.OldPrimaryAddresses);
        if (hint.AdditionalOldPrimaries is { } additional)
            for (var i = 0; i < additional.Length; i++)
                yield return (additional[i], hint.AdditionalOldPrimaryAddresses is { } addresses && i < addresses.Length
                    ? addresses[i] : null);
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
        var reporters = prioritized.Concat(EnumerateReportingSentinels(hint))
            .Distinct(SentinelDiscoveryState.EndpointComparer.Instance).ToArray();
        return hint with
        {
            ReportingSentinel = reporters[0],
            AdditionalReportingSentinels = reporters.Length < 2 ? null : reporters[1..],
        };
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
            next = active with
            {
                ReportingSentinel = reporters[0],
                AdditionalReportingSentinels = reporters.Length == 1 ? null : reporters[1..],
            };
            Active = next;
            return next;
        }
        if (Active is { } activeHint)
        {
            if (activeFailed)
            {
                var unqueriedReporters = EnumerateReportingSentinels(next)
                    .Where(reporter => activeHint.ReportingSentinel is not { } activeReporter
                        || !SentinelDiscoveryState.EndpointComparer.Instance.Equals(reporter, activeReporter)).ToArray();
                next = Merge(activeHint, in next) with { MustRediscover = true };
                if (unqueriedReporters.Length > 0)
                    next = PrioritizeReportingSentinels(next, unqueriedReporters);
            }
            else if (activeHint.AdditionalReportingSentinels is { Length: > 0 } unqueriedReporters)
            {
                var unqueried = activeHint with
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

    /// <summary>Ends the worker: no hint is active or pending.</summary>
    internal void Complete()
    {
        Active = null;
        _pending = null;
    }
}
