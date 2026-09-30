using System.Diagnostics;

namespace Respire.Extensions.Coordination;

/// <summary>The result of an immediate distributed read or write lock attempt.</summary>
public readonly struct RespireReadWriteLockAttempt : IAsyncDisposable
{
    private readonly RespireReadWriteLock? _lock;

    internal RespireReadWriteLockAttempt(RespireReadWriteLock @lock) => _lock = @lock;

    /// <summary>Whether this attempt acquired a lease.</summary>
    public bool Acquired => _lock is not null;

    /// <summary>The acquired lock. Throws when this attempt did not acquire a lease.</summary>
    public RespireReadWriteLock Lock => _lock ?? throw new RespireLockNotAcquiredException();

    /// <summary>Releases the acquired lease, or does nothing when acquisition failed.</summary>
    public ValueTask DisposeAsync() => _lock?.DisposeAsync() ?? default;
}

/// <summary>A bounded shared-read or exclusive-write Redis lease.</summary>
/// <remarks>
/// Acquisitions are immediate and have no queue or fairness guarantee. Expiry or uncertain
/// renewal ends local ownership. Redis asynchronous failover can restore an older lock state.
/// </remarks>
public sealed class RespireReadWriteLock : IAsyncDisposable
{
    private readonly IRespireClient _client;
    private readonly RespireLockToken _owner;
    private readonly bool _isWriter;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private long _renewedTimestamp;
    private long _durationTicks;
    private int _released;

    internal RespireReadWriteLock(
        IRespireClient client, RespireKey key, RespireLockToken owner,
        bool isWriter, TimeSpan duration, long startedTimestamp)
    {
        _client = client;
        Key = key;
        _owner = owner;
        _isWriter = isWriter;
        _durationTicks = duration.Ticks;
        _renewedTimestamp = startedTimestamp;
    }

    /// <summary>The lock key before the client's configured prefix.</summary>
    public RespireKey Key { get; }

    /// <summary>Whether this handle represents an exclusive writer lease.</summary>
    public bool IsWriter => _isWriter;

    /// <summary>The current lease duration, truncated to whole milliseconds.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

    /// <summary>A conservative local estimate; it does not prove continued server ownership.</summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _released) != 0) return TimeSpan.Zero;
            // Measured from before the acquiring or renewing command was sent. Subtracting elapsed
            // time from the duration cannot overflow, unlike adding a long duration to a timestamp.
            var remaining = Duration - Stopwatch.GetElapsedTime(Interlocked.Read(ref _renewedTimestamp));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether this handle was released, expired locally, or lost ownership.</summary>
    public bool IsReleased => Volatile.Read(ref _released) != 0 || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Checks whether this exact reader or writer lease still exists on Redis.</summary>
    public async ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
    {
        if (IsReleased) return false;
        try
        {
            using var response = await _client.Scripts.ExecuteAsync(
                RespireCoordination.VerifyReadWriteLock, [Key], [OwnerBytes(), Role], cancellationToken).ConfigureAwait(false);
            var held = response.AsInteger() == 1;
            if (!held) Interlocked.Exchange(ref _released, 1);
            return held;
        }
        catch
        {
            Interlocked.Exchange(ref _released, 1);
            throw;
        }
    }

    /// <summary>Renews this owner lease; renewal fails if the Redis lease already expired.</summary>
    /// <remarks>The duration is truncated to whole milliseconds.</remarks>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (milliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(duration), "Lease duration must be at least one millisecond.");
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReleased) return false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                using var response = await _client.Scripts.ExecuteAsync(
                    RespireCoordination.RenewReadWriteLock, [Key], [OwnerBytes(), Role, milliseconds], cancellationToken).ConfigureAwait(false);
                var completed = Stopwatch.GetTimestamp();
                var validity = TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond) - Stopwatch.GetElapsedTime(started, completed);
                if (response.AsInteger() == 1 && validity > TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref _durationTicks, milliseconds * TimeSpan.TicksPerMillisecond);
                    Interlocked.Exchange(ref _renewedTimestamp, started);
                    return true;
                }
            }
            catch
            {
                Interlocked.Exchange(ref _released, 1);
                await ReleaseBestEffortAsync().ConfigureAwait(false);
                throw;
            }

            Interlocked.Exchange(ref _released, 1);
            await ReleaseBestEffortAsync().ConfigureAwait(false);
            return false;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases only this owner lease. Repeated calls return false.</summary>
    public async ValueTask<bool> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return false;
            using var response = await _client.Scripts.ExecuteAsync(
                RespireCoordination.ReleaseReadWriteLock, [Key], [OwnerBytes(), Role], cancellationToken).ConfigureAwait(false);
            return response.AsInteger() == 1;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Releases this lease on a best-effort basis.</summary>
    public async ValueTask DisposeAsync()
    {
        try { _ = await ReleaseAsync().ConfigureAwait(false); }
        catch (Exception) { }
    }

    private string Role => _isWriter ? "W:" : "R:";
    private ReadOnlyMemory<byte> OwnerBytes() => _owner.Bytes;

    private async ValueTask ReleaseBestEffortAsync()
    {
        try
        {
            using var _ = await _client.Scripts.ExecuteAsync(
                RespireCoordination.ReleaseReadWriteLock, [Key], [OwnerBytes(), Role], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
