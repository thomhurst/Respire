using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class ReadFallbackPolicyTests
{
    [Test]
    public async Task ReplicaCandidatesPreserveHealthZoneAndRotationOrder()
    {
        var candidates = new ReadFallbackPolicy.ReplicaCandidates<int>();
        await Assert.That(candidates.Offer(1, local: false, linked: false, RespireReadFrom.AzAffinity)).IsFalse();
        await Assert.That(candidates.Offer(2, local: true, linked: false, RespireReadFrom.AzAffinity)).IsFalse();
        await Assert.That(candidates.Offer(3, local: true, linked: false, RespireReadFrom.AzAffinity)).IsFalse();
        await Assert.That(candidates.Offer(4, local: false, linked: true, RespireReadFrom.AzAffinity)).IsFalse();
        await Assert.That(candidates.Offer(5, local: false, linked: true, RespireReadFrom.AzAffinity)).IsFalse();
        await Assert.That(candidates.TryTake(out var linked)).IsTrue();
        await Assert.That(linked).IsEqualTo(4);
        await Assert.That(candidates.TryTake(out var unlinked)).IsTrue();
        await Assert.That(unlinked).IsEqualTo(2);
        await Assert.That(candidates.TryTake(out _)).IsFalse();
        await Assert.That(candidates.Offer(6, local: true, linked: true, RespireReadFrom.AzAffinity)).IsTrue();
        await Assert.That(candidates.Offer(7, local: false, linked: true, RespireReadFrom.Replica)).IsTrue();
    }

    [Test]
    public async Task RoleSwitchPinsRecoveryToTheOtherRole()
    {
        await Assert.That(ReadFallbackPolicy.AfterRoleSwitch(selectedReplica: false)).IsEqualTo(RespireReadFrom.Primary);
        await Assert.That(ReadFallbackPolicy.AfterRoleSwitch(selectedReplica: true)).IsEqualTo(RespireReadFrom.Replica);
    }

    [Test]
    public async Task OnlyPreferredPoliciesSwitchRolesForAvailabilityErrors()
    {
        foreach (var policy in new[] { RespireReadFrom.Primary, RespireReadFrom.Replica,
                     RespireReadFrom.PrimaryPreferred, RespireReadFrom.ReplicaPreferred,
                     RespireReadFrom.AzAffinity, RespireReadFrom.AzAffinityReplicasAndPrimary })
        foreach (var onReplica in new[] { false, true })
        foreach (var code in new[] { "LOADING", "MASTERDOWN", "CLUSTERDOWN", "ERR", "MOVED", "ASK" })
        {
            var error = new RespireServerException($"{code} unavailable");
            var expected = (code is "LOADING" or "MASTERDOWN" or "CLUSTERDOWN")
                && (policy == RespireReadFrom.ReplicaPreferred && onReplica
                    || policy == RespireReadFrom.PrimaryPreferred && !onReplica
                    || ReadFallbackPolicy.UsesAvailabilityZone(policy));
            await Assert.That(ReadFallbackPolicy.CanFallBackToOtherRole(error, policy, 123, onReplica)).IsEqualTo(expected);
            await Assert.That(ReadFallbackPolicy.CanFallBackToOtherRole(error, policy, null, onReplica)).IsFalse();
            await Assert.That(ReadFallbackPolicy.IsStrictReplicaAsk(error, policy))
                .IsEqualTo(policy == RespireReadFrom.Replica && code == "ASK");
        }
    }
}
