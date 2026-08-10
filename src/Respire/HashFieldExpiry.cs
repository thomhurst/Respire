namespace Respire;

/// <summary>Condition for hash field expiry updates. Redis: HPEXPIRE/HPEXPIREAT.</summary>
public enum HashFieldExpireWhen
{
    /// <summary>Set or update the expiry unconditionally.</summary>
    Always,

    /// <summary>Only set expiry when the field has no expiry. Redis: NX.</summary>
    NotExists,

    /// <summary>Only set expiry when the field already has an expiry. Redis: XX.</summary>
    Exists,

    /// <summary>Only set expiry when the new expiry is greater than the current expiry. Redis: GT.</summary>
    GreaterThan,

    /// <summary>Only set expiry when the new expiry is less than the current expiry. Redis: LT.</summary>
    LessThan,
}

/// <summary>Per-field result for hash field expiry mutations.</summary>
public enum HashFieldExpiryResult
{
    /// <summary>The hash key or field does not exist. Redis: -2.</summary>
    NoSuchField = -2,

    /// <summary>The field exists, but has no expiry to inspect or remove. Redis: -1.</summary>
    NoExpiry = -1,

    /// <summary>The requested NX, XX, GT, or LT condition was not met. Redis: 0.</summary>
    ConditionNotMet = 0,

    /// <summary>The expiry was set, updated, or removed. Redis: 1.</summary>
    Applied = 1,

    /// <summary>The field was deleted because the requested expiry time is already due. Redis: 2.</summary>
    Deleted = 2,
}

/// <summary>
/// The expiry state of one hash field, distinguishing a missing field from a persistent field.
/// Redis: HPTTL.
/// </summary>
public readonly struct HashFieldExpiry
{
    private HashFieldExpiry(bool fieldExists, TimeSpan? timeToLive)
    {
        FieldExists = fieldExists;
        TimeToLive = timeToLive;
    }

    /// <summary>Whether the field exists in the hash.</summary>
    public bool FieldExists { get; }

    /// <summary>Whether the field exists and has an expiry set.</summary>
    public bool HasExpiry => TimeToLive.HasValue;

    /// <summary>Remaining time to live, or null when the field is missing or has no expiry.</summary>
    public TimeSpan? TimeToLive { get; }

    internal static HashFieldExpiry FromHpttl(long milliseconds)
        => milliseconds switch
        {
            -2 => new HashFieldExpiry(fieldExists: false, timeToLive: null),
            -1 => new HashFieldExpiry(fieldExists: true, timeToLive: null),
            _ => new HashFieldExpiry(fieldExists: true, timeToLive: TimeSpan.FromMilliseconds(milliseconds)),
        };

    public override string ToString()
        => !FieldExists ? "(missing field)" : TimeToLive is { } ttl ? ttl.ToString() : "(no expiry)";
}
