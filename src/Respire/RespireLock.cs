using System.Diagnostics;

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
/// even mid-work. Keep protected work shorter than the duration, call <see cref="ExtendAsync"/>,
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
    private readonly object _releaseSync = new();
    private Task<LockReleaseOutcome>? _releaseTask;

    internal RespireLock(
        ILockCommands locks,
        RespireKey key,
        ReadOnlyMemory<byte> token,
        TimeSpan duration,
        long acquiredTimestamp)
    {
        _locks = locks;
        Key = key.Snapshot();
        Token = token;
        _durationTicks = duration.Ticks;
        _renewedTimestamp = acquiredTimestamp;
    }

    /// <summary>The locked key, as passed to <c>AcquireAsync</c> (before any client key prefix).</summary>
    public RespireKey Key { get; }

    /// <summary>
    /// The owner token stored in the key: 32 ASCII hex characters from a <see cref="Guid"/>. Held
    /// as bytes so it compares byte-for-byte with the value the server round-trips, and with
    /// <see cref="ILockCommands.GetOwnerTokenAsync"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Token { get; }

    /// <summary>
    /// The lease duration currently applied to the lock: the one it was acquired with, or the one
    /// from the most recent successful <see cref="ExtendAsync"/>.
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

            var remaining = Duration - Stopwatch.GetElapsedTime(Interlocked.Read(ref _renewedTimestamp));
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>Best-effort wall-clock expiry instant derived from <see cref="RemainingEstimate"/>.</summary>
    public DateTimeOffset ExpiresAtEstimate => DateTimeOffset.UtcNow + RemainingEstimate;

    /// <summary>Whether this handle no longer considers itself the lock owner.</summary>
    public bool IsReleased
        => Volatile.Read(ref _state) != StateHeld || RemainingEstimate == TimeSpan.Zero;

    /// <summary>
    /// Resets the lock's expiry to <paramref name="expiry"/> from now, only while this handle is
    /// still the owner. Redis: compare-and-PEXPIRE.
    /// </summary>
    /// <remarks>
    /// Managed extensions that can time out or be cancelled use Redis <c>CLIENT ID</c> and
    /// <c>CLIENT KILL</c> to fence uncertain commands; the authenticated user must permit them.
    /// </remarks>
    /// <returns>
    /// <see langword="false"/> when the lock is no longer owned — it expired, it was released, or
    /// another owner took it — in which case protected work must stop rather than retry.
    /// </returns>
    public ValueTask<bool> ExtendAsync(TimeSpan expiry, CancellationToken cancellationToken = default)
        => ExtendCoreAsync(expiry, signalLeaseChanged: true, onOutcomeUncertain: null, cancellationToken);

    internal ValueTask<bool> RenewAsync(Action onOutcomeUncertain, CancellationToken cancellationToken)
        => ExtendCoreAsync(expiry: null, signalLeaseChanged: false, onOutcomeUncertain, cancellationToken);

    internal CancellationToken LeaseChanged => Volatile.Read(ref _leaseChanged).Token;

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

            var effectiveExpiry = expiry ?? Duration;
            var renewedTimestamp = Stopwatch.GetTimestamp();
            var extended = _locks is IManagedLockCommands managed
                ? await managed.ExtendManagedAsync(
                        Key, Token, effectiveExpiry, onOutcomeUncertain, cancellationToken)
                    .ConfigureAwait(false)
                : await _locks.ExtendAsync(Key, Token, effectiveExpiry, cancellationToken).ConfigureAwait(false);
            if (!extended)
            {
                changed = Interlocked.CompareExchange(ref _state, StateNotOwned, StateHeld) == StateHeld;
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
            return ValueTask.FromResult(new RespireLockKeepAlive(this, cancellationToken));
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
    /// cancellation token of the caller that starts it.
    /// </summary>
    /// <returns>
    /// Distinguishes a successful delete, a repeat call, and lost ownership.
    /// </returns>
    public ValueTask<LockReleaseOutcome> ReleaseAsync(CancellationToken cancellationToken = default)
    {
        lock (_releaseSync)
        {
            var state = Volatile.Read(ref _state);
            if (state == StateReleased)
            {
                return ValueTask.FromResult(LockReleaseOutcome.AlreadyReleased);
            }

            if (state == StateNotOwned)
            {
                return ValueTask.FromResult(LockReleaseOutcome.NotOwned);
            }

            if (state == StateReleasing)
            {
                return new ValueTask<LockReleaseOutcome>(_releaseTask!);
            }

            Volatile.Write(ref _state, StateReleasing);
            return new ValueTask<LockReleaseOutcome>(
                _releaseTask = ReleaseCoreAsync(cancellationToken));
        }
    }

    private async Task<LockReleaseOutcome> ReleaseCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var released = await _locks.ReleaseAsync(Key, Token, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state, released ? StateReleased : StateNotOwned);
            SignalLeaseChanged();
            return released ? LockReleaseOutcome.Released : LockReleaseOutcome.NotOwned;
        }
        catch
        {
            // The delete is undecided and may still execute after the caller stops waiting.
            // Conservatively stop protected work instead of claiming this handle remains held.
            lock (_releaseSync)
            {
                Volatile.Write(ref _state, StateNotOwned);
                _releaseTask = null;
            }

            SignalLeaseChanged();
            throw;
        }
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
    internal static ReadOnlyMemory<byte> NewToken()
    {
        var token = new byte[TokenLength];
        Guid.NewGuid().TryFormat(token.AsSpan(), out _, "N");
        return token;
    }

    internal void KeepAliveStopped() => Volatile.Write(ref _keepAlive, 0);

    internal async ValueTask<bool> IsHeldByOriginAsync(CancellationToken cancellationToken)
    {
        var token = await _locks.GetOwnerTokenAsync(Key, cancellationToken).ConfigureAwait(false);
        return token is not null && token.AsSpan().SequenceEqual(Token.Span);
    }

    private void SignalLeaseChanged()
        => Interlocked.Exchange(ref _leaseChanged, new CancellationTokenSource()).Cancel();

    internal void MarkOwnershipLost()
        => Interlocked.CompareExchange(ref _state, StateNotOwned, StateHeld);
}

/// <summary>
/// Background renewal scope returned by <see cref="RespireLock.KeepAliveAsync"/>. Use
/// <see cref="CancellationToken"/> for protected work so ownership loss stops it promptly.
/// </summary>
public sealed class RespireLockKeepAlive : IAsyncDisposable
{
    private static readonly TimeSpan MinimumRenewalDelay = TimeSpan.FromMilliseconds(10);

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
                        await Task.Delay(delay, delayCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        leaseChanged.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        continue;
                    }
                }

                if (!await _lock.RenewAsync(MarkOwnershipUncertain, _lifetime.Token).ConfigureAwait(false))
                {
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

    internal static TimeSpan GetRenewalDelay(TimeSpan duration, TimeSpan remaining)
    {
        var delay = remaining - TimeSpan.FromTicks(duration.Ticks / 2);
        return delay > MinimumRenewalDelay ? delay : MinimumRenewalDelay;
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
