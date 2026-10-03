using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelNotificationStateTests
{
    private static readonly RespireEndpoint A = new("127.0.0.1", 6379);
    private static readonly RespireEndpoint B = new("127.0.0.1", 6380);
    private static readonly RespireEndpoint First = new("first", 26379);
    private static readonly RespireEndpoint Second = new("second", 26379);

    [Test]
    public async Task DnsTransitionsRetainOnlyLookupLifetimeEvidenceThroughCompletionAndDisposal()
    {
        var c = new RespireEndpoint("127.0.0.1", 6381);
        var generation = new SentinelGenerationIdentity();
        var state = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.Offer,
            SentinelHint.FromSwitchMaster("earlier", c, A, First))).State;
        var lookup = state.Transition(new(SentinelNotificationEventKind.BeginSourceResolution,
            SentinelHint.FromSwitchMaster("lookup", A, B, First)));
        state = lookup.State.Transition(new(SentinelNotificationEventKind.AttemptSucceeded),
            new(ValidatedGeneration: generation, ValidatedPrimary: new(A, A),
                CurrentEvidence: new(generation, A, A, false, [A], A))).State;
        await Assert.That(state.Phase).IsEqualTo(SentinelNotificationPhase.Idle);
        state = state.Transition(new(SentinelNotificationEventKind.Offer, SentinelHint.FromDown("down", Second, B))).State;
        state = state.Transition(new(SentinelNotificationEventKind.Offer,
            SentinelHint.FromSwitchMaster("later", B, c, Second))).State;
        var unresolved = state;
        state = state.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(A, ["192.0.2.1"]))).State;
        var retained = state.SourceResolutions[lookup.ResolutionId];
        await Assert.That(retained.Targets).IsEquivalentTo([B, c]);
        await Assert.That(retained.Sources.Select(source => source.Endpoint)).IsEquivalentTo([A, B]);
        await Assert.That(retained.Sources.Single(source => source.Endpoint == A).Addresses).IsEquivalentTo(["192.0.2.1"]);
        await Assert.That(unresolved.SourceResolutions[lookup.ResolutionId].Sources
            .Single(source => source.Endpoint == A).Addresses.IsDefault).IsTrue();
        var disposed = state.Transition(new(SentinelNotificationEventKind.Dispose)).State;
        var late = disposed.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(A, ["192.0.2.2"])));
        await Assert.That(late.Action).IsEqualTo(SentinelNotificationAction.Stop);
        await Assert.That(late.State).IsEqualTo(disposed);
        var ended = disposed.Transition(new(SentinelNotificationEventKind.EndSourceResolution,
            ResolutionId: lookup.ResolutionId)).State;
        await Assert.That(ended.SourceResolutions).IsEmpty();
        await Assert.That(disposed.SourceResolutions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task CallerArrayMutationsCannotChangeRetainedEvidence()
    {
        string[] addresses = ["192.0.2.1"];
        RespireEndpoint[] targets = [B];
        SentinelSwitchSource[] sources = [new(A, addresses)];
        RespireEndpoint[] reporters = [First];
        var hint = SentinelHintBuilder.Create("switch", targets, sources, reporters, false);
        var state = new SentinelNotificationState().Offer(hint, false, out _);

        addresses[0] = "192.0.2.2";
        targets[0] = A;
        sources[0] = new(B, null);
        reporters[0] = Second;

        var retained = state.Active!.Value;
        await Assert.That(retained.Targets).IsEquivalentTo([B]);
        await Assert.That(retained.Reporters).IsEquivalentTo([First]);
        await Assert.That(retained.Sources[0].Endpoint).IsEqualTo(A);
        await Assert.That(retained.Sources[0].Addresses).IsEquivalentTo(["192.0.2.1"]);
    }

    [Test]
    public async Task OfferAndTakePendingPreserveEarlierStateSnapshots()
    {
        var idle = default(SentinelNotificationState);
        var active = idle.Offer(SentinelHint.FromGap(First), false, out var start);
        var pending = active.Offer(SentinelHint.FromGap(Second), false, out var restart);
        var taken = pending.TakePending(false, B, B, out var next);

        await Assert.That(start).IsTrue();
        await Assert.That(restart).IsFalse();
        await Assert.That(idle.Phase).IsEqualTo(SentinelNotificationPhase.Idle);
        await Assert.That(idle.Active).IsNull();
        await Assert.That(active.Phase).IsEqualTo(SentinelNotificationPhase.Active);
        await Assert.That(active.Pending).IsNull();
        await Assert.That(pending.Phase).IsEqualTo(SentinelNotificationPhase.ActivePending);
        await Assert.That(taken.Phase).IsEqualTo(SentinelNotificationPhase.Active);
        await Assert.That(taken.Active).IsEqualTo(next);
        await Assert.That(pending.Pending).IsNotNull();
    }

    [Test]
    public async Task LookupSnapshotsRetainOnlyEvidenceOfferedDuringTheirLifetime()
    {
        var idle = default(SentinelNotificationState);
        var earlier = idle.Offer(SentinelHint.FromSwitchMaster("earlier", A, B, First), false, out _);
        var lookup = earlier.BeginSourceResolution(SentinelHintBuilder.Create("lookup"), out var id);
        var offered = lookup.Offer(SentinelHint.FromSwitchMaster("later", B, A, Second), false, out _);
        var complete = offered.Complete();
        var ended = complete.EndSourceResolution(id);

        await Assert.That(idle.SourceResolutions.Count).IsEqualTo(0);
        await Assert.That(lookup.SourceResolutions[id].Sources.Length).IsEqualTo(0);
        await Assert.That(offered.SourceResolutions[id].Sources.Select(source => source.Endpoint)).IsEquivalentTo([B]);
        await Assert.That(complete.Phase).IsEqualTo(SentinelNotificationPhase.Idle);
        await Assert.That(complete.SourceResolutions[id]).IsEqualTo(offered.SourceResolutions[id]);
        await Assert.That(ended.SourceResolutions.Count).IsEqualTo(0);
        await Assert.That(complete.SourceResolutions.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SupersessionPreservesPendingEvidenceAndLeavesTheOldSnapshotIntact()
    {
        var active = new SentinelNotificationState().Offer(
            SentinelHint.FromSwitchMaster("a-b", A, B, First), false, out _);
        var pending = active.Offer(SentinelHint.FromSwitchMaster("b-a", B, A, Second), false, out _);
        var superseded = pending.SupersedeActive();
        var complete = superseded.Complete();

        await Assert.That(superseded.Active).IsEqualTo(pending.Pending);
        await Assert.That(superseded.Pending).IsNull();
        await Assert.That(pending.Phase).IsEqualTo(SentinelNotificationPhase.ActivePending);
        await Assert.That(complete.Phase).IsEqualTo(SentinelNotificationPhase.Idle);
        await Assert.That(superseded.Active).IsNotNull();
    }

    [Test]
    [Arguments(0, 1)]
    [Arguments(1, 1)]
    [Arguments(2, 2)]
    [Arguments(5, 16)]
    [Arguments(6, 30)]
    [Arguments(int.MaxValue, 30)]
    public async Task DefaultRetryDelayBacksOffExponentiallyToThirtySeconds(int attempts, int expectedSeconds)
    {
        await Assert.That(SentinelNotificationState.DefaultRetryDelay(attempts)).IsEqualTo(TimeSpan.FromSeconds(expectedSeconds));
    }
}
