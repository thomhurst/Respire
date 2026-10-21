namespace Respire.AI;

internal static class EmbeddingCacheIdentity
{
    internal static bool IsWellFormed(string? value)
    {
        if (value is null) return true;
        for (var i = 0; i < value.Length; i++)
        {
            if (!char.IsSurrogate(value[i])) continue;
            if (!char.IsHighSurrogate(value[i]) || i + 1 == value.Length || !char.IsLowSurrogate(value[i + 1]))
                return false;
            i++;
        }
        return true;
    }
}
