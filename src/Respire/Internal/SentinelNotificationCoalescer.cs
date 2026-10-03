namespace Respire.Internal;

// Endpoint identity uses SentinelEndpointIdentity; Addresses are DNS evidence for this
// source, not interchangeable owners. In particular, overlapping DNS sets do not prove identity.
internal readonly record struct SentinelSwitchSource(RespireEndpoint Endpoint, string[]? Addresses)
{
    internal SentinelAddressEvidence Evidence => new(Endpoint, Addresses);
}
internal readonly record struct SentinelDownReport(RespireEndpoint Primary, RespireEndpoint Reporter,
    SentinelValidatedPrimary? OwnerAtObservation = null);

/// <summary>Advisory event evidence. Collection order never establishes failover chronology.</summary>
internal readonly record struct SentinelHint(
    SentinelHintKey Key, RespireEndpoint[] Targets, SentinelSwitchSource[] Sources,
    RespireEndpoint[] Reporters, bool MustRediscover)
{
    // Positive only for first-subscription gaps. Mixing any independent event clears
    // this marker so an earlier successful discovery can never swallow real evidence.
    internal long StartupSubscriptionVersion { get; init; }
    // Reporter-only reconciliation has no demotion evidence. Without a newer epoch it may
    // confirm this owner, but must not let a stale reporter undo the successful recovery.
    internal SentinelValidatedPrimary? ReconciliationPrimary { get; init; }
    // One reported outage, independent of reporter and changing quorum counts. Null means
    // mixed or non-down evidence, which cannot be classified as another report of this outage.
    internal SentinelHintKey? DownKey { get; init; }
    // Nonempty only when all merged evidence consists of parsed master-down reports.
    // Keep reporter association: a current-owner outage cannot release a stale reporter's fence.
    internal SentinelDownReport[] DownReports { get; init; } = [];
    internal SentinelValidatedPrimary? DownReportPrimary { get; init; }

    internal SentinelHint BindDownReportsToCurrentPrimary(SentinelValidatedPrimary current)
        => DownReports.Length == 0 ? this : this with { DownReportPrimary = current };

    internal static SentinelHint FromSwitchMaster(string key, RespireEndpoint? source,
        RespireEndpoint? target, RespireEndpoint reporter)
        => new(new(key), target is { } to ? [to] : [],
            source is { } from ? [new(from, null)] : [], [reporter], target is null);

    internal static SentinelHint FromDown(string key, RespireEndpoint reporter, RespireEndpoint? primary = null,
        SentinelValidatedPrimary? ownerAtObservation = null)
        => new(new(key, primary), [], [], [reporter], true)
        {
            // A hostname can denote a different physical owner after publication. Do not
            // treat its next outage as a duplicate of the previous owner's completed outage.
            DownKey = new(key, primary, primary is { } named && !System.Net.IPAddress.TryParse(named.Host, out _)
                && ownerAtObservation?.Peer is { } peer
                    ? peer : (RespireEndpoint?)null),
            DownReports = primary is { } affected ? [new(affected, reporter, ownerAtObservation)] : [],
        };

    internal static SentinelHint FromGap(RespireEndpoint reporter)
        => new(new("gap"), [], [], [reporter], true);

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
                    if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(source.Endpoint, target)) { isSource = true; break; }
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
    {
        for (var index = 0; index < Sources.Length; index++)
        {
            var source = Sources[index];
            if (!SentinelEndpointIdentity.EndpointComparer.Instance.Equals(source.Endpoint, endpoint)) continue;
            if (source.Evidence.HasSameAddresses(addresses)) return this;
            var sources = (SentinelSwitchSource[])Sources.Clone();
            sources[index] = source with { Addresses = addresses };
            return this with { Sources = sources };
        }
        return this;
    }
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
    internal SentinelHintKey? ActiveKey => Active?.Key;

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
        var activeDuplicate = ActiveKey == hint.Key || SameSwitch(Active.Value, in hint);
        var duplicate = activeDuplicate || _pending?.Key == hint.Key;
        if (activeDuplicate && hint.Sources.Length > 0
            && _pending is { Sources.Length: > 0 } independent && independent.Key != hint.Key)
        {
            // The active switch is already being validated. Repeating it must not add its
            // source to an independent pending failback and invert that failback's fence.
            // Keep every reporter; a newer epoch can still establish a subsequent switch.
            _pending = independent with
            {
                Reporters = UnionEndpoints(independent.Reporters, hint.Reporters),
                MustRediscover = true,
            };
            return false;
        }
        // A discovery already in flight can publish a different primary. Preserve even a
        // switch confirming Current so its source fence and reporter survive that result.
        var needsAnotherPass = hint.MustRediscover || HasNewReporter(in hint);
        if (duplicate && !needsAnotherPass && (_pending is null || _pending.Value.Key == hint.Key)) return false;
        var basis = _pending ?? (duplicate ? Active : null);
        _pending = Merge(basis, in hint);
        if (duplicate && needsAnotherPass) _pending = _pending.Value with { MustRediscover = true };
        return false;
    }

    private static bool SameSwitch(in SentinelHint left, in SentinelHint right)
        => left.Sources.Length == 1 && right.Sources.Length == 1
            && left.Targets.Length == 1 && right.Targets.Length == 1
            && SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left.Sources[0].Endpoint, right.Sources[0].Endpoint)
            && SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left.Targets[0], right.Targets[0]);

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
        var comparer = SentinelEndpointIdentity.EndpointComparer.Instance;
        foreach (var endpoint in value.Reporters)
            if (comparer.Equals(endpoint, reporter)) return true;
        return false;
    }

    /// <summary>Unions event evidence without inferring chronology from targets, sources, or reporters.</summary>
    internal static SentinelHint Merge(SentinelHint? pending, in SentinelHint hint)
    {
        if (pending is not { } previous) return hint;
        var targets = UnionEndpoints(previous.Targets, hint.Targets);
        var reporters = UnionEndpoints(previous.Reporters, hint.Reporters);
        var sources = UnionSources(hint.Sources, previous.Sources);
        var mustRediscover = previous.MustRediscover || hint.MustRediscover
            || targets.Length > 1 || HasTargetSourceOverlap(targets, sources);
        // Keep the switch key when a down/gap event contributes no source. This is only deduplication identity.
        var key = hint.Sources.Length > 0 || previous.Sources.Length == 0 ? hint.Key : previous.Key;
        return new(key, targets, sources, reporters, mustRediscover)
        {
            StartupSubscriptionVersion = previous.StartupSubscriptionVersion > 0 && hint.StartupSubscriptionVersion > 0
                ? Math.Max(previous.StartupSubscriptionVersion, hint.StartupSubscriptionVersion) : 0,
            // An independent wake-up remains independent even when its key duplicates an
            // active reconciliation pass. Only two reconciliation-only hints retain a bound.
            DownKey = previous.DownKey == hint.DownKey ? hint.DownKey : null,
            DownReports = previous.DownReports.Length > 0 && hint.DownReports.Length > 0
                ? UnionDownReports(previous.DownReports, hint.DownReports) : [],
            ReconciliationPrimary = sources.Length == 0 && previous.ReconciliationPrimary is not null
                ? hint.ReconciliationPrimary : null,
        };
    }

    private static SentinelDownReport[] UnionDownReports(SentinelDownReport[] first, SentinelDownReport[] second)
    {
        List<SentinelDownReport>? result = null;
        var comparer = SentinelEndpointIdentity.EndpointComparer.Instance;
        foreach (var report in second)
        {
            var found = false;
            IReadOnlyList<SentinelDownReport> reports = result ?? (IReadOnlyList<SentinelDownReport>)first;
            for (var index = 0; index < reports.Count; index++)
            {
                var known = reports[index];
                if (comparer.Equals(known.Primary, report.Primary) && comparer.Equals(known.Reporter, report.Reporter)
                    && known.OwnerAtObservation == report.OwnerAtObservation)
                {
                    found = true;
                    break;
                }
            }
            if (!found) (result ??= [.. first]).Add(report);
        }
        return result?.ToArray() ?? first;
    }

    private static bool HasTargetSourceOverlap(RespireEndpoint[] targets, SentinelSwitchSource[] sources)
    {
        foreach (var target in targets)
            foreach (var source in sources)
                if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(target, source.Endpoint)) return true;
        return false;
    }

    private static SentinelSwitchSource[] UnionSources(SentinelSwitchSource[] first, SentinelSwitchSource[] second)
    {
        if (first.Length == 0) return second;
        if (second.Length == 0 || ReferenceEquals(first, second)) return first;
        var sources = new Dictionary<RespireEndpoint, string[]?>(SentinelEndpointIdentity.EndpointComparer.Instance);
        foreach (var source in first.Concat(second))
        {
            if (!sources.TryGetValue(source.Endpoint, out var known)) sources.Add(source.Endpoint, source.Addresses);
            else if (source.Addresses is { } addresses)
                sources[source.Endpoint] = (known ?? []).Union(addresses, SentinelEndpointIdentity.AddressComparer.Instance).ToArray();
        }
        return sources.Select(pair => new SentinelSwitchSource(pair.Key, pair.Value)).ToArray();
    }

    /// <summary>Retains a completed DNS lookup for its source in active and pending hints.</summary>
    internal void RetainResolvedOldPrimaryAddresses(RespireEndpoint oldPrimary, string[] addresses)
    {
        if (Active is { } active) Active = active.WithSourceAddresses(oldPrimary, addresses);
        if (_pending is { } pending) _pending = pending.WithSourceAddresses(oldPrimary, addresses);
    }

    // Set union preserves first-seen reporter order without assigning event chronology.
    private static RespireEndpoint[] UnionEndpoints(
        RespireEndpoint[] first, RespireEndpoint[] second, RespireEndpoint? excluded = null)
    {
        if (excluded is null)
        {
            if (first.Length == 0) return second;
            if (second.Length == 0 || ContainsAll(first, second)) return first;
        }
        var seen = new HashSet<RespireEndpoint>(SentinelEndpointIdentity.EndpointComparer.Instance);
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

    private static bool ContainsAll(RespireEndpoint[] first, RespireEndpoint[] second)
    {
        foreach (var candidate in second)
        {
            var found = false;
            foreach (var endpoint in first)
                if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(endpoint, candidate)) { found = true; break; }
            if (!found) return false;
        }
        return true;
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
            // A newly delivered down/gap hint can describe a later promotion. It is not
            // merely another reporter for the attempt that just completed.
            var sameOutage = next.DownKey is { } downKey && downKey == activeHint.DownKey;
            var freshWakeup = next.Sources.Length == 0 && next.ReconciliationPrimary is null && !sameOutage;
            // Reconciliation preserves demoted sources and consumes only the validated primary's
            // source evidence, so an alternate reporter cannot retire that generation again.
            if (!activeFailed && !freshWakeup && (sameOutage || next.Key == activeHint.Key
                || activeHint.Sources.Length == 0
                    && next.Target is null && IsValidatedTarget(next, validatedPrimary, validatedPeer)))
                next = ForReporterReconciliation(next, validatedPrimary, validatedPeer);
            if (activeFailed)
            {
                var unqueriedReporters = next.Reporters
                    .Where(reporter => activeHint.ReportingSentinel is not { } activeReporter
                        || !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(reporter, activeReporter)).ToArray();
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
                // An independent, unambiguous pending target can be a failback from the
                // completed switch. Its source remains fenced; the completed switch must
                // not contribute a contradictory fence against that pending target.
                if (next.Target is { } pendingTarget)
                    unqueried = unqueried with { Sources = unqueried.Sources.Where(source =>
                        !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(source.Endpoint, pendingTarget)).ToArray() };
                next = Merge(unqueried, in next);
                next = next with { Reporters = UnionEndpoints(unqueriedReporters, next.Reporters) };
            }
            if (!activeFailed && freshWakeup) next = next with { ReconciliationPrimary = null };
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
    {
        // Successful validation consumes only that primary's source evidence. Keep every
        // other demotion fence: metadata-free reporters can still advertise a stale master.
        var sources = validatedPrimary is { } primary ? hint.Sources.Where(source =>
            !MatchesValidatedSource(primary, validatedPeer, source)).ToArray() : hint.Sources;
        return hint with
        {
            MustRediscover = true,
            ReconciliationPrimary = sources.Length == 0 && validatedPrimary is { } owner
                ? new(owner, validatedPeer) : hint.ReconciliationPrimary,
            Sources = sources,
        };
    }

    private static bool MatchesValidatedSource(RespireEndpoint primary, RespireEndpoint? peer, SentinelSwitchSource source)
    {
        if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(primary, source.Endpoint)) return true;
        // Demotion matching is conservative, but consuming its fence requires unambiguous
        // identity. A DNS set containing several servers does not identify the validated one.
        return peer is { } validated && source.Evidence.ConfirmsPeer(validated);
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
