namespace Respire;

/// <summary>Which expiry form a <see cref="RespireExpiry"/> carries.</summary>
internal enum RespireExpiryKind : byte
{
    /// <summary>No expiry option is sent.</summary>
    None,

    /// <summary>A relative time to live in milliseconds (PX).</summary>
    Relative,

    /// <summary>An absolute Unix-millisecond instant (PXAT).</summary>
    Absolute,

    /// <summary>Retain the key's existing TTL (KEEPTTL).</summary>
    Keep,

    /// <summary>Remove the existing expiry (PERSIST).</summary>
    Persist,
}

/// <summary>
/// An expiry command input: none, a relative TTL, an absolute instant, "keep the current TTL",
/// or "remove the current TTL". Individual Redis commands accept the forms meaningful to them.
/// </summary>
/// <remarks>
/// This is the expiry <i>input</i> type. <see cref="RespireTtl"/> is the expiry Redis
/// <i>reports</i> for a key (the PTTL result). Converts implicitly from <see cref="TimeSpan"/> and
/// <see cref="DateTimeOffset"/>, so <c>expiry: TimeSpan.FromMinutes(5)</c> still reads the same.
/// Absolute and relative forms are both sent at millisecond precision (PXAT/PX).
/// </remarks>
public readonly struct RespireExpiry : IEquatable<RespireExpiry>
{
    private readonly long _value;
    private readonly RespireExpiryKind _kind;

    private RespireExpiry(RespireExpiryKind kind, long value)
    {
        _kind = kind;
        _value = value;
    }

    /// <summary>No expiry option — Redis applies its default (SET clears any existing TTL). The default value.</summary>
    public static readonly RespireExpiry None = default;

    /// <summary>Retains the TTL the key already has. Redis: KEEPTTL.</summary>
    public static readonly RespireExpiry Keep = new(RespireExpiryKind.Keep, 0);

    /// <summary>Removes an existing expiry. Redis: PERSIST.</summary>
    public static readonly RespireExpiry Persist = new(RespireExpiryKind.Persist, 0);

    /// <summary>Expires the key <paramref name="timeToLive"/> from now. Redis: PX milliseconds.</summary>
    public static RespireExpiry In(TimeSpan timeToLive)
        => new(RespireExpiryKind.Relative, (long)timeToLive.TotalMilliseconds);

    /// <summary>Expires the key at <paramref name="instant"/>. Redis: PXAT Unix milliseconds.</summary>
    public static RespireExpiry At(DateTimeOffset instant)
        => new(RespireExpiryKind.Absolute, instant.ToUnixTimeMilliseconds());

    /// <summary>Whether no expiry option is sent — true for <see cref="None"/> and <c>default</c>.</summary>
    public bool IsNone => _kind == RespireExpiryKind.None;

    /// <summary>Whether this is <see cref="Keep"/>.</summary>
    public bool IsKeep => _kind == RespireExpiryKind.Keep;

    /// <summary>Whether this is <see cref="Persist"/>.</summary>
    public bool IsPersist => _kind == RespireExpiryKind.Persist;

    /// <summary>The relative time to live, or null when this is not a relative expiry.</summary>
    public TimeSpan? TimeToLive
        => _kind == RespireExpiryKind.Relative ? TimeSpan.FromMilliseconds(_value) : null;

    /// <summary>The absolute expiry instant, or null when this is not an absolute expiry.</summary>
    public DateTimeOffset? ExpiresAt
        => _kind == RespireExpiryKind.Absolute ? DateTimeOffset.FromUnixTimeMilliseconds(_value) : null;

    /// <summary>Converts a relative TTL. Redis: PX milliseconds.</summary>
    public static implicit operator RespireExpiry(TimeSpan timeToLive) => In(timeToLive);

    /// <summary>Converts a relative TTL, mapping null to <see cref="None"/>.</summary>
    public static implicit operator RespireExpiry(TimeSpan? timeToLive)
        => timeToLive is { } value ? In(value) : None;

    /// <summary>Converts an absolute expiry instant. Redis: PXAT Unix milliseconds.</summary>
    public static implicit operator RespireExpiry(DateTimeOffset instant) => At(instant);

    /// <summary>Tests two expiry policies for equality.</summary>
    public static bool operator ==(RespireExpiry left, RespireExpiry right) => left.Equals(right);

    /// <summary>Tests two expiry policies for inequality.</summary>
    public static bool operator !=(RespireExpiry left, RespireExpiry right) => !left.Equals(right);

    /// <summary>The number of command tokens this expiry contributes: 0 for none, 1 for KEEPTTL/PERSIST, 2 otherwise.</summary>
    internal int TokenCount
        => _kind switch
        {
            RespireExpiryKind.None => 0,
            RespireExpiryKind.Keep or RespireExpiryKind.Persist => 1,
            _ => 2,
        };

    internal bool TryGetRelativeMilliseconds(out long milliseconds)
    {
        milliseconds = _value;
        return _kind == RespireExpiryKind.Relative;
    }

    internal bool TryGetAbsoluteUnixMilliseconds(out long unixMilliseconds)
    {
        unixMilliseconds = _value;
        return _kind == RespireExpiryKind.Absolute;
    }

    /// <inheritdoc/>
    public bool Equals(RespireExpiry other) => _kind == other._kind && _value == other._value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireExpiry other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine((byte)_kind, _value);

    /// <inheritdoc/>
    public override string ToString()
        => _kind switch
        {
            RespireExpiryKind.Relative => TimeSpan.FromMilliseconds(_value).ToString(),
            RespireExpiryKind.Absolute => DateTimeOffset.FromUnixTimeMilliseconds(_value).ToString("O"),
            RespireExpiryKind.Keep => "(keep)",
            RespireExpiryKind.Persist => "(persist)",
            _ => "(none)",
        };
}
