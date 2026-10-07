namespace Respire.FusionCache;

/// <summary>Bounded server leases and polling for the FusionCache distributed locker.</summary>
public sealed record RespireFusionCacheDistributedLockerOptions
{
    /// <summary>A lease from one second to five minutes, renewed while the handle remains active. Defaults to 30 seconds.</summary>
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Base delay between contended attempts, from one millisecond to one second. Defaults to 50 milliseconds.</summary>
    /// <remarks>Each wait adds up to +/-10% jitter, with a one-millisecond minimum, and is capped by the remaining wait budget.</remarks>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Whether caller cancellation releases an acquired lease. Defaults to true.</summary>
    /// <remarks>
    /// Set to false to use the caller token only while waiting and handing off acquisition.
    /// An acquired lease then renews until explicit release, locker disposal, ownership loss, or its optional total lifetime limit.
    /// Neither policy cancels FusionCache factories or enforces fencing on their writes.
    /// </remarks>
    public bool ReleaseOnCallerCancellation { get; init; } = true;

    /// <summary>Optional total lifetime after successful independent-handle handoff. Defaults to null (no limit).</summary>
    /// <remarks>
    /// Requires <see cref="ReleaseOnCallerCancellation"/> to be false. A finite value must be at least one millisecond.
    /// Expiry stops renewal and starts owner-checked release; it does not cancel the factory or fence its writes.
    /// This limit is separate from each server lease duration and the acquisition wait budget.
    /// </remarks>
    public TimeSpan? MaximumIndependentLeaseLifetime { get; init; }

    internal void Validate()
    {
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "Lease duration must be between one second and five minutes.");
        if (PollInterval < TimeSpan.FromMilliseconds(1) || PollInterval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "Poll interval must be between one millisecond and one second.");
        if (MaximumIndependentLeaseLifetime is { } maximum)
        {
            if (maximum < TimeSpan.FromMilliseconds(1))
                throw new ArgumentOutOfRangeException(nameof(MaximumIndependentLeaseLifetime), "Independent lease lifetime must be at least one millisecond, or null for no limit.");
            if (ReleaseOnCallerCancellation)
                throw new ArgumentException("A total independent lease lifetime requires ReleaseOnCallerCancellation to be false.", nameof(MaximumIndependentLeaseLifetime));
        }
    }
}
