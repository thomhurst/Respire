using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelRetirementTransitionTests
{
    private static readonly RespireEndpoint A = new("192.0.2.1", 6379);
    private static readonly RespireEndpoint B = new("192.0.2.2", 6379);
    private static readonly RespireEndpoint C = new("192.0.2.3", 6379);
    private static readonly RespireEndpoint Reporter = new("sentinel.test", 26379);
    private static readonly RespireEndpoint Source = new("former.test", 6379);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NumericTargetShortcutRequiresAllCommandSlots(bool allSlotsAgree)
    {
        var identity = new object();
        var current = new SentinelGenerationEvidence(identity, A, A, false,
            allSlotsAgree ? [A] : [A, B], allSlotsAgree ? A : (RespireEndpoint?)null);
        var hint = SentinelHint.FromSwitchMaster("b-a", B, A, Reporter);
        var offered = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.Offer, hint),
            new(CurrentEvidence: current));
        await Assert.That(offered.Action).IsEqualTo(allSlotsAgree ? SentinelNotificationAction.None : SentinelNotificationAction.RunNext);
        await Assert.That(offered.RetireGeneration).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task DelayedDnsProtectsInterveningEndpointOrPeer(bool sameEndpoint, bool samePeer)
    {
        var arrived = new SentinelGenerationEvidence(new object(), new("original.test", 6379), A, false, [A], A);
        var current = new SentinelGenerationEvidence(new object(), sameEndpoint ? arrived.Endpoint : new("replacement.test", 6379),
            samePeer ? A : B, false, samePeer ? [A] : [B], samePeer ? A : B);
        var lookup = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.BeginSourceResolution,
            SentinelHint.FromSwitchMaster("source", Source, C, Reporter)));
        var completed = lookup.State.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(Source, [current.ValidatedPeer!.Value.Host])),
            new(CurrentEvidence: current, LookupGeneration: arrived));
        if (sameEndpoint || samePeer)
        {
            await Assert.That(completed.Action).IsEqualTo(SentinelNotificationAction.None);
            await Assert.That(completed.RetireGeneration).IsNull();
        }
        else
        {
            await Assert.That(completed.Action).IsEqualTo(SentinelNotificationAction.RunNext);
            await Assert.That(completed.RetireGeneration).IsSameReferenceAs(current.Identity);
        }
        await Assert.That(lookup.State.SourceResolutions[lookup.ResolutionId].Sources[0].Addresses.IsDefault).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OnlyTargetsOfferedDuringLookupProtectTheLaterGeneration(bool offeredDuringLookup)
    {
        var arrived = new SentinelGenerationEvidence(new object(), new("original.test", 6379), A, false, [A], A);
        var current = new SentinelGenerationEvidence(new object(), new("replacement.test", 6379), B, false, [B], B);
        var protection = SentinelHint.FromSwitchMaster("protection", C, B, Reporter);
        var state = new SentinelNotificationState();
        if (!offeredDuringLookup) state = state.Transition(new(SentinelNotificationEventKind.Offer, protection)).State;
        var lookup = state.Transition(new(SentinelNotificationEventKind.BeginSourceResolution,
            SentinelHint.FromSwitchMaster("source", Source, C, Reporter)));
        state = lookup.State;
        if (offeredDuringLookup) state = state.Transition(new(SentinelNotificationEventKind.Offer, protection)).State;
        state = state.Transition(new(SentinelNotificationEventKind.AttemptSucceeded),
            new(ValidatedGeneration: current.Identity, ValidatedPrimary: new(B, B), CurrentEvidence: current)).State;
        await Assert.That(state.Phase).IsEqualTo(SentinelNotificationPhase.Idle);
        var completed = state.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(Source, [B.Host])),
            new(CurrentEvidence: current, LookupGeneration: arrived));
        await Assert.That(completed.RetireGeneration is not null).IsEqualTo(!offeredDuringLookup);
        await Assert.That(completed.State.SourceResolutions[lookup.ResolutionId].Targets.Contains(B)).IsEqualTo(offeredDuringLookup);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task TargetDnsOverlapRequiresUnambiguousProofAndPreservesKnownPeer(bool knownPeer, bool ambiguous)
    {
        var target = new RespireEndpoint("promoted.test", 6379);
        var current = new SentinelGenerationEvidence(new object(), new("primary.test", 6379), knownPeer ? B : A, false, [B], B);
        var lookup = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.BeginSourceResolution,
            SentinelHint.FromSwitchMaster("source", Source, target, Reporter)));
        var completed = lookup.State.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(Source, [B.Host]),
            TargetAddresses: [new(target, ambiguous ? [B.Host, C.Host] : [B.Host])]),
            new(CurrentEvidence: current, LookupGeneration: current));
        await Assert.That(completed.RetireGeneration is not null).IsEqualTo(knownPeer || ambiguous);
        var retained = completed.State.SourceResolutions[lookup.ResolutionId];
        await Assert.That(retained.Ledger.Observations[0].CompletedDns.Length).IsEqualTo(2);
        await Assert.That(retained.Ledger.Observations[0].CompletedDns.Single(item => item.Endpoint == Source).Addresses)
            .IsEquivalentTo([B.Host]);
        await Assert.That(retained.Sources[0].Addresses.IsDefault).IsEqualTo(!knownPeer && !ambiguous);
    }

    [Test]
    public async Task DisposalCannotProduceLateRetirementOrRediscovery()
    {
        var current = new SentinelGenerationEvidence(new object(), A, A, false, [A], A);
        var lookup = new SentinelNotificationState().Transition(new(SentinelNotificationEventKind.BeginSourceResolution,
            SentinelHint.FromSwitchMaster("source", Source, B, Reporter)));
        var disposed = lookup.State.Transition(new(SentinelNotificationEventKind.Dispose)).State;
        var late = disposed.Transition(new(SentinelNotificationEventKind.SourceResolved,
            ResolutionId: lookup.ResolutionId, AddressEvidence: new(Source, [A.Host])),
            new(CurrentEvidence: current, LookupGeneration: current));
        await Assert.That(late.Action).IsEqualTo(SentinelNotificationAction.Stop);
        await Assert.That(late.RetireGeneration).IsNull();
        await Assert.That(late.State).IsEqualTo(disposed);
    }
}
