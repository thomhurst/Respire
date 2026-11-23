namespace Respire;

/// <summary>
/// The remaining time to live reported by Redis, distinguishing a missing key or field from one
/// that exists without an expiry — the two negative answers Redis folds into TTL's -2/-1 sentinels.
/// </summary>
/// <remarks>
/// This result is a remaining duration. Use <see cref="RespireExpiryTimeResult"/> for the server's absolute
/// deadline and <see cref="RespireExpiry"/> to supply an expiry to a command. These results stay distinct
/// because a remaining duration plus the client's clock cannot recover the server's exact deadline.
/// </remarks>
public readonly struct RespireTtlResult
{
    private RespireTtlResult(bool exists, long? timeToLiveMilliseconds)
    {
        Exists = exists;
        TimeToLiveMilliseconds = timeToLiveMilliseconds;
    }

    /// <summary>Whether the key or field exists.</summary>
    public bool Exists
    {
        get;
    }

    /// <summary>Whether the key or field exists and has an expiry set.</summary>
    public bool HasExpiry => TimeToLiveMilliseconds.HasValue;

    /// <summary>
    /// Remaining time to live as a <see cref="TimeSpan"/>, or null when missing, persistent,
    /// or outside the range of <see cref="TimeSpan"/>. Use <see cref="TimeToLiveMilliseconds"/>
    /// to read large durations.
    /// </summary>
    public TimeSpan? TimeToLive
    {
        get
        {
            if (TimeToLiveMilliseconds is { } milliseconds
                && milliseconds >= long.MinValue / TimeSpan.TicksPerMillisecond
                && milliseconds <= long.MaxValue / TimeSpan.TicksPerMillisecond)
            {
                return TimeSpan.FromMilliseconds(milliseconds);
            }

            return null;
        }
    }

    /// <summary>Remaining time to live in server milliseconds, or null when missing or persistent.</summary>
    public long? TimeToLiveMilliseconds
    {
        get;
    }

    internal static RespireTtlResult FromRedisMilliseconds(long milliseconds) => milliseconds switch
    {
        -2 => default,
        -1 => new RespireTtlResult(exists: true, timeToLiveMilliseconds: null),
        _ => new RespireTtlResult(exists: true, timeToLiveMilliseconds: milliseconds),
    };

    /// <inheritdoc/>
    public override string ToString()
    {
        if (!Exists)
        {
            return "(missing)";
        }

        if (TimeToLiveMilliseconds is not { } milliseconds)
        {
            return "(no expiry)";
        }

        return TimeToLive is { } ttl ? ttl.ToString() : $"{milliseconds} ms";
    }
}
