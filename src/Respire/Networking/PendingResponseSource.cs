using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// A poolable <see cref="IValueTaskSource{RespValue}"/> representing one in-flight command
/// awaiting its RESP response. Wraps <see cref="ManualResetValueTaskSourceCore{T}"/> for
/// zero-allocation async completion.
/// </summary>
/// <remarks>
/// <para>
/// Completion methods use CAS on <c>_completed</c>; only the first to complete wins.
/// </para>
/// <para>
/// Lifetime uses two references: one held by the awaiting caller (released inside
/// <c>GetResult</c>) and one held by the connection's receive loop (released after it dequeues
/// this source from the in-flight ring and completes or discards it). The source returns to its
/// pool only when both are released, so a caller-side cancellation can never recycle a source
/// that the receive loop still holds a FIFO slot for — the RESP protocol has no correlation
/// ids, so a recycled source completing a stale slot would silently answer the wrong command.
/// </para>
/// </remarks>
internal sealed class PendingResponseSource : IValueTaskSource<RespValue>
{
    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = true };
    private PendingResponsePool? _pool;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _completed;
    private int _refs;

    public ValueTask<RespValue> Task => new(this, _core.Version);

    internal void SetPool(PendingResponsePool pool) => _pool = pool;

    /// <summary>Called by the pool on rent, before the source is published anywhere.</summary>
    internal void PrepareForUse() => _refs = 2;

    public bool TrySetResult(in RespValue result)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        _core.SetResult(result);
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        _core.SetException(exception);
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        _core.SetException(new OperationCanceledException(cancellationToken));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RegisterCancellation(CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        _cancellationRegistration = cancellationToken.UnsafeRegister(
            static (state, token) => ((PendingResponseSource)state!).TrySetCanceled(token),
            this);
    }

    /// <summary>
    /// Releases one of the two lifetime references. The last release resets the core and
    /// returns the source to its pool.
    /// </summary>
    internal void ReleaseRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }

        Volatile.Write(ref _completed, 0);
        _core.Reset();
        _pool?.Return(this);
    }

    RespValue IValueTaskSource<RespValue>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            _cancellationRegistration.Dispose();
            _cancellationRegistration = default;
            ReleaseRef();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<RespValue>.GetStatus(short token)
        => _core.GetStatus(token);

    void IValueTaskSource<RespValue>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}

/// <summary>
/// Bounded, allocation-free pool of <see cref="PendingResponseSource"/> instances backed by
/// <see cref="LockFreeStack{T}"/>. When the pool is empty a new instance is created; when the
/// pool is full a returned instance is discarded to the GC.
/// </summary>
internal sealed class PendingResponsePool
{
    private readonly LockFreeStack<PendingResponseSource> _stack;

    public PendingResponsePool(int maxPoolSize)
    {
        _stack = new LockFreeStack<PendingResponseSource>(maxPoolSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PendingResponseSource Rent()
    {
        if (!_stack.TryPop(out var source))
        {
            source = new PendingResponseSource();
            source.SetPool(this);
        }

        source.PrepareForUse();
        return source;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PendingResponseSource source) => _stack.TryPush(source);
}
