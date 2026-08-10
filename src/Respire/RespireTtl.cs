namespace Respire;

/// <summary>Which expiry form a <see cref="RespireTtl"/> carries.</summary>
internal enum RespireTtlKind : byte
{
    /// <summary>No expiry option is sent.</summary>
    None,

    /// <summary>A relative time to live in milliseconds (PX).</summary>
    Relative,

    /// <summary>An absolute Unix-millisecond instant (PXAT).</summary>
    Absolute,

    /// <summary>Retain the key's existing TTL (KEEPTTL).</summary>
    Keep,
}

/// <summary>
/// The expiry to apply to a write: none, a relative TTL, an absolute instant, or "keep whatever
/// TTL the key already has". One parameter replaces the old <c>TimeSpan? expiry</c> plus
/// <c>bool keepTtl</c> pair, so conflicting combinations cannot be expressed.
/// </summary>
/// <remarks>
/// This is the expiry <i>input</i> type. <see cref="RespireExpiry"/> is the expiry Redis
/// <i>reports</i> for a key (the PTTL result). Converts implicitly from <see cref="TimeSpan"/> and
/// <see cref="DateTimeOffset"/>, so <c>expiry: TimeSpan.FromMinutes(5)</c> still reads the same.
/// Absolute and relative forms are both sent at millisecond precision (PXAT/PX).
/// </remarks>
public readonly struct RespireTtl : IEquatable<RespireTtl>
{
    private readonly long _value;
    private readonly RespireTtlKind _kind;

    private RespireTtl(RespireTtlKind kind, long value)
    {
        _kind = kind;
        _value = value;
    }

    /// <summary>No expiry option — Redis applies its default (SET clears any existing TTL). The default value.</summary>
    public static readonly RespireTtl None = default;

    /// <summary>Retains the TTL the key already has. Redis: KEEPTTL.</summary>
    public static readonly RespireTtl Keep = new(RespireTtlKind.Keep, 0);

    /// <summary>Expires the key <paramref name="timeToLive"/> from now. Redis: PX milliseconds.</summary>
    public static RespireTtl In(TimeSpan timeToLive)
        => new(RespireTtlKind.Relative, (long)timeToLive.TotalMilliseconds);

    /// <summary>Expires the key at <paramref name="instant"/>. Redis: PXAT Unix milliseconds.</summary>
    public static RespireTtl At(DateTimeOffset instant)
        => new(RespireTtlKind.Absolute, instant.ToUnixTimeMilliseconds());

    /// <summary>Whether no expiry option is sent — true for <see cref="None"/> and <c>default</c>.</summary>
    public bool IsNone => _kind == RespireTtlKind.None;

    /// <summary>Whether this is <see cref="Keep"/>.</summary>
    public bool IsKeep => _kind == RespireTtlKind.Keep;

    /// <summary>The relative time to live, or null when this is not a relative expiry.</summary>
    public TimeSpan? TimeToLive
        => _kind == RespireTtlKind.Relative ? TimeSpan.FromMilliseconds(_value) : null;

    /// <summary>The absolute expiry instant, or null when this is not an absolute expiry.</summary>
    public DateTimeOffset? ExpiresAt
        => _kind == RespireTtlKind.Absolute ? DateTimeOffset.FromUnixTimeMilliseconds(_value) : null;

    /// <summary>Converts a relative TTL. Redis: PX milliseconds.</summary>
    public static implicit operator RespireTtl(TimeSpan timeToLive) => In(timeToLive);

    /// <summary>Converts a relative TTL, mapping null to <see cref="None"/>.</summary>
    public static implicit operator RespireTtl(TimeSpan? timeToLive)
        => timeToLive is { } value ? In(value) : None;

    /// <summary>Converts an absolute expiry instant. Redis: PXAT Unix milliseconds.</summary>
    public static implicit operator RespireTtl(DateTimeOffset instant) => At(instant);

    public static bool operator ==(RespireTtl left, RespireTtl right) => left.Equals(right);

    public static bool operator !=(RespireTtl left, RespireTtl right) => !left.Equals(right);

    /// <summary>The number of command tokens this expiry contributes: 0 for none, 1 for KEEPTTL, 2 otherwise.</summary>
    internal int TokenCount
        => _kind switch
        {
            RespireTtlKind.None => 0,
            RespireTtlKind.Keep => 1,
            _ => 2,
        };

    internal bool TryGetRelativeMilliseconds(out long milliseconds)
    {
        milliseconds = _value;
        return _kind == RespireTtlKind.Relative;
    }

    internal bool TryGetAbsoluteUnixMilliseconds(out long unixMilliseconds)
    {
        unixMilliseconds = _value;
        return _kind == RespireTtlKind.Absolute;
    }

    public bool Equals(RespireTtl other) => _kind == other._kind && _value == other._value;

    public override bool Equals(object? obj) => obj is RespireTtl other && Equals(other);

    public override int GetHashCode() => HashCode.Combine((byte)_kind, _value);

    public override string ToString()
        => _kind switch
        {
            RespireTtlKind.Relative => TimeSpan.FromMilliseconds(_value).ToString(),
            RespireTtlKind.Absolute => DateTimeOffset.FromUnixTimeMilliseconds(_value).ToString("O"),
            RespireTtlKind.Keep => "(keep)",
            _ => "(none)",
        };
}
