using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Protocol;

/// <summary>Shared aggregate framing and storage rules; see docs/RESP_PARSER_LIMITS.md.</summary>
internal static class RespAggregateStorage
{
    // Preserve the existing 257-level Sentinel regression while bounding recursive work.
    internal const int MaxDepth = 512;

    /// <summary>Validates a non-null aggregate's depth and converts its declared count to element slots.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryValidate(long declaredCount, bool pairCount, int depth, out int count)
    {
        count = 0;
        if (depth >= MaxDepth || declaredCount < 0 || declaredCount > (pairCount ? int.MaxValue / 2 : int.MaxValue))
            return false;
        count = (int)(pairCount ? declaredCount * 2 : declaredCount);
        return true;
    }

    /// <summary>Grows a full frame for a completed child, transferring ownership of its existing children.</summary>
    // A header alone rents nothing.
    internal static void Grow(ref RespValue[] elements, int count)
    {
        // Large frames have already delivered at least 256 children. Quadrupling reduces
        // copies while keeping speculative capacity proportional to completed children.
        var growthFactor = elements.Length < 256 ? 2L : 4L;
        var capacity = (int)Math.Min(count, elements.Length == 0 ? 16L : elements.Length * growthFactor);
        var replacement = RespirePools.ValueArrays.Rent(capacity);
        if (elements.Length != 0)
        {
            elements.CopyTo(replacement, 0);
            // Ownership moves to the replacement; do not dispose the transferred children.
            RespirePools.ValueArrays.Return(elements, clearArray: true);
        }
        elements = replacement;
    }
}
