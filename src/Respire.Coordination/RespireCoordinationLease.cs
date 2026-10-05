using System.Diagnostics;

namespace Respire.Coordination;

/// <summary>A named coordination lease stored in one expiring Redis hash field.</summary>
/// <remarks>Ownership operations compare the unique owner token atomically. Hash-field expiration requires Redis 7.4 or later.</remarks>
public sealed class RespireCoordinationLease : IAsyncDisposable
{
    private sealed record LeaseSnapshot(long DurationTicks, long RenewedTimestamp);

    private readonly RespireCoordination _coordination;
    private readonly RespireLockToken _owner;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Lock _releaseSync = new();
    private LeaseSnapshot _snapshot;
    private int _state;
    private Task<LockReleaseOutcome>? _releaseTask;
    private int _releasePreviousState;

    private const int StateHeld = 0;
    private const int StateReleasing = 1;
    private const int StateReleased = 2;
    private const int StateNotOwned = 3;
    private const int StateUncertain = 4;

    internal RespireCoordinationLease(RespireCoordination coordination,
        RespireKey hashKey, RespireKey field, RespireLockToken owner, TimeSpan duration, long acquiredTimestamp)
    {
        _coordination = coordination;
        HashKey = hashKey.Snapshot();
        Field = field.Snapshot();
        _owner = owner;
        _snapshot = new LeaseSnapshot(duration.Ticks, acquiredTimestamp);
    }

    /// <summary>The hash key, before the client's prefix.</summary>
    public RespireKey HashKey { get; }

    /// <summary>The binary-safe hash field that stores this lease.</summary>
    public RespireKey Field { get; }

    /// <summary>The generated owner token used to compare every operation.</summary>
    public RespireLockToken OwnerToken => _owner;

    /// <summary>The currently configured lease duration.</summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Volatile.Read(ref _snapshot).DurationTicks);

    /// <summary>A conservative local estimate; it is not a server ownership guarantee.</summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _state) != StateHeld) return TimeSpan.Zero;
            var snapshot = Volatile.Read(ref _snapshot);
            var remaining = TimeSpan.FromTicks(snapshot.DurationTicks) - Stopwatch.GetElapsedTime(snapshot.RenewedTimestamp);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Whether this handle is released, no longer owned, uncertain, or past its local lease estimate.</summary>
    public bool IsReleased => Volatile.Read(ref _state) != StateHeld || RemainingEstimate == TimeSpan.Zero;

    /// <summary>Checks that this owner still holds the expiring hash field.</summary>
    public ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
        => _coordination.VerifyHashFieldLeaseAsync(HashKey, Field, _owner, cancellationToken);

    /// <summary>Renews only this owner and preserves the hash field's independent expiry; uncertain state fails closed.</summary>
    public async ValueTask<bool> ResetExpiryAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var milliseconds = RespireCoordination.ValidateLease(HashKey, Field, duration);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = Volatile.Read(ref _state);
            if (state is StateReleasing or StateReleased or StateNotOwned) return false;
            // An earlier timed-out renewal may still execute on another Redis connection.
            // Its late execution could shorten a later renewal, so uncertain state stays fail-closed.
            if (state == StateUncertain) return false;
            var started = Stopwatch.GetTimestamp();
            try
            {
                if (!await _coordination.RenewHashFieldLeaseAsync(HashKey, Field, _owner, milliseconds, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Volatile.Write(ref _state, StateNotOwned);
                    return false;
                }
            }
            catch (RespireServerException)
            {
                throw;
            }
            catch
            {
                lock (_releaseSync)
                {
                    if (_state == StateReleasing) _releasePreviousState = StateUncertain;
                    else Volatile.Write(ref _state, StateUncertain);
                }
                throw;
            }

            var appliedTicks = checked(milliseconds * TimeSpan.TicksPerMillisecond);
            Volatile.Write(ref _snapshot, new LeaseSnapshot(appliedTicks, started));
            _ = Interlocked.CompareExchange(ref _state, StateHeld, StateUncertain);
            return true;
        }
        finally
        {
            _operationGate.Release();
            ResumeQueuedRelease();
        }
    }

    /// <summary>Releases only this owner, leaving unrelated hash fields untouched. Each attempt has a two-second deadline.</summary>
    public ValueTask<LockReleaseOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_releaseSync)
        {
            if (_state == StateReleased) return ValueTask.FromResult(LockReleaseOutcome.AlreadyReleased);
            if (_state == StateNotOwned) return ValueTask.FromResult(LockReleaseOutcome.NotOwned);
            if (_state == StateReleasing)
            {
                if (_releaseTask is null)
                {
                    var retryTask = ReleaseCoreAsync();
                    _releaseTask = retryTask;
                    ObserveReleaseFailure(retryTask);
                }
                return new ValueTask<LockReleaseOutcome>(_releaseTask!.WaitAsync(cancellationToken));
            }
            _releasePreviousState = _state;
            _state = StateReleasing;
            // The shared release outlives each caller so cancellation cannot cancel another waiter's operation.
            var releaseTask = ReleaseCoreAsync();
            _releaseTask = releaseTask;
            ObserveReleaseFailure(releaseTask);
            return new ValueTask<LockReleaseOutcome>(releaseTask.WaitAsync(cancellationToken));
        }
    }

    private static void ObserveReleaseFailure(Task releaseTask)
        => _ = releaseTask.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async Task<LockReleaseOutcome> ReleaseCoreAsync()
    {
        var entered = false;
        var retryAfterGateRelease = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _operationGate.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            var released = await _coordination.ReleaseHashFieldLeaseAsync(HashKey, Field, _owner, deadline.Token)
                .ConfigureAwait(false);
            Volatile.Write(ref _state, released ? StateReleased : StateNotOwned);
            return released ? LockReleaseOutcome.Released : LockReleaseOutcome.NotOwned;
        }
        catch (RespireServerException)
        {
            lock (_releaseSync)
            {
                if (_state == StateReleasing)
                {
                    Volatile.Write(ref _state, _releasePreviousState);
                }
                _releaseTask = null;
            }
            throw;
        }
        catch
        {
            lock (_releaseSync)
            {
                if (_state == StateReleasing)
                {
                    if (entered) Volatile.Write(ref _state, StateUncertain);
                }
                _releaseTask = null;
                // If the gate owner has already released before we cleared the task, its
                // ResumeQueuedRelease call observed the old task and could not restart it.
                // Retry only in that race. Otherwise the gate owner's finally resumes release.
                retryAfterGateRelease = !entered && _state == StateReleasing && _operationGate.CurrentCount != 0;
            }
            if (retryAfterGateRelease) ResumeQueuedRelease();
            throw;
        }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    private void ResumeQueuedRelease()
    {
        lock (_releaseSync)
        {
            if (_state != StateReleasing || _releaseTask is not null) return;
            var releaseTask = ReleaseCoreAsync();
            _releaseTask = releaseTask;
            ObserveReleaseFailure(releaseTask);
        }
    }

    /// <summary>Releases this lease on a best-effort basis.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RespireConnectionException or RespireTimeoutException
            or ObjectDisposedException or OperationCanceledException)
        {
            // Uncertain release expires on its own; cleanup must not mask the caller's exception.
        }
    }
}
