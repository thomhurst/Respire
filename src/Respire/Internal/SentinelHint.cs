using System.Collections.Immutable;

namespace Respire.Internal;

// Endpoint identity uses SentinelEndpointIdentity; Addresses are DNS evidence for this
// source, not interchangeable owners. In particular, overlapping DNS sets do not prove identity.
internal readonly record struct SentinelSwitchSource
{
    internal SentinelAddressEvidence Evidence { get; }
    internal RespireEndpoint Endpoint => Evidence.Endpoint;
    internal ImmutableArray<string> Addresses => Evidence.Addresses;

    internal SentinelSwitchSource(RespireEndpoint endpoint, string[]? addresses)
        => Evidence = new(endpoint, addresses);

    private SentinelSwitchSource(SentinelAddressEvidence evidence) => Evidence = evidence;

    internal static SentinelSwitchSource FromSnapshot(RespireEndpoint endpoint, ImmutableArray<string> addresses)
        => new(SentinelAddressEvidence.FromSnapshot(endpoint, addresses));

    internal static SentinelSwitchSource FromEvidence(SentinelAddressEvidence evidence) => new(evidence);
}
internal readonly record struct SentinelDownReport(RespireEndpoint Primary, RespireEndpoint Reporter,
    SentinelValidatedPrimary? OwnerAtObservation = null);

/// <summary>Advisory event evidence. Collection order never establishes failover chronology.</summary>
internal readonly record struct SentinelHint(
    SentinelHintKey Key, ImmutableArray<RespireEndpoint> Targets, ImmutableArray<SentinelSwitchSource> Sources,
    ImmutableArray<RespireEndpoint> Reporters, bool MustRediscover)
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
    internal SentinelReporterLedger Ledger { get; init; }
    // Nonempty only when all merged evidence consists of parsed master-down reports.
    // Keep reporter association: a current-owner outage cannot release a stale reporter's fence.
    internal ImmutableArray<SentinelDownReport> DownReports => Ledger.IsDownOnly ? Ledger.DownReports : [];
    internal SentinelValidatedPrimary? DownReportPrimary { get; init; }

    internal SentinelHint BindDownReportsToCurrentPrimary(SentinelValidatedPrimary current)
        => DownReports.Length == 0 ? this : this with { DownReportPrimary = current };

    internal static SentinelHint FromSwitchMaster(string key, RespireEndpoint? source,
        RespireEndpoint? target, RespireEndpoint reporter)
        => new(new(key), target is { } to ? [to] : [],
            source is { } from ? [new(from, null)] : [], [reporter], target is null)
        {
            Ledger = SentinelReporterLedger.FromObservation(new(SentinelObservationKind.Switch, new(key), reporter, source, target)),
        };

    internal static SentinelHint FromDown(string key, RespireEndpoint reporter, RespireEndpoint? primary = null,
        SentinelValidatedPrimary? ownerAtObservation = null)
        => new(new(key, primary), [], [], [reporter], true)
        {
            // A hostname can denote a different physical owner after publication. Do not
            // treat its next outage as a duplicate of the previous owner's completed outage.
            DownKey = new(key, primary, primary is { } named && !System.Net.IPAddress.TryParse(named.Host, out _)
                && ownerAtObservation?.Peer is { } peer
                    ? peer : (RespireEndpoint?)null),
            Ledger = SentinelReporterLedger.FromObservation(new(SentinelObservationKind.Down,
                new(key, primary), reporter, primary, null, ownerAtObservation)),
        };

    internal static SentinelHint FromGap(RespireEndpoint reporter)
        => new(new("gap"), [], [], [reporter], true)
        {
            Ledger = SentinelReporterLedger.FromObservation(new(SentinelObservationKind.Gap, new("gap"), reporter, null, null)),
        };

    internal SentinelHint CaptureObservationContext(SentinelValidatedPrimary? owner, SentinelEpochEvidence epoch)
    {
        var captured = Ledger.CaptureContext(owner, epoch);
        var downKey = DownKey;
        if (downKey is { Primary: { } primary, Peer: null } key
            && !System.Net.IPAddress.TryParse(primary.Host, out _)
            && captured.Observations is [var observed] && observed.OwnerAtObservation?.Peer is { } peer)
            downKey = key with { Peer = peer };
        return this with { Ledger = captured, DownKey = downKey };
    }

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
            return WithSourceEvidence(source.Evidence.HasSameAddresses(addresses)
                ? source.Evidence : new(source.Endpoint, addresses));
        }
        return this;
    }

    internal SentinelHint WithSourceEvidence(SentinelAddressEvidence evidence, bool retainObservation = true)
    {
        var updated = retainObservation ? this with { Ledger = Ledger.WithDnsEvidence(evidence) } : this;
        for (var index = 0; index < Sources.Length; index++)
        {
            var source = Sources[index];
            if (!SentinelEndpointIdentity.EndpointComparer.Instance.Equals(source.Endpoint, evidence.Endpoint)) continue;
            if (source.Addresses.IsDefault == evidence.Addresses.IsDefault
                && source.Addresses.AsSpan().SequenceEqual(evidence.Addresses.AsSpan())) return updated;
            return updated with { Sources = Sources.SetItem(index, SentinelSwitchSource.FromEvidence(evidence)) };
        }
        return updated;
    }
}
