using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// Type-erased base for one in-flight command. Two references keep each source alive: caller
/// and receive loop. Cancellation can therefore complete caller without corrupting RESP FIFO.
/// </summary>
/// <remarks>
/// Completion uses CAS, so only response, cancellation, or connection failure can win. Source
/// returns to pool only after caller consumes completion and receive loop dequeues its FIFO slot;
/// otherwise a cancelled source could be reused and a stale reply could answer another command.
/// </remarks>
internal abstract class PendingResponse
{
    private CancellationTokenRegistration _cancellationRegistration;
    private int _completed;
    private int _refs;

    /// <summary>Called after rent, before source is published anywhere.</summary>
    internal void PrepareForUse() => _refs = 2;

    public bool TrySetResult(in RespValue result)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        SetResultCore(in result);
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        SetExceptionCore(exception);
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        SetExceptionCore(new OperationCanceledException(cancellationToken));
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
            static (state, token) => ((PendingResponse)state!).TrySetCanceled(token),
            this);
    }

    protected void ReleaseCallerRef()
    {
        _cancellationRegistration.Dispose();
        _cancellationRegistration = default;
        ReleaseRef();
    }

    /// <summary>Returns source to its pool after caller and receive loop both release it.</summary>
    internal void ReleaseRef()
    {
        if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }

        Volatile.Write(ref _completed, 0);
        ResetAndReturn();
    }

    protected abstract void SetResultCore(in RespValue result);

    protected abstract void SetExceptionCore(Exception exception);

    protected abstract void ResetAndReturn();
}

/// <summary>Poolable raw RESP response source.</summary>
internal sealed class PendingResponseSource : PendingResponse, IValueTaskSource<RespValue>
{
    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = true };
    private PendingResponsePool? _pool;
    private bool _throwOnError;

    public ValueTask<RespValue> Task => new(this, _core.Version);

    internal void SetPool(PendingResponsePool pool) => _pool = pool;

    internal void Configure(bool throwOnError) => _throwOnError = throwOnError;

    protected override void SetResultCore(in RespValue result) => _core.SetResult(result);

    protected override void SetExceptionCore(Exception exception) => _core.SetException(exception);

    RespValue IValueTaskSource<RespValue>.GetResult(short token)
    {
        try
        {
            var response = _core.GetResult(token);
            if (!_throwOnError || !response.IsError)
            {
                return response;
            }

            var error = ResponseReader.ServerError(in response);
            response.Dispose();
            throw error;
        }
        finally
        {
            ReleaseCallerRef();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<RespValue>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<RespValue>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    protected override void ResetAndReturn()
    {
        _throwOnError = false;
        _core.Reset();
        _pool?.Return(this);
    }
}

/// <summary>
/// Poolable typed response source. Receive loop stores raw response and signals completion;
/// conversion and disposal occur when caller consumes result, outside receive loop.
/// </summary>
internal sealed class ConvertedPendingResponseSource<TState, TResult> : PendingResponse, IValueTaskSource<TResult>
{
    private const int MaxPoolSize = 4096;
    private static readonly LockFreeStack<ConvertedPendingResponseSource<TState, TResult>> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
    private RespValue _response;
    private ResponseConverter<TState, TResult>? _converter;
    private TState _state = default!;
    private bool _hasResponse;
    private bool _transferOwnership;

    private ConvertedPendingResponseSource()
    {
    }

    public ValueTask<TResult> Task => new(this, _core.Version);

    public static ConvertedPendingResponseSource<TState, TResult> Rent(
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership)
    {
        if (!Pool.TryPop(out var source))
        {
            source = new ConvertedPendingResponseSource<TState, TResult>();
        }

        source._state = state;
        source._converter = converter;
        source._transferOwnership = transferOwnership;
        source.PrepareForUse();
        return source;
    }

    protected override void SetResultCore(in RespValue result)
    {
        _response = result;
        _hasResponse = true;
        _core.SetResult(true);
    }

    protected override void SetExceptionCore(Exception exception) => _core.SetException(exception);

    TResult IValueTaskSource<TResult>.GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
            if (_response.IsError)
            {
                throw ResponseReader.ServerError(in _response);
            }

            var result = _converter!(_state, in _response);
            if (_transferOwnership)
            {
                _hasResponse = false;
            }

            return result;
        }
        finally
        {
            ClearResponse();
            ReleaseCallerRef();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<TResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    protected override void ResetAndReturn()
    {
        ClearResponse();
        _core.Reset();
        Pool.TryPush(this);
    }

    private void ClearResponse()
    {
        if (_hasResponse)
        {
            _response.Dispose();
        }

        _response = default;
        _state = default!;
        _converter = null;
        _hasResponse = false;
        _transferOwnership = false;
    }
}

/// <summary>Bounded, allocation-free pool of raw response sources.</summary>
internal sealed class PendingResponsePool
{
    private readonly LockFreeStack<PendingResponseSource> _stack;

    public PendingResponsePool(int maxPoolSize) => _stack = new LockFreeStack<PendingResponseSource>(maxPoolSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PendingResponseSource Rent(bool throwOnError = false)
    {
        if (!_stack.TryPop(out var source))
        {
            source = new PendingResponseSource();
            source.SetPool(this);
        }

        source.Configure(throwOnError);
        source.PrepareForUse();
        return source;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PendingResponseSource source) => _stack.TryPush(source);
}
