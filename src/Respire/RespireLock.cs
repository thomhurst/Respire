using System.Runtime.ExceptionServices;

namespace Respire;

/// <summary>The result of releasing a managed distributed lock.</summary>
public enum LockReleaseOutcome
{
    /// <summary>This call removed the lock while the handle still owned it.</summary>
    Released,

    /// <summary>This handle had already released the lock.</summary>
    AlreadyReleased,

    /// <summary>The lock expired or is now owned by another token.</summary>
    NotOwned,
}

/// <summary>The result of trying to acquire a distributed lock.</summary>
public readonly struct RespireLockAttempt : IAsyncDisposable
{
    private readonly RespireLock? _lock;

    internal RespireLockAttempt(RespireLock? @lock) => _lock = @lock;

    /// <summary>Whether this attempt acquired the lock.</summary>
    public bool Acquired => _lock is not null;

    /// <summary>
    /// The acquired lock. Throws <see cref="RespireLockNotAcquiredException"/> when
    /// <see cref="Acquired"/> is <see langword="false"/>.
    /// </summary>
    public RespireLock Lock => _lock ?? throw new RespireLockNotAcquiredException();

    /// <summary>Releases the acquired lock, or does nothing when acquisition failed.</summary>
    public ValueTask DisposeAsync() => _lock?.DisposeAsync() ?? default;
}

/// <summary>
/// An acquired distributed lock. Exposed by <see cref="RespireLockAttempt.Lock"/> or returned by
/// <see cref="ILockCommands.AcquireOrThrowAsync(RespireKey, TimeSpan, CancellationToken)"/>,
/// which generates the owner token, so callers never invent or thread one through calls. Every
/// operation compares that token on the server, so a lock that expired and was taken by someone
/// else is never extended or deleted by this handle.
/// </summary>
/// <remarks>
/// The lock is a lease, not a mutex: it disappears on its own when <see cref="Duration"/> elapses,
/// even mid-work. Keep protected work shorter than the duration, call <see cref="ResetExpiryAsync"/>,
/// or use <see cref="KeepAliveAsync"/> and stop protected work when its token is cancelled.
/// </remarks>
public sealed class RespireLock : IAsyncDisposable
{
    private const int TokenLength = 32;
    private const int StateHeld = 0;
    private const int StateReleasing = 1;
    private const int StateReleased = 2;
    private const int StateNotOwned = 3;

    private readonly ILockCommands _locks;
    private readonly SemaphoreSlim _extendSync = new(1, 1);
    private CancellationTokenSource _leaseChanged = new();
    private long _durationTicks;
    private long _renewedTimestamp;
    private int _state;
    private int _keepAlive;
    private RespireLockKeepAlive? _ownedKeepAlive;
    private int _ownedKeepAliveDisposed;
    private readonly Lock _releaseSync = new();
    private ReleaseAttempt? _releaseAttempt;

    internal RespireLock(
        ILockCommands locks,
        RespireKey key,
        RespireLockToken token,
        TimeSpan duration,
        long acquiredTimestamp,
        TimeProvider? timeProvider = null)
    {
        Clock = timeProvider ?? TimeProvider.System;
        _locks = locks;
        Key = key.Snapshot();
        Token = token;
        _durationTicks = duration.Ticks;
        _renewedTimestamp = acquiredTimestamp;
    }

    internal TimeProvider Clock { get; }

    /// <summary>The locked key, as passed to <c>AcquireAsync</c> (before any client key prefix).</summary>
    public RespireKey Key { get; }

    /// <summary>
    /// The owner token stored in the key: 32 ASCII hex characters from a <see cref="Guid"/>. Held
    /// as bytes so it compares byte-for-byte with the value the server round-trips, and with
    /// <see cref="ILockCommands.GetOwnerTokenAsync"/>.
    /// </summary>
    public RespireLockToken Token { get; }

