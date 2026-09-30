using System.Diagnostics;

namespace Respire.Extensions.Coordination;

/// <summary>A named coordination lease stored in one expiring Redis hash field.</summary>
/// <remarks>Ownership operations compare the unique owner token atomically. Hash-field expiration requires Redis 7.4 or later.</remarks>
public sealed class RespireCoordinationLease : IAsyncDisposable
{
    private readonly RespireCoordination _coordination;
    private readonly RespireLockToken _owner;
    private long _durationTicks;
    private long _renewedTimestamp;
    private int _released;

    internal RespireCoordinationLease(RespireCoordination coordination,
        RespireKey hashKey, RespireKey field, RespireLockToken owner, TimeSpan duration, long acquiredTimestamp)
    {
        _coordination = coordination;
        HashKey = hashKey.Snapshot();
        Field = field.Snapshot();
        _owner = owner;
        _durationTicks = duration.Ticks;
        _renewedTimestamp = acquiredTimestamp;
    }

    /// <summary>The hash key, before the client's prefix.</summary>
    public RespireKey HashKey { get; }

    /// <summary>The binary-safe hash field that stores this lease.</summary>
    public RespireKey Field { get; }

    /// <summary>The generated owner token used to compare every operation.</summary>
    public RespireLockToken OwnerToken => _owner;

    /// <summary>The currently configured lease duration.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

    /// <summary>A conservative local estimate; it is not a server ownership guarantee.</summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _released) != 0) return TimeSpan.Zero;
            var remaining = Duration - Stopwatch.GetElapsedTime(Interlocked.Read(ref _renewedTimestamp));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether this handle was released or its local lease estimate elapsed.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Checks that this owner still holds the expiring hash field.</summary>
    public ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
        => _coordination.VerifyHashFieldLeaseAsync(HashKey, Field, _owner, cancellationToken);

    /// <summary>Renews only this owner and preserves the hash field's independent expiry.</summary>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var milliseconds = RespireCoordination.ValidateLease(HashKey, duration);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsReleased) return false;
        var started = Stopwatch.GetTimestamp();
        if (!await _coordination.RenewHashFieldLeaseAsync(HashKey, Field, _owner, milliseconds, cancellationToken)
                .ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _released, 1);
            return false;
        }
        Interlocked.Exchange(ref _durationTicks, duration.Ticks);
        Interlocked.Exchange(ref _renewedTimestamp, started);
        return true;
    }

    /// <summary>Releases only this owner, leaving unrelated hash fields untouched.</summary>
    public async ValueTask<LockReleaseOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return LockReleaseOutcome.AlreadyReleased;
        try
        {
            if (await _coordination.ReleaseHashFieldLeaseAsync(HashKey, Field, _owner, cancellationToken)
                    .ConfigureAwait(false)) return LockReleaseOutcome.Released;
            return LockReleaseOutcome.NotOwned;
        }
        catch
        {
            Interlocked.Exchange(ref _released, 0);
            throw;
        }
    }

    /// <summary>Releases this lease.</summary>
    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync().ConfigureAwait(false);
    }
}
