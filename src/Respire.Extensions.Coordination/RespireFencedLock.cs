namespace Respire.Extensions.Coordination;

/// <summary>The result of an immediate fencing-lock acquisition.</summary>
public readonly struct RespireFencedLockAttempt : IAsyncDisposable
{
    private readonly RespireFencedLock? _lock;
    internal RespireFencedLockAttempt(RespireFencedLock @lock) => _lock = @lock;
    /// <summary>Whether this attempt acquired a usable lease.</summary>
    public bool Acquired => _lock is not null;
    /// <summary>The lease, or a <see cref="RespireLockNotAcquiredException"/> when acquisition failed.</summary>
    public RespireFencedLock Lock => _lock ?? throw new RespireLockNotAcquiredException();
    /// <summary>Disposes an acquired lease; an unsuccessful attempt is a no-op.</summary>
    public ValueTask DisposeAsync() => _lock?.DisposeAsync() ?? default;
}

/// <summary>A bounded Redis lease carrying a fencing token for a cooperating protected resource.</summary>
/// <remarks>
/// Renewal preserves the fencing token. Expiry, cancellation or connection loss can end ownership;
/// stop protected work when ownership is uncertain. Counter rollback invalidates monotonicity across Redis histories.
/// </remarks>
public sealed class RespireFencedLock : IAsyncDisposable
{
    private readonly RespireLock _lease;
    internal RespireFencedLock(RespireLock lease, RespireKey counterKey, long fencingToken)
    {
        _lease = lease;
        FencingCounterKey = counterKey;
        FencingToken = fencingToken;
    }
    /// <summary>The lease key before the client's prefix.</summary>
    public RespireKey Key => _lease.Key;
    /// <summary>The persistent counter key before the client's prefix.</summary>
    public RespireKey FencingCounterKey { get; }
    /// <summary>The generated ownership token used by atomic release and renewal.</summary>
    public RespireLockToken OwnerToken => _lease.Token;
    /// <summary>The positive fencing token. Protected resources must validate it atomically with their writes.</summary>
    public long FencingToken { get; }
    /// <summary>The currently configured lease duration.</summary>
    public TimeSpan Duration => _lease.Duration;
    /// <summary>A conservative local estimate; it is not a server ownership guarantee.</summary>
    public TimeSpan RemainingEstimate => _lease.RemainingEstimate;
    /// <summary>Whether ownership was released, lost or locally expired.</summary>
    public bool IsReleased => _lease.IsReleased;
    /// <summary>Checks that the generated owner still holds the server lease.</summary>
    public ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
        => _lease.VerifyStillHeldAsync(cancellationToken);
    /// <summary>Renews only this owner, retaining its fencing token.</summary>
    /// <remarks>Managed renewal uses CLIENT ID and CLIENT KILL to fence uncertain commands; Redis ACLs must permit them.</remarks>
    public ValueTask<bool> ResetExpiryAsync(TimeSpan duration, CancellationToken cancellationToken = default)
        => _lease.ResetExpiryAsync(duration, cancellationToken);
    /// <summary>Releases only this owner and preserves the counter for future acquisitions.</summary>
    public ValueTask<LockReleaseOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
        => _lease.ReleaseAsync(cancellationToken);
    /// <summary>Releases the lease using managed lock cleanup; transport failures leave bounded expiry as the fallback.</summary>
    public ValueTask DisposeAsync() => _lease.DisposeAsync();
}
