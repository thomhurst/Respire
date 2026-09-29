namespace Respire;

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
