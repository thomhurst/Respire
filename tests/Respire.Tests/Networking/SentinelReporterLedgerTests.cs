using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelReporterLedgerTests
{
    private static readonly RespireEndpoint A = new("192.0.2.1", 6379);
    private static readonly RespireEndpoint B = new("192.0.2.2", 6379);
    private static readonly RespireEndpoint First = new("first.test", 26379);
    private static readonly RespireEndpoint Second = new("second.test", 26379);

    private static SentinelEpochEvidence Epoch(long accepted, long observed)
        => new(accepted, observed, A, new(A, [A.Host]), A);

    [Test]
    public async Task MixedHistoryRetainsReporterOutagesWithoutBindingIndependentRecovery()
    {
        var owner = new SentinelValidatedPrimary(A, A);
        var first = SentinelHint.FromDown("down", First, A, owner).CaptureObservationContext(owner, Epoch(3, 4));
        var second = SentinelHint.FromDown("down", Second, B, owner).CaptureObservationContext(owner, Epoch(3, 4));
        var merged = SentinelNotificationCoalescer.Merge(first, second);
        await Assert.That(merged.DownReports.Length).IsEqualTo(2);
        var mixed = SentinelNotificationCoalescer.Merge(merged, SentinelHint.FromGap(Second));
        await Assert.That(mixed.DownReports).IsEmpty();
        await Assert.That(mixed.BindDownReportsToCurrentPrimary(owner).DownReportPrimary).IsNull();
        await Assert.That(mixed.Ledger.DownReports).IsEquivalentTo(merged.DownReports);
        await Assert.That(mixed.Ledger.Observations.Length).IsEqualTo(3);
        await Assert.That(mixed.Ledger.Observations.Where(item => item.Kind == SentinelObservationKind.Down)
            .Select(item => item.Reporter)).IsEquivalentTo([First, Second]);
        await Assert.That(first.Ledger.Observations[0].EpochAtObservation.AcceptedEpoch).IsEqualTo(3);
        await Assert.That(first.Ledger.Observations[0].EpochAtObservation.ObservedEpoch).IsEqualTo(4);
    }

    [Test]
    public async Task CapturedOwnersAndEpochsNeverRebindIncludingAnAbsentOwner()
    {
        var hostname = new RespireEndpoint("primary.test", 6379);
        var firstOwner = new SentinelValidatedPrimary(hostname, A);
        var nextOwner = new SentinelValidatedPrimary(hostname, B);
        var original = SentinelHint.FromDown("down", First, hostname).CaptureObservationContext(firstOwner, Epoch(3, 4));
        var later = original.CaptureObservationContext(nextOwner, Epoch(5, 5));
        await Assert.That(later.Ledger.Observations == original.Ledger.Observations).IsTrue();
        await Assert.That(later.DownReports[0].OwnerAtObservation).IsEqualTo(firstOwner);
        await Assert.That(later.DownKey!.Value.Peer).IsEqualTo(A);
        var next = SentinelHint.FromDown("down", First, hostname).CaptureObservationContext(nextOwner, Epoch(5, 5));
        await Assert.That(next.DownKey == original.DownKey).IsFalse();
        var missing = SentinelHint.FromGap(First).CaptureObservationContext(null, default);
        var afterPublication = missing.CaptureObservationContext(nextOwner, Epoch(5, 5));
        await Assert.That(afterPublication.Ledger.Observations[0].OwnerAtObservation).IsNull();
        await Assert.That(afterPublication.Ledger.Observations[0].EpochAtObservation.ObservedEpoch).IsNull();
    }

    [Test]
    public async Task NewEpochEvidenceFromAnExistingReporterRequiresAnotherPass()
    {
        var owner = new SentinelValidatedPrimary(A, A);
        var hint = SentinelHint.FromSwitchMaster("a-b", A, B, First).CaptureObservationContext(owner, Epoch(3, 3));
        var active = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.Offer, hint)).State;
        var duplicate = active.Transition(new(SentinelNotificationEventKind.Offer, hint)).State;
        await Assert.That(duplicate.Pending).IsNull();
        var newer = SentinelHint.FromSwitchMaster("a-b", A, B, First).CaptureObservationContext(owner, Epoch(3, 4));
        var pending = duplicate.Transition(new(SentinelNotificationEventKind.Offer, newer)).State;
        await Assert.That(pending.Pending!.Value.MustRediscover).IsTrue();
        await Assert.That(pending.Pending.Value.Ledger.Observations.Select(item => item.EpochAtObservation.ObservedEpoch))
            .IsEquivalentTo(new long?[] { 3, 4 });
        await Assert.That(active.Active!.Value.Ledger.Observations.Length).IsEqualTo(1);
    }

    [Test]
    public async Task ReconciliationConsumesEffectiveFenceWithoutErasingOriginalEvidence()
    {
        var owner = new SentinelValidatedPrimary(A, A);
        var first = SentinelHint.FromSwitchMaster("a-b", A, B, First).CaptureObservationContext(owner, Epoch(3, 3));
        var second = SentinelHint.FromSwitchMaster("a-b", A, B, Second).CaptureObservationContext(owner, Epoch(3, 3));
        var merged = SentinelNotificationCoalescer.Merge(first, second).WithSourceAddresses(A, [A.Host]);
        var state = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.Offer, merged)).State;
        var generation = new object();
        var reconciled = state.Transition(new(SentinelNotificationEventKind.AttemptSucceeded),
            new(ValidatedGeneration: generation, ValidatedPrimary: owner,
                CurrentEvidence: new(generation, A, A, false, [A], A)));
        var next = reconciled.State.Active!.Value;
        await Assert.That(next.Sources).IsEmpty();
        await Assert.That(next.Reporters).IsEquivalentTo([Second]);
        await Assert.That(next.ReconciliationPrimary).IsEqualTo(owner);
        await Assert.That(next.Ledger.Observations.Length).IsEqualTo(2);
        foreach (var observation in next.Ledger.Observations)
        {
            await Assert.That(observation.Source).IsEqualTo(A);
            await Assert.That(observation.OwnerAtObservation).IsEqualTo(owner);
            await Assert.That(observation.CompletedDns[0].SingleAddress).IsEqualTo(A.Host);
        }
        var lateDns = next.WithSourceEvidence(new(A, [B.Host]));
        await Assert.That(lateDns.Sources).IsEmpty();
        await Assert.That(lateDns.Ledger.Observations[0].CompletedDns.Length).IsEqualTo(2);
        await Assert.That(next.Ledger.Observations[0].CompletedDns.Length).IsEqualTo(1);
    }

    [Test]
    public async Task RepeatedActiveObservationCannotRestoreItsFenceIntoIndependentFailback()
    {
        var first = SentinelHint.FromSwitchMaster("a-b", A, B, First)
            .CaptureObservationContext(new(A, A), Epoch(3, 3));
        var failback = SentinelHint.FromSwitchMaster("b-a", B, A, Second)
            .CaptureObservationContext(new(B, B), Epoch(4, 4));
        var repeated = SentinelHint.FromSwitchMaster("a-b", A, B, First)
            .CaptureObservationContext(new(B, B), Epoch(4, 5));
        var state = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.Offer, first)).State;
        state = state.Transition(new(SentinelNotificationEventKind.Offer, failback)).State;
        state = state.Transition(new(SentinelNotificationEventKind.Offer, repeated)).State;
        await Assert.That(state.Pending!.Value.Sources.Select(source => source.Endpoint)).IsEquivalentTo([B]);
        await Assert.That(state.Pending.Value.Ledger.Observations.Length).IsEqualTo(2);
        await Assert.That(state.Pending.Value.Ledger.Observations.Select(item => item.Source)).IsEquivalentTo(new RespireEndpoint?[] { A, B });
    }

    [Test]
    public async Task DnsHistoryUnionsAreOrderIndependentAndKeepEarlierSnapshots()
    {
        var source = new RespireEndpoint("former.test", 6379);
        var original = SentinelHint.FromSwitchMaster("switch", source, B, First)
            .CaptureObservationContext(new(A, A), Epoch(3, 4));
        var first = original.WithSourceAddresses(source, ["192.0.2.1", "192.0.2.2"]);
        var equivalent = original.WithSourceAddresses(source, ["::ffff:192.0.2.2", "::ffff:192.0.2.1"]);
        var changed = original.WithSourceAddresses(source, ["192.0.2.3"]);
        var random = new Random(796);
        for (var run = 0; run < 64; run++)
        {
            var inputs = new[] { first, equivalent, changed, first, changed };
            random.Shuffle(inputs);
            var ledger = inputs[0].Ledger;
            foreach (var input in inputs.Skip(1)) ledger = ledger.Union(input.Ledger);
            await Assert.That(ledger.Observations.Length).IsEqualTo(1);
            await Assert.That(ledger.Observations[0].CompletedDns.Length).IsEqualTo(2);
            await Assert.That(ledger.ContainsObservation(first.Ledger.Observations[0])).IsTrue();
            await Assert.That(ledger.ContainsObservation(changed.Ledger.Observations[0])).IsTrue();
        }
        await Assert.That(original.Ledger.Observations[0].CompletedDns).IsEmpty();
        await Assert.That(first.Ledger.Observations[0].CompletedDns.Length).IsEqualTo(1);
        var repeated = first.WithSourceAddresses(source, ["192.0.2.1", "192.0.2.2"]);
        await Assert.That(repeated.Ledger.Observations == first.Ledger.Observations).IsTrue();
    }

    [Test]
    public async Task ContextCaptureCoalescesEquivalentObservationsWithoutLosingDns()
    {
        var owner = new SentinelValidatedPrimary(A, A);
        var unknown = SentinelHint.FromDown("down", First, A);
        var known = SentinelHint.FromDown("down", First, A, owner);
        var merged = SentinelNotificationCoalescer.Merge(unknown, known).CaptureObservationContext(owner, Epoch(3, 3));
        await Assert.That(merged.Ledger.Observations.Length).IsEqualTo(1);
        await Assert.That(merged.DownReports.Length).IsEqualTo(1);
        var duplicate = SentinelNotificationCoalescer.Merge(merged, merged);
        await Assert.That(duplicate.Ledger.Observations == merged.Ledger.Observations).IsTrue();
        await Assert.That(duplicate.DownReports == merged.DownReports).IsTrue();
    }

    [Test]
    public async Task SeededLedgerIdentityPreservesCanonicalAliasesPortsAndObservedOwners()
    {
        var random = new Random(1796);
        for (var iteration = 0; iteration < 256; iteration++)
        {
            var port = random.Next(1000, 60000);
            var peer = new RespireEndpoint($"192.0.2.{random.Next(1, 200)}", port);
            var mapped = new RespireEndpoint("::ffff:" + peer.Host, port);
            var owner = new SentinelValidatedPrimary(new("PRIMARY.test", port), peer);
            var ownerAlias = new SentinelValidatedPrimary(new("primary.TEST", port), mapped);
            var first = SentinelHint.FromDown("down", First, peer, owner).CaptureObservationContext(owner, default);
            var alias = SentinelHint.FromDown("down", new("FIRST.TEST", First.Port), mapped, ownerAlias)
                .CaptureObservationContext(ownerAlias, default);
            var merged = SentinelNotificationCoalescer.Merge(first, alias);
            await Assert.That(merged.Ledger.Observations.Length).IsEqualTo(1);
            await Assert.That(merged.DownReports == first.DownReports).IsTrue();
            var anotherPort = SentinelHint.FromDown("down", First, new(peer.Host, port + 1), owner)
                .CaptureObservationContext(owner, default);
            merged = SentinelNotificationCoalescer.Merge(merged, anotherPort);
            await Assert.That(merged.Ledger.Observations.Length).IsEqualTo(2);
            var nextOwner = new SentinelValidatedPrimary(owner.Endpoint, new("198.51.100.1", port));
            var later = SentinelHint.FromDown("down", First, peer, nextOwner).CaptureObservationContext(nextOwner, default);
            merged = SentinelNotificationCoalescer.Merge(merged, later);
            await Assert.That(merged.Ledger.Observations.Length).IsEqualTo(3);
            await Assert.That(first.Ledger.Observations[0].OwnerAtObservation).IsEqualTo(owner);
        }
    }
}
