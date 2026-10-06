using System.Runtime.CompilerServices;
using Respire.Networking;

namespace Respire.Protocol;

/// <summary>Shared aggregate framing and storage rules; see docs/RESP_PARSER_LIMITS.md.</summary>
internal static class RespAggregateStorage
{
    // Preserve the existing 257-level Sentinel regression while bounding recursive work.
    internal const int MaxDepth = 512;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryGetCount(long declaredCount, bool pairCount, out int count)
    {
        count = 0;
        if (declaredCount < 0 || declaredCount > (pairCount ? int.MaxValue / 2 : int.MaxValue))
            return false;
        count = (int)(pairCount ? declaredCount * 2 : declaredCount);
        return true;
    }

    // Called only when a complete child needs a slot. A header alone rents nothing.
    internal static void Grow(ref RespValue[] elements, int count)
    {
        var capacity = (int)Math.Min(count, elements.Length == 0 ? 16L : elements.Length * 2L);
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