    /// <summary>
    /// The lease duration currently applied to the lock: the one it was acquired with, or the one
    /// from the most recent successful <see cref="ResetExpiryAsync"/>.
    /// </summary>
    public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));

    /// <summary>
    /// Best-effort remaining lease time based on a monotonic timestamp captured before the most
    /// recent acquire or successful extension. Zero means the estimate elapsed or ownership was lost.
    /// </summary>
    public TimeSpan RemainingEstimate
    {
        get
        {
            if (Volatile.Read(ref _state) != StateHeld)
            {
                return TimeSpan.Zero;
            }

            var remaining = Duration - Clock.GetElapsedTime(Interlocked.Read(ref _renewedTimestamp));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Best-effort wall-clock expiry instant derived from <see cref="RemainingEstimate"/>.</summary>
    public DateTimeOffset ExpiresAtEstimate => Clock.GetUtcNow() + RemainingEstimate;

    /// <summary>Whether this handle no longer considers itself the lock owner.</summary>
    public bool IsReleased
        => Volatile.Read(ref _state) != StateHeld || RemainingEstimate == TimeSpan.Zero;

    /// <summary>
    /// Cancelled when acquisition-owned keep-alive renewal stops or loses ownership. This token
    /// cannot be cancelled when the lock was acquired without <c>keepAlive: true</c>.
    /// </summary>
    public CancellationToken KeepAliveCancellationToken
        => Volatile.Read(ref _ownedKeepAlive)?.CancellationToken ?? CancellationToken.None;

    /// <summary>
    /// Resets the lock's expiry to <paramref name="newDuration"/> from now, only while this handle is
    /// still the owner. Uses SET IFEQ PX when supported, otherwise Lua compare-and-PEXPIRE.
    /// </summary>
    /// <remarks>
    /// Managed extensions that can time out or be cancelled use Redis <c>CLIENT ID</c> and
    /// <c>CLIENT KILL</c> to fence uncertain commands; the authenticated user must permit them.
    /// Redis stores lock expiry in whole milliseconds, so a positive duration with a fractional
    /// millisecond is rounded up before it is sent; <see cref="Duration"/> reports the value applied.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the lock is no longer owned — it expired, it was released, or
    /// another owner took it — in which case protected work must stop rather than retry.
    /// </returns>
    public ValueTask<bool> ResetExpiryAsync(
        TimeSpan newDuration,
        CancellationToken cancellationToken = default)
        => ExtendCoreAsync(newDuration, signalLeaseChanged: true, MarkOwnershipLost, cancellationToken);

    /// <summary>Checks Redis to verify that this handle's owner token still holds the key.</summary>
    public ValueTask<bool> VerifyStillHeldAsync(CancellationToken cancellationToken = default)
        => IsHeldByOriginAsync(cancellationToken);

    internal ValueTask<bool> RenewAsync(Action onOutcomeUncertain, CancellationToken cancellationToken)
        => ExtendCoreAsync(expiry: null, signalLeaseChanged: false, onOutcomeUncertain, cancellationToken);

    internal CancellationToken LeaseChanged => Volatile.Read(ref _leaseChanged).Token;

    internal TimeSpan RemainingUntilLeaseExpiry
    {
        get
        {
            var remaining = Duration - Clock.GetElapsedTime(Interlocked.Read(ref _renewedTimestamp));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private async ValueTask<bool> ExtendCoreAsync(
        TimeSpan? expiry,
        bool signalLeaseChanged,
        Action? onOutcomeUncertain,
        CancellationToken cancellationToken)
    {
        if (IsReleased)
        {
            // Released keys are owned by nobody or by the next owner; either way not by us.
            return false;
        }

        await _extendSync.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            if (IsReleased)
            {
                return false;
            }

            var effectiveExpiry = NormalizeDuration(expiry ?? Duration);
            var renewedTimestamp = Clock.GetTimestamp();
            var extended = _locks is IManagedLockCommands managed
                ? await managed.ExtendManagedAsync(
                        Key, Token, effectiveExpiry, onOutcomeUncertain, cancellationToken)
                    .ConfigureAwait(false)
                : await _locks.ResetExpiryAsync(Key, Token, effectiveExpiry, cancellationToken).ConfigureAwait(false);
            if (!extended)
            {
                changed = TryMarkOwnershipLost();
                return false;
            }

            Interlocked.Exchange(ref _durationTicks, effectiveExpiry.Ticks);
            Interlocked.Exchange(ref _renewedTimestamp, renewedTimestamp);
            changed = signalLeaseChanged;
            return true;
        }
        finally
        {
            _extendSync.Release();
            if (changed)
            {
                SignalLeaseChanged();
            }
        }
    }

    /// <summary>
    /// Starts renewing the lock halfway through each current <see cref="Duration"/>. The returned
    /// handle's cancellation token is cancelled when renewal reports lost ownership, renewal
    /// throws, the caller token is cancelled, or the handle is disposed. Only one keep-alive may
    /// run for a lock at a time.
    /// </summary>
    public ValueTask<RespireLockKeepAlive> KeepAliveAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(StartKeepAlive(cancellationToken));

    internal void StartOwnedKeepAlive()
        => Volatile.Write(ref _ownedKeepAlive, StartKeepAlive(CancellationToken.None));

    private RespireLockKeepAlive StartKeepAlive(CancellationToken cancellationToken)
    {
        if (IsReleased)
        {
            throw new InvalidOperationException("A released or lost lock cannot be kept alive.");
        }

        if (Interlocked.CompareExchange(ref _keepAlive, 1, 0) != 0)
        {
            throw new InvalidOperationException("This lock already has an active keep-alive.");
        }

        try
        {
            return new RespireLockKeepAlive(this, cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _keepAlive, 0);
            throw;
        }
    }

    /// <summary>
    /// Releases the lock, only while this handle is still the owner. Redis: compare-and-DEL.
    /// Idempotent: later calls return <see cref="LockReleaseOutcome.AlreadyReleased"/> without
    /// touching the server. Concurrent callers share one in-flight release, governed by the
    /// cancellation token of the caller that starts it. When that caller cancels before the command
    /// is submitted, the other callers retry with their own tokens. Any other failure, including a
    /// timeout or connection error before submission, is reported to every caller that shares the
    /// release; when it happened before submission the handle still owns the lock, so any caller
    /// can call <see cref="ReleaseAsync"/> again.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels release. Cancellation before command submission preserves ownership and allows retry;
    /// after submission, an uncertain outcome fails closed. A caller that joined another caller's
    /// in-flight release stops waiting when its own token is cancelled, but the shared release
    /// continues.
    /// </param>
    /// <returns>
    /// Distinguishes a successful delete, a repeat call, and lost ownership.
    /// </returns>
    public ValueTask<LockReleaseOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var attempt = EnterRelease(cancellationToken, out var outcome, out var started);
        if (attempt is null)
        {
            return ValueTask.FromResult(outcome);
        }

        return started
            ? new ValueTask<LockReleaseOutcome>(attempt.Task)
            : JoinReleaseAsync(attempt, cancellationToken);
    }

    /// <summary>
    /// Returns the release attempt this caller should await, starting one when the lock is held,
    /// or <see langword="null"/> with the final <paramref name="outcome"/> when release finished.
    /// </summary>
    private ReleaseAttempt? EnterRelease(
        CancellationToken cancellationToken, out LockReleaseOutcome outcome, out bool started)
    {
        ReleaseAttempt attempt;
        lock (_releaseSync)
        {
            started = false;
            outcome = default;
            switch (Volatile.Read(ref _state))
            {
                case StateReleased:
                    outcome = LockReleaseOutcome.AlreadyReleased;
                    return null;
                case StateNotOwned:
                    outcome = LockReleaseOutcome.NotOwned;
                    return null;
                case StateReleasing:
                    return _releaseAttempt!;
            }

            Volatile.Write(ref _state, StateReleasing);
            attempt = _releaseAttempt = new ReleaseAttempt();
        }

        // Start outside _releaseSync so the first caller's synchronous prefix never runs under it.
        // CompleteReleaseAsync routes every outcome to the shared task, so nothing is unobserved.
        _ = CompleteReleaseAsync(attempt, cancellationToken);
        started = true;
        return attempt;
    }

    private async ValueTask<LockReleaseOutcome> JoinReleaseAsync(
        ReleaseAttempt attempt, CancellationToken cancellationToken)
    {
        // Each pass follows a newer attempt only after the previous one was cancelled before
        // submission and ownership was restored. This caller then starts the next attempt with
        // its own token or joins one another caller started, so the loop ends when an attempt
        // completes or this caller's token is cancelled.
        while (true)
        {
            try
            {
                // The joiner's token ends only its own wait; the shared release keeps running.
                return await attempt.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && attempt.OwnershipRestored)
            {
                // The starter cancelled before submission. RestoreOwnership sets OwnershipRestored
                // under _releaseSync before CompleteReleaseAsync completes the task, so this read
                // cannot miss it. Fall through and start or join the next attempt.
            }

            var next = EnterRelease(cancellationToken, out var outcome, out var started);
            if (next is null)
            {
                return outcome;
            }

            if (started)
            {
                return await next.Task.ConfigureAwait(false);
            }

            attempt = next;
        }
    }

    private async Task CompleteReleaseAsync(ReleaseAttempt attempt, CancellationToken cancellationToken)
    {
        try { attempt.TrySetResult(await ReleaseCoreAsync(attempt, cancellationToken).ConfigureAwait(false)); }
        catch (OperationCanceledException error) { attempt.TrySetCanceled(error.CancellationToken); }
        catch (Exception error) { attempt.TrySetException(error); }
    }

    private async Task<LockReleaseOutcome> ReleaseCoreAsync(
        ReleaseAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            var released = _locks is IManagedLockCommands managed
                // Stop protected work as soon as the outcome is uncertain; the fence that
                // follows can wait on a control connection.
                ? await managed.ReleaseManagedAsync(Key, Token, MarkOwnershipLost, cancellationToken)
                    .ConfigureAwait(false)
                : await _locks.ReleaseAsync(Key, Token, cancellationToken).ConfigureAwait(false);
            lock (_releaseSync)
            {
                Volatile.Write(ref _state, released ? StateReleased : StateNotOwned);
                _releaseAttempt = null;
            }

            SignalLeaseChanged();
            return released ? LockReleaseOutcome.Released : LockReleaseOutcome.NotOwned;
        }
        catch (LockReleaseNotSubmittedException notSubmitted)
        {
            // Positive proof from the managed release that no delete reached Redis.
            RestoreOwnership(attempt);
            ExceptionDispatchInfo.Throw(notSubmitted.InnerException!);
            throw; // Unreachable: ExceptionDispatchInfo.Throw never returns.
        }
        catch (Exception error) when (RespireException.GetDefinitiveServerError(error) is not null)
        {
            // A server error is a definitive reply: the compare-and-DEL did not complete, so
            // this handle may still own the key and can safely retry.
            RestoreOwnership(attempt);
            throw;
        }
        catch
        {
            // The delete is undecided and may still execute after the caller stops waiting.
            // Conservatively stop protected work instead of claiming this handle remains held.
            lock (_releaseSync)
            {
                Volatile.Write(ref _state, StateNotOwned);
                _releaseAttempt = null;
            }

            SignalLeaseChanged();
            throw;
        }
    }

    private void RestoreOwnership(ReleaseAttempt attempt)
    {
        lock (_releaseSync)
        {
            // A keep-alive that reached the lease deadline meanwhile keeps the lock NotOwned.
            attempt.OwnershipRestored =
                Interlocked.CompareExchange(ref _state, StateHeld, StateReleasing) == StateReleasing;
            _releaseAttempt = null;
        }

        // Wake a keep-alive waiting for the release outcome.
        SignalLeaseChanged();
    }

    /// <summary>
    /// Releases the lock when <see cref="ReleaseAsync"/> has not already done so.
    /// </summary>
    /// <remarks>
    /// Failures that mean the command could not complete — a lost or disposed connection, or a
    /// command timeout — are swallowed, because disposal usually unwinds a scope that is already
    /// failing and must not replace the caller's exception with a cleanup one. The lock is not
    /// leaked by that: it expires on its own within <see cref="Duration"/>. Errors the server
    /// answered with are not swallowed. Call <see cref="ReleaseAsync"/> explicitly when the
    /// release itself must be observed.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var keepAlive = Volatile.Read(ref _ownedKeepAlive);
        if (keepAlive is not null && Interlocked.Exchange(ref _ownedKeepAliveDisposed, 1) == 0)
        {
            await keepAlive.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RespireConnectionException
            or RespireTimeoutException
            or ObjectDisposedException
            or OperationCanceledException)
        {
            // The release could not be delivered. Swallowed so cleanup never masks the caller's
            // own failure; expiry still frees the lock, and uncertain ownership stops protected
            // work conservatively.
        }
    }

    /// <summary>Generates a fresh owner token: a <see cref="Guid"/> as 32 ASCII hex bytes.</summary>
    internal static RespireLockToken NewToken()
    {
        var token = new byte[TokenLength];
        Guid.NewGuid().TryFormat(token.AsSpan(), out _, "N");
        return RespireLockToken.FromOwnedBytes(token);
    }

    internal void KeepAliveStopped() => Volatile.Write(ref _keepAlive, 0);

    /// <summary>The handle as the keep-alive loop sees it.</summary>
    internal enum KeepAlivePhase
    {
        /// <summary>Owned; renew before the remaining lease runs out.</summary>
        Held,
        /// <summary>A release is in flight; do not renew, wait for its outcome.</summary>
        Releasing,
        /// <summary>Released or ownership lost; stop.</summary>
        Ended,
    }

    /// <summary>
    /// Reads the keep-alive phase and the remaining lease from one state snapshot.
    /// <see cref="RemainingEstimate"/> reports zero while releasing, so reading the lease and the
    /// release state separately could mistake a just-started release for an elapsed lease.
    /// <paramref name="remaining"/> is zero unless the phase is <see cref="KeepAlivePhase.Held"/>.
    /// </summary>
    internal KeepAlivePhase GetKeepAlivePhase(out TimeSpan remaining)
    {
        switch (Volatile.Read(ref _state))
        {
            case StateHeld:
                remaining = RemainingUntilLeaseExpiry;
                return KeepAlivePhase.Held;
            case StateReleasing:
                remaining = TimeSpan.Zero;
                return KeepAlivePhase.Releasing;
            default:
                remaining = TimeSpan.Zero;
                return KeepAlivePhase.Ended;
        }
    }

    internal async ValueTask<bool> IsHeldByOriginAsync(CancellationToken cancellationToken)
    {
        var token = await _locks.GetOwnerTokenAsync(Key, cancellationToken).ConfigureAwait(false);
        return token is { } owner && owner == Token;
    }

    private void SignalLeaseChanged()
        => Interlocked.Exchange(ref _leaseChanged, new CancellationTokenSource()).Cancel();

    internal void MarkOwnershipLost()
    {
        if (TryMarkOwnershipLost())
        {
            SignalLeaseChanged();
        }
    }

    internal static TimeSpan NormalizeDuration(TimeSpan duration)
    {
        var milliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        if (duration.Ticks % TimeSpan.TicksPerMillisecond > 0
            && milliseconds < TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
        {
            milliseconds++;
        }

        return TimeSpan.FromTicks(milliseconds * TimeSpan.TicksPerMillisecond);
    }

    /// <summary>One shared release; joiners read <see cref="OwnershipRestored"/> after it completes.</summary>
    private sealed class ReleaseAttempt()
        : TaskCompletionSource<LockReleaseOutcome>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        /// <summary>Set before completion when the attempt failed without submitting a delete.</summary>
        internal volatile bool OwnershipRestored;
    }

    private bool TryMarkOwnershipLost()
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (state is StateReleased or StateNotOwned)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, StateNotOwned, state) == state)
            {
                return true;
            }
        }
    }
}

