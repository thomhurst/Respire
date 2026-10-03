using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class SentinelEpochEvidenceTests
{
    private static readonly RespireEndpoint Host = new("primary.test", 6379);
    private static readonly RespireEndpoint First = new("127.0.0.1", 6379);
    private static readonly RespireEndpoint Second = new("127.0.0.2", 6379);

    [Test]
    public async Task ObservationRaisesOnlyTheObservedFloorAndPreservesItsInput()
    {
        var before = default(SentinelEpochEvidence);
        await Assert.That(before.TryObserve(Host, 10, [First.Host, Second.Host], out var observed)).IsTrue();

        await Assert.That(before).IsEqualTo(default(SentinelEpochEvidence));
        await Assert.That(observed.ObservedEpoch).IsEqualTo(10);
        await Assert.That(observed.AcceptedEpoch).IsNull();
        await Assert.That(observed.ValidatedPeer).IsNull();
        await Assert.That(observed.ObservedAddresses.SingleAddress).IsNull();
        await Assert.That(observed.IsCurrent(Second, 10)).IsFalse();
    }

    [Test]
    public async Task RoleAcceptanceNarrowsProvisionalAddressesToOnePhysicalPeer()
    {
        var before = default(SentinelEpochEvidence);
        before.TryObserve(Host, 10, [First.Host, Second.Host], out var observed);
        var result = observed.Accept(Host, 10, [First.Host, Second.Host], First, out var accepted);

        await Assert.That(result).IsEqualTo(SentinelConfigurationRejection.None);
        await Assert.That(observed.AcceptedEpoch).IsNull();
        await Assert.That(observed.ValidatedPeer).IsNull();
        await Assert.That(accepted.AcceptedEpoch).IsEqualTo(10);
        await Assert.That(accepted.ValidatedPeer).IsEqualTo(First);
        await Assert.That(accepted.IsCurrent(new("::ffff:127.0.0.1", 6379), null)).IsTrue();
        await Assert.That(accepted.IsCurrent(Second, 10)).IsFalse();
    }

    [Test]
    public async Task SameEpochHostnameCannotReplaceItsAcceptedPeer()
    {
        default(SentinelEpochEvidence).Accept(Host, 10, [First.Host], First, out var accepted);
        var result = accepted.Accept(Host, 10, [Second.Host], Second, out var rejected);

        await Assert.That(result).IsEqualTo(SentinelConfigurationRejection.DifferentPeer);
        await Assert.That(rejected).IsEqualTo(accepted);
        await Assert.That(accepted.ValidatedPeer).IsEqualTo(First);
    }

    [Test]
    public async Task NewerObservationKeepsAcceptedFloorUntilSuccessfulValidation()
    {
        default(SentinelEpochEvidence).Accept(Host, 10, [First.Host], First, out var accepted);
        await Assert.That(accepted.TryObserve(Host, 11, [Second.Host], out var observed)).IsTrue();
        await Assert.That(observed.ObservedEpoch).IsEqualTo(11);
        await Assert.That(observed.AcceptedEpoch).IsEqualTo(10);
        await Assert.That(observed.ValidatedPeer).IsNull();
        await Assert.That(accepted.ValidatedPeer).IsEqualTo(First);
        await Assert.That(observed.TryObserve(First, 10, null, out var stale)).IsFalse();
        await Assert.That(stale).IsEqualTo(observed);

        await Assert.That(observed.Accept(Host, 11, [Second.Host], Second, out var next))
            .IsEqualTo(SentinelConfigurationRejection.None);
        await Assert.That(next.AcceptedEpoch).IsEqualTo(11);
        await Assert.That(next.ValidatedPeer).IsEqualTo(Second);
    }

    [Test]
    public async Task MissingEpochKeepsMetadataFreeRecoveryUntilAnEpochIsObserved()
    {
        var before = default(SentinelEpochEvidence);
        await Assert.That(before.Accept(First, null, null, First, out var first))
            .IsEqualTo(SentinelConfigurationRejection.None);
        await Assert.That(first.IsCurrent(Second, null)).IsTrue();
        await Assert.That(first.Accept(Second, null, null, Second, out var second))
            .IsEqualTo(SentinelConfigurationRejection.None);
        await Assert.That(second.ObservedEpoch).IsNull();
        await Assert.That(second.AcceptedEpoch).IsNull();
    }
}
