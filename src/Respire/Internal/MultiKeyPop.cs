using Respire.Protocol;

namespace Respire.Internal;

internal static class MultiKeyPop
{
    internal static void CopyPopKeys(
        RespireClient client, ReadOnlySpan<RespireKey> keys, Span<RespireValue> destination, string operation)
    {
        if (keys.IsEmpty)
        {
            throw new ArgumentException("At least one key is required.", nameof(keys));
        }
        int? slot = null;
        for (var index = 0; index < keys.Length; index++)
        {
            var key = client.Key(in keys[index]);
            if (client.Core.Cluster is not null && key.TryGetClusterSlot(out var keySlot))
            {
                if (slot is { } expected && keySlot != expected)
                {
                    throw new RespireServerException("CROSSSLOT Keys in request don't hash to the same slot", operation);
                }
                slot = keySlot;
            }
            destination[index] = key;
        }
    }

    internal static void ValidateWait(TimeSpan waitFor)
    {
        if (waitFor < TimeSpan.Zero && waitFor != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(waitFor), waitFor, "Wait must be nonnegative or Timeout.InfiniteTimeSpan.");
        }
    }

    internal static RespireKey ParsePoppedKey(in RespValue value, ReadOnlySpan<byte> prefix)
    {
        var bytes = value.AsSpan();
        if (!prefix.IsEmpty)
        {
            if (!bytes.StartsWith(prefix))
            {
                throw new RespireProtocolException("Returned key does not start with the client key prefix.");
            }
            bytes = bytes[prefix.Length..];
        }
        return new RespireKey(bytes.ToArray());
    }

    // Redis zero means infinite blocking; finite waits use at least one millisecond.
    internal static RespireValue ToSeconds(TimeSpan waitFor)
        => waitFor == Timeout.InfiniteTimeSpan ? 0 : Math.Max(waitFor.TotalSeconds, 0.001);
}
