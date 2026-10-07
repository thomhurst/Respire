using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterScanPassCertifierTests
{
    [Test]
    public async Task NoEligibleOwnerSlotResetsPassWithoutIndexingPastBounds()
    {
        var state = new ClusterScanState(null, null, null) { ActiveNode = "previous", RunId = "previous-run" };
        Array.Fill(state.Owners, "other");
        await Assert.That(ClusterScanPassCertifier.BeginOwnerRange(state, new bool[16384], "owner", 1, "run", out var slot)).IsFalse();
        await Assert.That(slot).IsEqualTo(16384);
        await Assert.That(state.ActiveNode).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TerminalBootstrapCertifiesOnlySelectedSlotAndRedirectCertifiesNone(bool redirected)
    {
        var state = State();
        await Assert.That(ClusterScanPassCertifier.ApplyPosition(state, "0", 42, true, redirected, true)).IsEqualTo(!redirected);
        await Assert.That(state.PassSlots.Count(static bit => bit)).IsEqualTo(redirected ? 0 : 1);
        await Assert.That(state.Completed.Any(static bit => bit)).IsFalse();
    }

    [Test]
    public async Task EmptyRedirectBootstrapKeepsOriginalOwnerWithoutCertifyingSlots()
    {
        var state = State();
        const string cursor = "position-{owner}-opaque";
        var slot = ClusterHash.GetSlot(cursor);
        await Assert.That(ClusterScanPassCertifier.ApplyPosition(state, cursor, slot, true, true, true)).IsFalse();
        await Assert.That(state.ValkeyCursor).IsEqualTo(cursor);
        await Assert.That(state.ActiveNode).IsEqualTo("owner");
        await Assert.That(state.Completed.Any(static bit => bit)).IsFalse();
    }

    [Test]
    public async Task RangeBoundaryTrimsCertificationAndBackwardCursorFails()
    {
        var state = State();
        const string cursor = "position-{owner}-opaque";
        var slot = ClusterHash.GetSlot(cursor);
        state.PassSlots[slot] = false;
        await Assert.That(ClusterScanPassCertifier.ApplyPosition(state, cursor, 0, false, false, false)).IsTrue();
        await Assert.That(state.PassSlots.Count(static bit => bit)).IsEqualTo(slot);
        await Assert.That(() => ClusterScanPassCertifier.ApplyPosition(state, cursor, slot + 1, false, false, false))
            .Throws<RespireProtocolException>();
    }

    private static ClusterScanState State()
    {
        var state = new ClusterScanState(null, null, null) { ActiveNode = "owner", RunId = "run" };
        Array.Fill(state.Owners, "owner");
        Array.Fill(state.PassSlots, true);
        return state;
    }
}
