using System.Collections.Immutable;

namespace Respire.Internal;

internal enum SentinelNotificationPhase
{
    Idle,
    Active,
    ActivePending,
}

// Every operation returns a new value. DNS lookup records share immutable storage,
// so an earlier snapshot cannot be changed by later offers or lookup completions.
internal readonly record struct SentinelNotificationState
{
    internal SentinelHint? Active { get; init; }
    internal SentinelHint? Pending { get; init; }
    private readonly ImmutableDictionary<long, SentinelHint>? _sourceResolutions;
    internal ImmutableDictionary<long, SentinelHint> SourceResolutions
    {
        get => _sourceResolutions ?? ImmutableDictionary<long, SentinelHint>.Empty;
        init => _sourceResolutions = value;
    }
    internal long LastResolutionId { get; init; }
    internal SentinelNotificationPhase Phase => Active is null ? SentinelNotificationPhase.Idle
        : Pending is null ? SentinelNotificationPhase.Active : SentinelNotificationPhase.ActivePending;

    internal SentinelNotificationState BeginSourceResolution(in SentinelHint hint, out long id)
    {
        id = checked(LastResolutionId + 1);
        return this with { LastResolutionId = id, SourceResolutions = SourceResolutions.Add(id, hint) };
    }

    internal SentinelNotificationState EndSourceResolution(long id)
        => this with { SourceResolutions = SourceResolutions.Remove(id) };

    internal SentinelNotificationState Offer(in SentinelHint hint, bool targetIsCurrent, out bool startWorker)
    {
        var state = this;
        foreach (var resolution in SourceResolutions)
            state = state with
            {
                SourceResolutions = state.SourceResolutions.SetItem(resolution.Key, Merge(resolution.Value, in hint)),
            };
        startWorker = false;
        if (Active is null)
        {
            if (!hint.MustRediscover && targetIsCurrent) return state;
            startWorker = true;
            return state with { Pending = null, Active = hint };
        }
        var activeDuplicate = Active.Value.Key == hint.Key || SameSwitch(Active.Value, in hint);
        var duplicate = activeDuplicate || Pending?.Key == hint.Key;
        if (activeDuplicate && hint.Sources.Length > 0
            && Pending is { Sources.Length: > 0 } independent && independent.Key != hint.Key)
        {
            // A duplicate of active work must not invert an independent pending failback.
            return state with
            {
                Pending = independent with
                {
                    Reporters = UnionEndpoints(independent.Reporters, hint.Reporters),
                    MustRediscover = true,
                },
            };
        }
        // Even confirmation of Current remains pending: in-flight discovery can publish
        // another generation before that confirmation has been reconciled.
        var needsAnotherPass = hint.MustRediscover || HasNewReporter(in hint);
        if (duplicate && !needsAnotherPass && (Pending is null || Pending.Value.Key == hint.Key)) return state;
        var pending = Merge(Pending ?? (duplicate ? Active : null), in hint);
        if (duplicate && needsAnotherPass) pending = pending with { MustRediscover = true };
        return state with { Pending = pending };
    }

    private static bool SameSwitch(in SentinelHint left, in SentinelHint right)
        => left.Sources.Length == 1 && right.Sources.Length == 1
            && left.Targets.Length == 1 && right.Targets.Length == 1
            && SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left.Sources[0].Endpoint, right.Sources[0].Endpoint)
            && SentinelEndpointIdentity.EndpointComparer.Instance.Equals(left.Targets[0], right.Targets[0]);

    private bool HasNewReporter(in SentinelHint hint)
    {
        foreach (var reporter in hint.Reporters)
            if (!ContainsReporter(Active, reporter) && !ContainsReporter(Pending, reporter)) return true;
        return false;
    }

    private static bool ContainsReporter(SentinelHint? hint, RespireEndpoint reporter)
    {
        if (hint is not { } value) return false;
        foreach (var endpoint in value.Reporters)
            if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(endpoint, reporter)) return true;
        return false;
    }

    internal SentinelNotificationState RetainResolvedOldPrimaryAddresses(RespireEndpoint endpoint, string[] addresses)
        => this with
        {
            Active = Active?.WithSourceAddresses(endpoint, addresses),
            Pending = Pending?.WithSourceAddresses(endpoint, addresses),
        };

    internal SentinelNotificationState TakePending(bool activeFailed, RespireEndpoint? validatedPrimary,
        RespireEndpoint? validatedPeer, out SentinelHint? taken)
    {
        if (Pending is not { } next)
        {
            if (Active is not { Reporters.Length: > 1 } active)
            {
                taken = null;
                return this;
            }
            next = (activeFailed ? active : ForReporterReconciliation(active, validatedPrimary, validatedPeer)) with
            {
                MustRediscover = true,
                Reporters = active.Reporters[1..],
            };
            taken = next;
            return this with { Active = next };
        }
        if (Active is { } activeHint)
        {
            var sameOutage = next.DownKey is { } downKey && downKey == activeHint.DownKey;
            var freshWakeup = next.Sources.Length == 0 && next.ReconciliationPrimary is null && !sameOutage;
            if (!activeFailed && !freshWakeup && (sameOutage || next.Key == activeHint.Key
                || activeHint.Sources.Length == 0
                    && next.Target is null && IsValidatedTarget(next, validatedPrimary, validatedPeer)))
                next = ForReporterReconciliation(next, validatedPrimary, validatedPeer);
            if (activeFailed)
            {
                var unqueriedReporters = next.Reporters
                    .Where(reporter => activeHint.ReportingSentinel is not { } activeReporter
                        || !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(reporter, activeReporter)).ToImmutableArray();
                next = Merge(activeHint, in next) with { MustRediscover = true };
                if (unqueriedReporters.Length > 0)
                    next = next with { Reporters = UnionEndpoints(unqueriedReporters, next.Reporters, activeHint.ReportingSentinel) };
            }
            else if (activeHint.Reporters.Length > 1)
            {
                var unqueriedReporters = activeHint.Reporters[1..];
                var unqueried = ForReporterReconciliation(activeHint, validatedPrimary, validatedPeer) with
                {
                    Reporters = unqueriedReporters,
                };
                // A completed switch cannot fence the target of an independent failback.
                if (next.Target is { } pendingTarget)
                    unqueried = unqueried with { Sources = unqueried.Sources.Where(source =>
                        !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(source.Endpoint, pendingTarget)).ToImmutableArray() };
                next = Merge(unqueried, in next);
                next = next with { Reporters = UnionEndpoints(unqueriedReporters, next.Reporters) };
            }
            if (!activeFailed && freshWakeup) next = next with { ReconciliationPrimary = null };
        }
        taken = next;
        return this with { Pending = null, Active = next };
    }

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
        var sources = validatedPrimary is { } primary ? hint.Sources.Where(source =>
            !MatchesValidatedSource(primary, validatedPeer, source)).ToImmutableArray() : hint.Sources;
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
        return peer is { } validated && source.Evidence.ConfirmsPeer(validated);
    }

    internal SentinelNotificationState SupersedeActive() => this with { Active = Pending, Pending = null };
    internal SentinelNotificationState Complete() => this with { Active = null, Pending = null };
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

    private static ImmutableArray<SentinelDownReport> UnionDownReports(
        ImmutableArray<SentinelDownReport> first, ImmutableArray<SentinelDownReport> second)
    {
        List<SentinelDownReport>? result = null;
        var comparer = SentinelEndpointIdentity.EndpointComparer.Instance;
        foreach (var report in second)
        {
            var found = false;
            var count = result?.Count ?? first.Length;
            for (var index = 0; index < count; index++)
            {
                var known = result is null ? first[index] : result[index];
                if (comparer.Equals(known.Primary, report.Primary) && comparer.Equals(known.Reporter, report.Reporter)
                    && known.OwnerAtObservation == report.OwnerAtObservation)
                {
                    found = true;
                    break;
                }
            }
            if (!found) (result ??= [.. first]).Add(report);
        }
        return result?.ToImmutableArray() ?? first;
    }

    private static bool HasTargetSourceOverlap(ImmutableArray<RespireEndpoint> targets, ImmutableArray<SentinelSwitchSource> sources)
    {
        foreach (var target in targets)
            foreach (var source in sources)
                if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(target, source.Endpoint)) return true;
        return false;
    }

    private static ImmutableArray<SentinelSwitchSource> UnionSources(
        ImmutableArray<SentinelSwitchSource> first, ImmutableArray<SentinelSwitchSource> second)
    {
        if (first.Length == 0) return second;
        if (second.Length == 0 || first == second) return first;
        var sources = new Dictionary<RespireEndpoint, ImmutableArray<string>>(SentinelEndpointIdentity.EndpointComparer.Instance);
        foreach (var source in first.Concat(second))
        {
            if (!sources.TryGetValue(source.Endpoint, out var known)) sources.Add(source.Endpoint, source.Addresses);
            else if (!source.Addresses.IsDefault)
                sources[source.Endpoint] = (known.IsDefault ? [] : known)
                    .Union(source.Addresses, SentinelEndpointIdentity.AddressComparer.Instance).ToImmutableArray();
        }
        return sources.Select(pair => SentinelSwitchSource.FromSnapshot(pair.Key, pair.Value)).ToImmutableArray();
    }

    // Set union preserves first-seen reporter order without assigning event chronology.
    private static ImmutableArray<RespireEndpoint> UnionEndpoints(
        ImmutableArray<RespireEndpoint> first, ImmutableArray<RespireEndpoint> second, RespireEndpoint? excluded = null)
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
        return result.ToImmutableArray();

        void Add(ImmutableArray<RespireEndpoint> endpoints)
        {
            foreach (var value in endpoints)
                if (seen.Add(value)) result.Add(value);
        }
    }

    private static bool ContainsAll(ImmutableArray<RespireEndpoint> first, ImmutableArray<RespireEndpoint> second)
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

}