/// <summary>
/// Background renewal scope returned by <see cref="RespireLock.KeepAliveAsync"/>. Use
/// <see cref="CancellationToken"/> for protected work so ownership loss stops it promptly.
/// </summary>
public sealed class RespireLockKeepAlive : IAsyncDisposable
{
    private static readonly TimeSpan MinimumRenewalDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    private readonly RespireLock _lock;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _loop;
    private Exception? _failure;
    private int _ownershipLost;
    private int _disposed;

    internal RespireLockKeepAlive(RespireLock @lock, CancellationToken cancellationToken)
    {
        _lock = @lock;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
        _loop = RunAsync();
    }

    /// <summary>Cancelled when renewal fails, the caller cancels, or this scope is disposed.</summary>
    public CancellationToken CancellationToken => _lifetime.Token;

    /// <summary>Whether renewal reported or conservatively assumed lost ownership.</summary>
    public bool OwnershipLost => Volatile.Read(ref _ownershipLost) != 0;

    /// <summary>The renewal exception, when an error made ownership uncertain.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                var leaseChanged = _lock.LeaseChanged;
                var duration = _lock.Duration;
                var delay = GetRenewalDelay(duration, _lock.RemainingEstimate);
                if (delay > TimeSpan.Zero)
                {
                    using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        _lifetime.Token, leaseChanged);
                    try
                    {
                        await DelayInChunksAsync(delay, delayCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        leaseChanged.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                _lifetime.Token.ThrowIfCancellationRequested();
                // One state read: a release that starts between two reads must not look like an
                // elapsed lease, or the keep-alive would mark ownership lost and stop a release
                // that later proves it never submitted its delete from restoring ownership.
                var phase = _lock.GetKeepAlivePhase(out var remaining);
                switch (phase)
                {
                    case RespireLock.KeepAlivePhase.Releasing:
                        if (await WaitForReleaseOutcomeAsync(leaseChanged).ConfigureAwait(false))
                        {
                            continue;
                        }

                        return;
                    case RespireLock.KeepAlivePhase.Held when remaining > TimeSpan.Zero:
                        break;
                    default:
                        // The lease elapsed, or the handle ended without a lease change this loop saw.
                        MarkOwnershipUncertain();
                        return;
                }

                if (!await RenewBeforeDeadlineAsync(remaining).ConfigureAwait(false))
                {
                    if (_lock.GetKeepAlivePhase(out _) == RespireLock.KeepAlivePhase.Releasing
                        || leaseChanged.IsCancellationRequested)
                    {
                        // A release started after the snapshot, so the renewal stopped at the
                        // handle. Re-evaluate: wait for that release, or stop if it ended ownership.
                        continue;
                    }

                    Volatile.Write(ref _ownershipLost, 1);
                    await _lifetime.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _failure, ex);
            Volatile.Write(ref _ownershipLost, 1);
            _lock.MarkOwnershipLost();
            await _lifetime.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.KeepAliveStopped();
        }
    }

