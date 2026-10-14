using System.Globalization;

namespace Respire.Coordination;

/// <summary>Immutable settings for a rate limiter or a composite component.</summary>
public sealed class RespireRateLimit
{
    internal int PermitLimit { get; }
    internal string Configuration { get; }
    internal RedisRateLimiterKind Kind { get; }
    internal long PeriodMs { get; }
    internal int Extra { get; }

    private RespireRateLimit(int kind, int limit, TimeSpan period, int extra)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (period < TimeSpan.FromMilliseconds(1)) throw new ArgumentOutOfRangeException(nameof(period));
        var milliseconds = (period.Ticks - 1) / TimeSpan.TicksPerMillisecond + 1;
        var maxMilliseconds = TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;
        var horizon = kind == 2 ? ((long)limit + extra - 1) / extra : kind == 1 ? 2 : kind == 3 ? limit : 1;
        if (milliseconds > maxMilliseconds / horizon)
            throw new ArgumentOutOfRangeException(nameof(period), "The state retention duration must fit in a TimeSpan.");
        PermitLimit = limit;
        Kind = (RedisRateLimiterKind)kind;
        PeriodMs = milliseconds;
        Extra = extra;
        Configuration = string.Create(CultureInfo.InvariantCulture, $"{kind},{limit},{milliseconds},{extra}");
    }

    /// <summary>Defines a fixed window beginning with its first successful acquisition.</summary>
    public static RespireRateLimit FixedWindow(int permitLimit, TimeSpan window)
        => new(0, permitLimit, window, 0);

    /// <summary>Defines a sliding window with one to 1024 segments. Permits expire at segment end plus the window.</summary>
    public static RespireRateLimit SlidingWindow(int permitLimit, TimeSpan window, int segments)
    {
        if (segments is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(segments));
        return new(1, permitLimit, window, segments);
    }

    /// <summary>Defines a bucket initially full, refilling tokens at whole replenishment periods.</summary>
    public static RespireRateLimit TokenBucket(int tokenLimit, int tokensPerPeriod, TimeSpan replenishmentPeriod)
    {
        if (tokensPerPeriod <= 0) throw new ArgumentOutOfRangeException(nameof(tokensPerPeriod));
        return new(2, tokenLimit, replenishmentPeriod, tokensPerPeriod);
    }

    /// <summary>Defines GCRA settings for a standalone limiter update; not a composite component.</summary>
    public static RespireRateLimit Gcra(int burstLimit, TimeSpan emissionInterval)
        => new(3, burstLimit, emissionInterval, 0);
}
