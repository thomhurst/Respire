namespace Respire;

/// <summary>Reasons an observed key may need to be read again. Coalesced notifications combine flags.</summary>
[Flags]
public enum RespireClientCacheInvalidationReason
{
    /// <summary>No invalidation.</summary>
    None = 0,
    /// <summary>Redis invalidated the key or flushed tracked state.</summary>
    ServerInvalidation = 1,
    /// <summary>A local command conservatively invalidated the key before or after execution.</summary>
    LocalMutation = 2,
    /// <summary>The application explicitly cleared the local cache.</summary>
    ExplicitClear = 4,
    /// <summary>Tracking continuity was lost. The current server value is unknown.</summary>
    ContinuityLost = 8,
}

/// <summary>An owned physical key and the coalesced reasons to recheck its state.</summary>
/// <remarks>This is a wake-up signal, not a write event or proof that a value changed.</remarks>
public readonly record struct RespireClientCacheInvalidation(
    RespireKey Key, RespireClientCacheInvalidationReason Reasons);

/// <summary>A bounded asynchronous observation of one physical cache key.</summary>
/// <remarks>At most one callback executes and one coalesced notification waits per subscription.
/// Callbacks never execute inline on an invalidating thread. Their exceptions are retained in
/// <see cref="LastObserverException"/> and do not interrupt cache eviction or later callbacks.
/// ExecutionContext is not captured. Use synchronous callbacks; async-void failures cannot be caught.
/// Dispose does not join callbacks, so a callback may safely dispose itself or its client.</remarks>
public sealed class RespireClientCacheInvalidationSubscription : IDisposable
{
    private readonly ClientSideCacheCoordinator _owner;
    private readonly Action<RespireClientCacheInvalidation> _observer;
    private readonly Lock _gate = new();
    private CancellationTokenRegistration _cancellation;
    private RespireClientCacheInvalidationReason _pending;
    private bool _dispatching;
    private int _disposed;
    private Exception? _lastObserverException;

    internal RespireClientCacheInvalidationSubscription(ClientSideCacheCoordinator owner,
        RespireKey key, Action<RespireClientCacheInvalidation> observer)
        => (_owner, Key, _observer) = (owner, key, observer);

    /// <summary>The owned physical key supplied at registration.</summary>
    public RespireKey Key { get; }

    /// <summary>Whether cancellation, subscription disposal, or client disposal stopped observation.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>The most recent callback failure, or null if no callback has thrown.</summary>
    public Exception? LastObserverException => Volatile.Read(ref _lastObserverException);

    internal void RegisterCancellation(CancellationToken cancellationToken)
    {
        var registration = cancellationToken.UnsafeRegister(
            static state => ((RespireClientCacheInvalidationSubscription)state!).Dispose(), this);
        // Cancellation can run synchronously before UnsafeRegister returns its registration.
        lock (_gate)
        {
            if (!IsDisposed)
            {
                _cancellation = registration;
                return;
            }
        }
        registration.Unregister();
    }

    internal void Enqueue(RespireClientCacheInvalidationReason reason)
    {
        lock (_gate)
        {
            if (IsDisposed) return;
            _pending |= reason;
            if (_dispatching) return;
            _dispatching = true;
            ThreadPool.UnsafeQueueUserWorkItem(static subscription => subscription.Dispatch(), this, preferLocal: false);
        }
    }

    private void Dispatch()
    {
        while (true)
        {
            RespireClientCacheInvalidationReason reasons;
            lock (_gate)
            {
                if (IsDisposed || _pending == RespireClientCacheInvalidationReason.None)
                {
                    _pending = RespireClientCacheInvalidationReason.None;
                    _dispatching = false;
                    return;
                }
                reasons = _pending;
                _pending = RespireClientCacheInvalidationReason.None;
            }
            try { _observer(new(Key, reasons)); }
            catch (Exception error) { Volatile.Write(ref _lastObserverException, error); }
        }
    }

    /// <summary>Stops observation and discards pending notifications without waiting for an active callback.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _owner.RemoveInvalidationObserver(this);
        CancellationTokenRegistration registration;
        lock (_gate)
        {
            registration = _cancellation;
            _cancellation = default;
            _pending = RespireClientCacheInvalidationReason.None;
        }
        registration.Unregister();
    }
}
