namespace Respire.Internal;

/// <summary>Shared cardinality count, keys, optional approximation and limit arguments.</summary>
internal static class CountedKeyArguments
{
    internal static RespireValue[] Create(RespireClient client, ReadOnlySpan<RespireKey> keys, long limit,
        bool approximate = false)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var arguments = new RespireValue[keys.Length + (limit == 0 ? 1 : 3) + (approximate ? 1 : 0)];
        arguments[0] = keys.Length;
        for (var index = 0; index < keys.Length; index++)
        {
            arguments[index + 1] = client.Key(in keys[index]);
        }
        if (approximate) arguments[keys.Length + 1] = "APPROX";
        if (limit != 0)
        {
            arguments[^2] = "LIMIT";
            arguments[^1] = limit;
        }
        return arguments;
    }
}
