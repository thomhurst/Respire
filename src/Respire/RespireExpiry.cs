namespace Respire;

/// <summary>
/// The expiry state of a key, distinguishing "key does not exist" from "key exists without an
/// expiry" — the two negative answers Redis folds into TTL's -2/-1 sentinels.
/// </summary>
public readonly struct RespireExpiry
{
    private RespireExpiry(bool keyExists, TimeSpan? timeToLive)
    {
        KeyExists = keyExists;
        TimeToLive = timeToLive;
    }

    /// <summary>Whether the key exists at all.</summary>
    public bool KeyExists { get; }

    /// <summary>Whether the key exists and has an expiry set.</summary>
    public bool HasExpiry => TimeToLive.HasValue;

    /// <summary>Remaining time to live, or null when the key is missing or has no expiry.</summary>
    public TimeSpan? TimeToLive { get; }

    internal static RespireExpiry FromPttl(long milliseconds)
        => milliseconds switch
        {
            -2 => new RespireExpiry(keyExists: false, timeToLive: null),
            -1 => new RespireExpiry(keyExists: true, timeToLive: null),
            _ => new RespireExpiry(keyExists: true, timeToLive: TimeSpan.FromMilliseconds(milliseconds)),
        };

    public override string ToString()
        => !KeyExists ? "(missing key)" : TimeToLive is { } ttl ? ttl.ToString() : "(no expiry)";
}
