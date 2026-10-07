using System.Collections.Immutable;

namespace Respire.Internal;

internal enum SentinelNotificationPhase
{
    Idle,
    Active,
    ActivePending,
}

/// <summary>
/// Every operation returns a new value. DNS lookup records share immutable storage,
/// so an earlier snapshot cannot be changed by later offers or lookup completions.
/// </summary>
/// <remarks>
/// <para>
/// Hints retain immutable source, target, reporter and down-report collections, plus a
/// <see cref="SentinelReporterLedger"/>. Lookup records are an immutable dictionary keyed by
/// lookup lifetime. Evidence arrays are immutable after publication: duplicate reporter unions
/// reuse existing arrays, empty source unions reuse their populated operand, and unchanged DNS
/// evidence does not clone source arrays.
/// </para>
/// <para>
/// A source-resolution record retains its original hint and evidence offered after that lookup
/// began. It never imports earlier pending evidence: an older switch target could then protect a
/// primary from a later demotion. Completion removes the record. DNS evidence stays paired with
/// its endpoint and port.
/// </para>
/// <para>Safety boundaries:</para>
/// <list type="bullet">
/// <item>Notifications are advisory. Publication still requires Sentinel discovery and a successful
/// <c>ROLE</c> check. Unknown or ambiguous advisory DNS never authorizes a different owner or erases a
/// demotion fence.</item>
/// <item>Observed configuration epochs never move backward. Equal or missing epochs cannot change an
/// observed owner through an ambiguous DNS overlap.</item>
/// <item>When <c>SENTINEL MASTER</c> is unavailable, source/target evidence still fences a stale reporter
/// whose old primary keeps answering <c>ROLE master</c>; a wake-up-only event model would lose this.
/// Conflicting targets do not disable source fences. A completed A-to-B switch cannot invert an
/// independent pending B-to-A fence when another A-to-B report arrives. Reconciling a completed
/// conflicting cycle consumes only the validated target's source fence; a source demoted toward a
/// distinct pending target stays fenced.</item>
/// <item>A gap or master-down report carries no demotion evidence. After it recovers a primary,
/// unqueried reporters can confirm that primary or its unambiguous validated peer alias, but cannot
/// replace it without a newer epoch. This restriction belongs to that reconciliation pass and duplicate
/// reports of the same outage; an independent down/gap hint can discover a later primary, and an
/// independent switch keeps its own evidence.</item>
/// <item>DNS answer sets do not prove which peer answered <c>ROLE</c>. Reconciliation keeps the actual
/// validated socket peer, including port. Ambiguous overlaps cannot consume another primary's source
/// fence: demotion matching may conservatively match any source address, but consuming the fence needs
/// the stronger identity proof. Fresh DNS must match the validated peer even when the hostname text is
/// unchanged; textual hostname identity is a fallback only when DNS evidence is unavailable.</item>
/// <item>An unchanged target hostname cannot suppress a switch (DNS may now point elsewhere). The
/// target-is-current shortcut requires numeric peer identity on every command slot (see
/// <see cref="SentinelGenerationEvidence"/>); one matching socket cannot authorize reuse while another
/// still reaches an old DNS peer. Source fences still recognize any known peer, and conflicting-cycle
/// source evidence still protects an explicitly announced failback target.</item>
/// <item>A target hostname whose entire DNS answer set identifies demoted sources is rejected before
/// connecting, even with a newer epoch. Mixed answers proceed to socket validation, where connecting to a
/// demoted source is still rejected. Epochs order Sentinel's announced owner; they do not prove the
/// client's DNS or socket reaches it. The router checks every registered ROLE-validated socket peer
/// before accepting or publishing a configuration; the last validated socket cannot hide another
/// socket reaching a demoted source.</item>
/// <item>A source hostname may already resolve to the promoted peer. Fresh source addresses that also
/// identify an unambiguous announced hostname target are not retained as demotion evidence (before or
/// after target publication) unless they identify the peer validated when the event arrived. A literal
/// source, the connected source hostname and that known peer still retire the generation; target DNS
/// cannot erase this source identity.</item>
/// <item>When a switch names the current primary's hostname, its validated peer is captured before
/// queuing discovery, so a metadata-denied numeric alias cannot republish the demoted server while DNS
/// is unavailable. In a conflicting cycle, source address evidence also protects a target with the
/// same hostname and port from premature retirement.</item>
/// <item>Forced discovery can reuse a healthy generation when the announced endpoint is its canonical
/// endpoint without DNS evidence, or resolves unambiguously to its connected peer. A stable hostname
/// with changed DNS requires a fresh connection; reuse also requires the same validated peer and port,
/// still checks <c>ROLE</c>, and compares IPv6 spellings with the same normalized comparer as epoch state.</item>
/// <item>Late source resolution never combines a source's addresses with the arrival generation's port.
/// A newer generation is protected by the arrival generation's own identity or later announced failback
/// evidence; a pending B-to-C switch must still demote an intervening B.</item>
/// <item>A switch confirming the current primary stays pending while discovery is active: the in-flight
/// query can still publish another primary first.</item>
/// <item>Retirement preserves accepted commands and correction fences. Coalescing never replays accepted
/// work or forces disposal of a draining generation.</item>
/// </list>
/// <para>
/// Liveness limits: failed discovery retries with backoff (unlimited by default); a configured budget can
/// stop the worker, and commands still trigger discovery on demand. There is no periodic polling loop.
/// Permanently ambiguous or stale reports cannot guarantee failover without weakening split-brain safety.
/// Switch payloads carry no epoch or sequence, so without epoch metadata a delayed A-to-B report after a
/// completed A-to-B, B-to-A cycle is indistinguishable from a genuine third transition; the metadata-free
/// fallback deliberately permits genuine recurrence (pinned by
/// <c>CompletedSwitchCycleAllowsGenuineRecurrenceWithoutEpochs</c>). Epoch ordering needs
/// <c>SENTINEL MASTER</c> permission; ROLE alone does not prove global ownership.
/// </para>
/// <para>
/// Regression gates: <c>SentinelFenceTransitionTests</c> (15 idle/active/active+pending transitions,
/// each with no metadata, equal and newer epochs), <c>OfferedEvidenceIsAlwaysQueuedOrDiscoveredAcrossWorkerTransitions</c>,
/// <c>MergeUnionsAreCommutativeIdempotentAndKeepTheFaultFlag</c>,
/// <c>RandomNotificationSequencesRequireRoleAndMonotonicEpochs</c> (real router, fake RESP sockets; positive
/// controls so reject-everything cannot pass) and
/// <c>RandomInterleavingsNeverLetAStaleReporterReleaseValidatedOwnership</c>.
/// </para>
/// </remarks>
internal readonly partial record struct SentinelNotificationState
{
    internal SentinelHint? Active { get; init; }
    internal SentinelHint? Pending { get; init; }
    internal int RetryAttempts { get; init; }
    internal int ConsecutiveFailures { get; init; }
    internal long DiscoveryNotBefore { get; init; }
    internal bool IsDisposed { get; init; }
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
                    Ledger = independent.Ledger.Union(hint.Ledger),
                    MustRediscover = true,
                },
            };
        }
        // Even confirmation of Current remains pending: in-flight discovery can publish
        // another generation before that confirmation has been reconciled.
        var needsAnotherPass = hint.MustRediscover || HasNewReporter(in hint) || HasNewObservation(in hint);
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

    private bool HasNewObservation(in SentinelHint hint)
    {
        foreach (var observation in hint.Ledger.Observations)
            if (Active?.Ledger.ContainsObservation(observation) != true
                && Pending?.Ledger.ContainsObservation(observation) != true) return true;
        return false;
    }

    private static bool ContainsReporter(SentinelHint? hint, RespireEndpoint reporter)
    {
        if (hint is not { } value) return false;
        foreach (var endpoint in value.Reporters)
            if (SentinelEndpointIdentity.EndpointComparer.Instance.Equals(endpoint, reporter)) return true;
        return false;
    }

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
            Ledger = previous.Ledger.Union(hint.Ledger),
            ReconciliationPrimary = sources.Length == 0 && previous.ReconciliationPrimary is not null
                ? hint.ReconciliationPrimary : null,
        };
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
