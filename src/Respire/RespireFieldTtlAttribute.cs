namespace Respire;

/// <summary>Sets a mapped hash property's expiry in milliseconds whenever its value is written.</summary>
/// <param name="milliseconds">A positive relative expiry in milliseconds.</param>
[AttributeUsage(AttributeTargets.Property, Inherited = false)]
public sealed class RespireFieldTtlAttribute(long milliseconds) : Attribute
{
    /// <summary>The relative field expiry in milliseconds.</summary>
    public long Milliseconds { get; } = milliseconds > 0 ? milliseconds
        : throw new ArgumentOutOfRangeException(nameof(milliseconds));
}

/// <summary>Selects how generated mappers write fields with expiry.</summary>
public enum RespireHashExpiryMode
{
    /// <summary>Use atomic HSETEX for each expiry group. Requires Redis 8.0 or later.</summary>
    HSetEx,
    /// <summary>Opt in to non-atomic HSET followed by HPEXPIRE. Requires Redis 7.4 or later.</summary>
    HSetThenExpire,
}
