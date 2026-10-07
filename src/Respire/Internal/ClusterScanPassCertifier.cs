namespace Respire.Internal;

internal static class ClusterScanPassCertifier
{
    internal static bool BeginOwnerRange(ClusterScanState state, bool[] moving,
        string nodeId, ulong epoch, string runId, out int slot)
    {
        if (state.ValkeyCursor is { } position)
        {
            slot = ClusterHash.GetSlot(position);
            return true;
        }
        slot = 0;
        while (slot < ClusterHash.SlotCount && (state.Completed[slot] || moving[slot]
            || state.Owners[slot] != nodeId)) slot++;
        state.ResetPass();
        if (slot == ClusterHash.SlotCount) return false;
        state.ActiveNode = nodeId;
        state.RunId = runId;
        state.Epoch = epoch;
        // Only the contiguous, validated owner range can be certified by this pass.
        for (var current = slot; current < ClusterHash.SlotCount && state.Owners[current] == nodeId; current++)
            state.PassSlots[current] = !state.Completed[current] && !moving[current];
        return true;
    }

    // True requests fresh topology/run-ID validation before certifying this pass.
    internal static bool ApplyPosition(ClusterScanState state, string nextCursor,
        int slot, bool starting, bool redirected, bool empty)
    {
        // An empty redirected bootstrap has scanned no data. Keep its opaque
        // position for the original, validated owner without certifying slots.
        var bootstrap = starting && empty && nextCursor != "0" && ClusterHash.GetSlot(nextCursor) == slot;
        if (redirected && !bootstrap)
        {
            state.ResetPass();
            return false;
        }
        if (nextCursor == "0")
        {
            if (starting)
                for (var current = 0; current < ClusterHash.SlotCount; current++) state.PassSlots[current] &= current == slot;
            return true;
        }
        var nextSlot = ClusterHash.GetSlot(nextCursor);
        if (nextSlot < slot) throw new RespireProtocolException("CLUSTERSCAN moved its cursor backwards.");
        if (!state.PassSlots[nextSlot])
        {
            // The cursor crossed into another, completed or migrating range.
            for (var current = nextSlot; current < ClusterHash.SlotCount; current++) state.PassSlots[current] = false;
            return true;
        }
        state.ValkeyCursor = nextCursor;
        return false;
    }
}