    /// <summary>
    /// Waits for an in-flight release to settle without renewing. A release can outlast the
    /// lease, so the wait is bounded by the last known lease deadline. Returns
    /// <see langword="true"/> when the release outcome changed the lease and the loop should
    /// re-evaluate, or <see langword="false"/> after the deadline passed and ownership was
    /// marked uncertain.
    /// </summary>
    private async ValueTask<bool> WaitForReleaseOutcomeAsync(CancellationToken leaseChanged)
    {
        var remaining = _lock.RemainingUntilLeaseExpiry;
        if (remaining > TimeSpan.Zero)
        {
            using var releaseOutcome = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetime.Token, leaseChanged);
            try
            {
                await DelayInChunksAsync(remaining, releaseOutcome.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                leaseChanged.IsCancellationRequested && !_lifetime.IsCancellationRequested)
            {
                return true;
            }

            _lifetime.Token.ThrowIfCancellationRequested();
            if (leaseChanged.IsCancellationRequested)
            {
                return true;
            }
        }

        MarkOwnershipUncertain();
        return false;
    }

    internal static TimeSpan GetRenewalDelay(TimeSpan duration, TimeSpan remaining)
    {
        var delay = remaining - TimeSpan.FromTicks(duration.Ticks / 2);
        if (delay > MinimumRenewalDelay)
        {
            return delay;
        }

        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining > MinimumRenewalDelay
            ? MinimumRenewalDelay
            : TimeSpan.FromTicks(Math.Max(1, remaining.Ticks / 2));
    }

