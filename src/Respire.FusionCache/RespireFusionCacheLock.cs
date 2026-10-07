using Microsoft.Extensions.Logging;
using Respire.Coordination;
#if !NET9_0_OR_GREATER
using Lock = System.Object;
#endif

namespace Respire.FusionCache;

/// <summary>The opaque FusionCache lock handle, with fencing and ownership diagnostics.</summary>
/// <remarks>
/// FusionCache does not pass this handle to its factory or enforce its fencing token on writes.
/// Applications that need fenced writes must use a cooperating resource and the coordination API directly.
/// </remarks>
public sealed partial class RespireFusionCacheLock : IAsyncDisposable
{
    private readonly RespireFusionCacheDistributedLocker _owner;
    private readonly RespireFencedLock _lease;
    private readonly RespireLockKeepAlive _keepAlive;
    private readonly CancellationTokenSource _lifetime;
    private readonly ILogger? _logger;
    private readonly Lock _sync = new();
    private CancellationTokenRegistration _registration;
    private TaskCompletionSource? _cleanup;

    internal RespireFusionCacheLock(RespireFusionCacheDistributedLocker owner, RespireFencedLock lease,
        RespireLockKeepAlive keepAlive, CancellationTokenSource lifetime, ILogger? logger)
    {
        _owner = owner;
        _lease = lease;
        _keepAlive = keepAlive;
        _lifetime = lifetime;
        _logger = logger;
        OwnershipCancellationToken = keepAlive.CancellationToken;
    }

    /// <summary>The lease key before the caller's client prefix.</summary>
    public RespireKey LeaseKey => _lease.Key;
    /// <summary>The persistent fencing counter key before the caller's client prefix.</summary>
    public RespireKey FencingCounterKey => _lease.FencingCounterKey;
    /// <summary>The positive token allocated by Redis. FusionCache does not enforce it on factory writes.</summary>
    public long FencingToken => _lease.FencingToken;
    /// <summary>Whether renewal conservatively reported lost ownership.</summary>
    public bool OwnershipLost => _keepAlive.OwnershipLost;
    /// <summary>The renewal exception that made ownership uncertain, when present.</summary>
    public Exception? RenewalFailure => _keepAlive.Failure;
    /// <summary>Signals ownership loss, caller cancellation, or teardown; it cannot cancel FusionCache's factory automatically.</summary>
    public CancellationToken OwnershipCancellationToken { get; }

    internal bool BelongsTo(RespireFusionCacheDistributedLocker owner) => ReferenceEquals(_owner, owner);

    internal void ObserveLifetime()
    {
        var registration = OwnershipCancellationToken.UnsafeRegister(static state =>
        {
            _ = ((RespireFusionCacheLock)state!).CleanupAfterCancellationAsync();
        }, this);
        // Register can invoke the callback synchronously when cancellation won the handoff race.
        lock (_sync)
        {
            if (_cleanup is null) _registration = registration;
            else registration.Unregister();
        }
    }

    private async Task CleanupAfterCancellationAsync()
    {
        try { await DisposeAsync().ConfigureAwait(false); }
        catch
        {
            // CompleteCleanupAsync logs the failure. Cancellation callbacks have no caller
            // to receive it; explicit release and teardown joining this handle observe the fault.
        }
    }

    /// <summary>Stops and joins renewal, then releases only this owner. Concurrent teardown joins the same cleanup.</summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        CancellationTokenRegistration registration;
        lock (_sync)
        {
            if (_cleanup is not null) return new ValueTask(_cleanup.Task);
            completion = _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            // Setting _cleanup freezes registration writes under this same gate. If Register
            // invokes cleanup synchronously, ObserveLifetime unregisters its returned value instead.
            registration = _registration;
        }
        _ = CompleteCleanupAsync(completion, registration);
        return new ValueTask(completion.Task);
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion, CancellationTokenRegistration registration)
    {
        Exception? failure = null;
        try
        {
            registration.Unregister();
            await _keepAlive.DisposeAsync().ConfigureAwait(false);
            if (_keepAlive.OwnershipLost && _logger is not null)
                LogOwnershipLost(_logger, LeaseKey, _keepAlive.Failure);
            // Never pass an already-cancelled factory token to owner-checked cleanup.
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            try { if (_logger is not null) LogCleanupFailed(_logger, LeaseKey, error); }
            catch { /* Logging must not replace the release error or leave teardown pending. */ }
        }
        finally
        {
            _lifetime.Dispose();
            _owner.Forget(this);
            if (failure is null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }

    [LoggerMessage(6, LogLevel.Warning, "FusionCache lease {LeaseKey} lost ownership; its factory may still be running.")]
    private static partial void LogOwnershipLost(ILogger logger, RespireKey leaseKey, Exception? exception);

    [LoggerMessage(7, LogLevel.Warning, "Could not release FusionCache lease {LeaseKey}; renewal stopped and server expiry remains the fallback.")]
    private static partial void LogCleanupFailed(ILogger logger, RespireKey leaseKey, Exception exception);
}
