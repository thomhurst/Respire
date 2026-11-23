namespace Respire;

/// <summary>The resolution requested from an absolute expiry-time command.</summary>
public enum RespireExpiryTimePrecision
{
    /// <summary>Preserve millisecond resolution (PEXPIRETIME/HPEXPIRETIME).</summary>
    Milliseconds,

    /// <summary>Use the server's whole-second resolution (EXPIRETIME/HEXPIRETIME).</summary>
    Seconds,
}

/// <summary>
/// An absolute expiration time, distinguishing a missing key or field from one without an expiry.
/// The default value represents a missing key or field.
/// </summary>
/// <remarks>
/// This result preserves the server's absolute deadline. <see cref="RespireTtlResult"/> instead reports a
/// remaining duration; converting it with the client's clock would lose the original deadline.
/// Use <see cref="RespireExpiry"/> for command inputs.
/// </remarks>
public readonly struct RespireExpiryTimeResult
{
    private const long MillisecondsPerSecond = 1000;

    // Keep milliseconds as the canonical form unless multiplying a seconds reply would overflow.
    private readonly long? _timestamp;
    private readonly bool _isSeconds;

    private RespireExpiryTimeResult(bool exists, long? timestamp, bool isSeconds = false)
    {
        Exists = exists;
        _timestamp = timestamp;
        _isSeconds = isSeconds;
    }

    /// <summary>Whether the key or field exists.</summary>
    public bool Exists
    {
        get;
    }

    /// <summary>Whether the key or field exists and has an expiry set.</summary>
    public bool HasExpiry => _timestamp.HasValue;

    /// <summary>
    /// Absolute Unix timestamp in milliseconds, or null when missing, persistent, or too large for
    /// signed 64-bit milliseconds. Use <see cref="UnixTimeSeconds"/> for those large seconds replies.
    /// </summary>
    public long? UnixTimeMilliseconds => _isSeconds ? null : _timestamp;

    /// <summary>
    /// Absolute Unix timestamp in whole seconds, or null when missing or persistent.
    /// Preserves seconds replies and truncates millisecond replies to whole seconds.
    /// </summary>
    public long? UnixTimeSeconds => _isSeconds ? _timestamp : _timestamp / MillisecondsPerSecond;

    /// <summary>Absolute expiration in UTC, or null when missing or persistent.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The server timestamp exceeds the range of <see cref="DateTimeOffset"/>.
    /// Use <see cref="TryGetExpiresAt"/> for a nonthrowing conversion.
    /// </exception>
    public DateTimeOffset? GetExpiresAt()
    {
        if (_timestamp is not { } timestamp)
        {
            return null;
        }

        return _isSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
            : DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
    }

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

    internal static RespireExpiryTimeResult FromRedis(long timestamp, RespireExpiryTimePrecision precision) => timestamp switch
    {
        -2 => default,
        -1 => new RespireExpiryTimeResult(exists: true, timestamp: null),
        > long.MaxValue / MillisecondsPerSecond when precision == RespireExpiryTimePrecision.Seconds
            => new RespireExpiryTimeResult(exists: true, timestamp, isSeconds: true),
        >= 0 when precision == RespireExpiryTimePrecision.Seconds
            => new RespireExpiryTimeResult(exists: true, timestamp * MillisecondsPerSecond),
        >= 0 => new RespireExpiryTimeResult(exists: true, timestamp),
        _ => throw new RespireProtocolException($"Unexpected expiry timestamp: {timestamp}."),
    };
}
