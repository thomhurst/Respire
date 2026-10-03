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
    // Nonempty only when all merged evidence consists of parsed master-down reports.
    // Keep reporter association: a current-owner outage cannot release a stale reporter's fence.
    internal ImmutableArray<SentinelDownReport> DownReports { get; init; } = [];
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
            return this with { Sources = Sources.SetItem(index, new(source.Endpoint, addresses)) };
        }
        return this;
    }
}
