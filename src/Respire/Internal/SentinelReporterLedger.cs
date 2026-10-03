using System.Collections.Immutable;

namespace Respire.Internal;

internal enum SentinelObservationKind
{
    Switch,
    Down,
    Gap,
}

internal readonly record struct SentinelReporterObservation(
    SentinelObservationKind Kind,
    SentinelHintKey Key,
    RespireEndpoint Reporter,
    RespireEndpoint? Source,
    RespireEndpoint? Target,
    SentinelValidatedPrimary? OwnerAtObservation = null,
    SentinelEpochEvidence EpochAtObservation = default,
    bool ContextCaptured = false)
{
    internal ImmutableArray<SentinelAddressEvidence> CompletedDns { get; init; } = [];

    internal bool HasSameObservation(in SentinelReporterObservation other)
    {
        if (Kind != other.Kind || Key != other.Key || ContextCaptured != other.ContextCaptured
            || !SentinelEndpointIdentity.EndpointComparer.Instance.Equals(Reporter, other.Reporter)
            || !SentinelEndpointIdentity.SameEndpoint(Source, other.Source)
            || !SentinelEndpointIdentity.SameEndpoint(Target, other.Target)
            || OwnerAtObservation != other.OwnerAtObservation) return false;
        var epoch = EpochAtObservation;
        var otherEpoch = other.EpochAtObservation;
        return epoch.AcceptedEpoch == otherEpoch.AcceptedEpoch && epoch.ObservedEpoch == otherEpoch.ObservedEpoch
            && SentinelEndpointIdentity.SameEndpoint(epoch.ObservedPrimary, otherEpoch.ObservedPrimary)
            && SentinelEndpointIdentity.SameEndpoint(epoch.ValidatedPeer, otherEpoch.ValidatedPeer)
            && epoch.ObservedAddresses.HasSameSnapshot(otherEpoch.ObservedAddresses);
    }

    internal SentinelReporterObservation WithDns(SentinelAddressEvidence evidence)
    {
        foreach (var known in CompletedDns)
            if (known.HasSameSnapshot(evidence)) return this;
        return this with { CompletedDns = CompletedDns.Add(evidence) };
    }
}

// Original observations remain separate from the effective source fences and reporter
// order used by a discovery pass. Reconciliation can consume a fence without rewriting
// the event's owner, epoch, reporter, or completed DNS facts.
internal readonly record struct SentinelReporterLedger
{
    private readonly ImmutableArray<SentinelReporterObservation> _observations;
    private readonly ImmutableArray<SentinelDownReport> _downReports;
    internal ImmutableArray<SentinelReporterObservation> Observations => _observations.IsDefault ? [] : _observations;
    internal ImmutableArray<SentinelDownReport> DownReports => _downReports.IsDefault ? [] : _downReports;
    internal bool IsDownOnly { get; }

    private SentinelReporterLedger(ImmutableArray<SentinelReporterObservation> observations,
        ImmutableArray<SentinelDownReport> downReports, bool isDownOnly)
        => (_observations, _downReports, IsDownOnly) = (observations, downReports, isDownOnly);

    internal static SentinelReporterLedger FromObservation(SentinelReporterObservation observation)
    {
        if (observation.Kind == SentinelObservationKind.Down && observation.Source is { } primary)
            return new([observation], [new(primary, observation.Reporter, observation.OwnerAtObservation)], true);
        return new([observation], [], false);
    }

    internal bool ContainsObservation(in SentinelReporterObservation candidate)
    {
        foreach (var known in Observations)
        {
            if (!known.HasSameObservation(in candidate)) continue;
            foreach (var evidence in candidate.CompletedDns)
            {
                var found = false;
                foreach (var existing in known.CompletedDns)
                    if (existing.HasSameSnapshot(evidence)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }
        return false;
    }

    internal SentinelReporterLedger Union(in SentinelReporterLedger other)
    {
        var observations = Observations;
        foreach (var candidate in other.Observations)
            observations = AddObservation(observations, candidate);
        var reports = DownReports;
        foreach (var report in other.DownReports)
            reports = AddDownReport(reports, report);
        return new(observations, reports, IsDownOnly && other.IsDownOnly);
    }

    internal SentinelReporterLedger CaptureContext(SentinelValidatedPrimary? owner, SentinelEpochEvidence epoch)
    {
        var needsCapture = false;
        foreach (var observed in Observations)
            if (!observed.ContextCaptured) { needsCapture = true; break; }
        if (!needsCapture) return this;
        ImmutableArray<SentinelReporterObservation> observations = [];
        ImmutableArray<SentinelDownReport> reports = [];
        foreach (var original in Observations)
        {
            var observed = original.ContextCaptured ? original : original with
            {
                OwnerAtObservation = original.OwnerAtObservation ?? owner,
                EpochAtObservation = epoch,
                ContextCaptured = true,
            };
            observations = AddObservation(observations, observed);
            if (observed.Kind == SentinelObservationKind.Down && observed.Source is { } primary)
                reports = AddDownReport(reports, new(primary, observed.Reporter, observed.OwnerAtObservation));
        }
        return new(observations, reports, IsDownOnly);
    }

    internal SentinelReporterLedger WithSourceEvidence(SentinelAddressEvidence evidence)
    {
        var observations = Observations;
        for (var index = 0; index < observations.Length; index++)
        {
            var observed = observations[index];
            if (!SentinelEndpointIdentity.SameEndpoint(observed.Source, evidence.Endpoint)) continue;
            var updated = observed.WithDns(evidence);
            if (updated.CompletedDns != observed.CompletedDns) observations = observations.SetItem(index, updated);
        }
        return observations == Observations ? this : new(observations, DownReports, IsDownOnly);
    }

    private static ImmutableArray<SentinelDownReport> AddDownReport(
        ImmutableArray<SentinelDownReport> reports, SentinelDownReport report)
    {
        var comparer = SentinelEndpointIdentity.EndpointComparer.Instance;
        foreach (var known in reports)
            if (comparer.Equals(known.Primary, report.Primary) && comparer.Equals(known.Reporter, report.Reporter)
                && known.OwnerAtObservation == report.OwnerAtObservation) return reports;
        return reports.Add(report);
    }

    private static ImmutableArray<SentinelReporterObservation> AddObservation(
        ImmutableArray<SentinelReporterObservation> observations, SentinelReporterObservation candidate)
    {
        for (var index = 0; index < observations.Length; index++)
        {
            var known = observations[index];
            if (!known.HasSameObservation(candidate)) continue;
            var combined = known;
            foreach (var evidence in candidate.CompletedDns) combined = combined.WithDns(evidence);
            return combined.CompletedDns == known.CompletedDns ? observations : observations.SetItem(index, combined);
        }
        return observations.Add(candidate);
    }
}