    internal static TimeSpan GetTimerDelayChunk(TimeSpan remaining)
        => remaining > MaximumTimerDelay ? MaximumTimerDelay : remaining;

    private async ValueTask<bool> RenewBeforeDeadlineAsync(TimeSpan remaining)
    {
        using var deadlineStop = new CancellationTokenSource();
        using var renewalCancellation = new CancellationTokenSource();
        var deadline = MarkOwnershipLostAtDeadlineAsync(
            remaining, deadlineStop.Token, renewalCancellation);
        try
        {
            return await _lock.RenewAsync(MarkOwnershipUncertain, renewalCancellation.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            await deadlineStop.CancelAsync().ConfigureAwait(false);
            await deadline.ConfigureAwait(false);
        }
    }

    private async Task MarkOwnershipLostAtDeadlineAsync(
        TimeSpan remaining,
        CancellationToken stop,
        CancellationTokenSource renewalCancellation)
    {
        try
        {
            await DelayInChunksAsync(remaining, stop).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return;
        }

        MarkOwnershipUncertain();
        await renewalCancellation.CancelAsync().ConfigureAwait(false);
    }

    private async Task DelayInChunksAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        while (delay > TimeSpan.Zero)
        {
            var chunk = GetTimerDelayChunk(delay);
            await Task.Delay(chunk, _lock.Clock, cancellationToken).ConfigureAwait(false);
            delay -= chunk;
        }
    }

    private void MarkOwnershipUncertain()
    {
        Volatile.Write(ref _ownershipLost, 1);
        _lock.MarkOwnershipLost();
        _ = _lifetime.CancelAsync();
    }

    /// <summary>Stops renewal and waits for the background renewal loop to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        _lifetime.Dispose();
        _stop.Dispose();
    }
}
