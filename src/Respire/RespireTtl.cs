namespace Respire;

/// <summary>
/// The remaining time to live reported by Redis, distinguishing a missing key or field from one
/// that exists without an expiry — the two negative answers Redis folds into TTL's -2/-1 sentinels.
/// </summary>
public readonly struct RespireTtl
{
    private RespireTtl(bool exists, TimeSpan? timeToLive)
    {
        Exists = exists;
        TimeToLive = timeToLive;
    }

    /// <summary>Whether the key or field exists.</summary>
    public bool Exists { get; }

    /// <summary>Whether the key or field exists and has an expiry set.</summary>
    public bool HasExpiry => TimeToLive.HasValue;

    /// <summary>Remaining time to live, or null when the value is missing or has no expiry.</summary>
    public TimeSpan? TimeToLive { get; }

    internal static RespireTtl FromRedisMilliseconds(long milliseconds)
        => milliseconds switch
        {
            -2 => new RespireTtl(exists: false, timeToLive: null),
            -1 => new RespireTtl(exists: true, timeToLive: null),
            _ => new RespireTtl(exists: true, timeToLive: TimeSpan.FromMilliseconds(milliseconds)),
        };

    /// <inheritdoc/>
    public override string ToString()
        => !Exists ? "(missing)" : TimeToLive is { } ttl ? ttl.ToString() : "(no expiry)";
}
