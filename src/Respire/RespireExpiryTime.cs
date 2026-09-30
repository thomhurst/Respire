namespace Respire;

/// <summary>The resolution requested from an absolute expiry-time command.</summary>
public enum ExpiryTimePrecision
{
    /// <summary>Preserve millisecond resolution (HPEXPIRETIME).</summary>
    Milliseconds,

    /// <summary>Use whole-second resolution, rounded up by the server (HEXPIRETIME).</summary>
    Seconds,
}

/// <summary>
/// An absolute expiration time, distinguishing a missing key or field from one without an expiry.
/// The default value represents a missing key or field.
/// </summary>
public readonly struct RespireExpiryTime
{
    private RespireExpiryTime(bool exists, long? unixTimeMilliseconds)
    {
        Exists = exists;
        UnixTimeMilliseconds = unixTimeMilliseconds;
    }

    /// <summary>Whether the key or field exists.</summary>
    public bool Exists { get; }

    /// <summary>Whether the key or field exists and has an expiry set.</summary>
    public bool HasExpiry => UnixTimeMilliseconds.HasValue;

    /// <summary>
    /// Absolute Unix timestamp in milliseconds, or null when missing or persistent.
    /// This preserves Redis timestamps outside the range supported by DateTimeOffset.
    /// </summary>
    public long? UnixTimeMilliseconds { get; }

    /// <summary>Absolute expiration in UTC, or null when missing or persistent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The Redis timestamp exceeds the range of DateTimeOffset. Use UnixTimeMilliseconds to
    /// inspect such timestamps without conversion, or TryGetExpiresAt for a nonthrowing conversion.
    /// </exception>
    public DateTimeOffset? GetExpiresAt() => UnixTimeMilliseconds is { } timestamp
        ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
        : null;

    /// <summary>Attempts to convert an expiry to a UTC instant without throwing.</summary>
    /// <returns>False for missing/persistent values or timestamps outside DateTimeOffset's range.</returns>
    public bool TryGetExpiresAt(out DateTimeOffset expiresAt)
    {
        if (UnixTimeMilliseconds is { } timestamp
            && timestamp <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
            return true;
        }
        expiresAt = default;
        return false;
    }

    internal static RespireExpiryTime FromRedis(long timestamp, ExpiryTimePrecision precision)
        => timestamp switch
        {
            -2 => new RespireExpiryTime(exists: false, unixTimeMilliseconds: null),
            -1 => new RespireExpiryTime(exists: true, unixTimeMilliseconds: null),
            > long.MaxValue / 1000 when precision == ExpiryTimePrecision.Seconds
                => throw new RespireProtocolException($"Expiry timestamp cannot be represented in milliseconds: {timestamp}."),
            >= 0 => new RespireExpiryTime(exists: true, precision == ExpiryTimePrecision.Seconds
                ? checked(timestamp * 1000) : timestamp),
            _ => throw new RespireProtocolException($"Unexpected expiry timestamp: {timestamp}."),
        };
}
