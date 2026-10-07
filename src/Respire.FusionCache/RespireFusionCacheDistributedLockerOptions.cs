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
    /// An acquired lease then renews until explicit release, locker disposal, or ownership loss.
    /// Neither policy cancels FusionCache factories or enforces fencing on their writes.
    /// </remarks>
    public bool ReleaseOnCallerCancellation { get; init; } = true;

    internal void Validate()
    {
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration), "Lease duration must be between one second and five minutes.");
        if (PollInterval < TimeSpan.FromMilliseconds(1) || PollInterval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(PollInterval), "Poll interval must be between one millisecond and one second.");
    }
}
