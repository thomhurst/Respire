namespace Respire;

/// <summary>The resulting value and actual applied increment returned by INCREX.</summary>
/// <remarks>A zero increment can mean a rejected bound, a saturated value, or a successful zero increment. It is not a success flag.</remarks>
public readonly record struct RespireIncrementResult<T>(T Value, T AppliedIncrement) where T : struct;

/// <summary>Integer bounds and expiry policy for Redis 8.8+ INCREX.</summary>
public readonly record struct IntegerIncrementOptions
{
    /// <summary>Inclusive lower bound; omitted means <see cref="long.MinValue"/>.</summary>
    public long? LowerBound { get; init; }
    /// <summary>Inclusive upper bound; omitted means <see cref="long.MaxValue"/>.</summary>
    public long? UpperBound { get; init; }
    /// <summary>Clamp an out-of-range result instead of rejecting the increment. An unrepresentable applied delta still errors.</summary>
    public bool Saturate { get; init; }
    /// <summary>New expiry, or Persist to remove it. None and Keep preserve the current TTL. Relative and absolute forms use milliseconds.</summary>
    public RespireExpiry Expiry { get; init; }
    /// <summary>Apply the expiry only when the key has no TTL (ENX). Requires a relative or absolute expiry.</summary>
    public bool ExpireOnlyWhenPersistent { get; init; }
}

/// <summary>Floating-point bounds and expiry policy for Redis 8.8+ INCREX.</summary>
/// <remarks>Redis calculates with long double; this API represents inputs and replies as .NET double.</remarks>
public readonly record struct FloatIncrementOptions
{
    /// <summary>Inclusive lower bound; omitted uses the server's numeric type limit. Infinity is allowed, NaN is not.</summary>
    public double? LowerBound { get; init; }
    /// <summary>Inclusive upper bound; omitted uses the server's numeric type limit. Infinity is allowed, NaN is not.</summary>
    public double? UpperBound { get; init; }
    /// <summary>Clamp an out-of-range result instead of rejecting the increment.</summary>
    public bool Saturate { get; init; }
    /// <summary>New expiry, or Persist to remove it. None and Keep preserve the current TTL. Relative and absolute forms use milliseconds.</summary>
    public RespireExpiry Expiry { get; init; }
    /// <summary>Apply the expiry only when the key has no TTL (ENX). Requires a relative or absolute expiry.</summary>
    public bool ExpireOnlyWhenPersistent { get; init; }
}
