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
    internal void PrepareForUse(int receiveReferences = 1) => _refs = receiveReferences + 1;

    public virtual bool TrySetResult(in RespValue result)
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

        DispatchException(exception);
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
        {
            return false;
        }

        DispatchException(new OperationCanceledException(cancellationToken));
        return true;
    }

    /// <summary>
    /// Cores run continuations inline (see <see cref="CompletionScheduler"/>), so failure
    /// paths — cancellation callbacks on arbitrary user threads, connection teardown — must
    /// hop to the pool themselves rather than run caller continuations where they stand.
    /// </summary>
    private void DispatchException(Exception exception)
        => ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Self.SetExceptionCore(state.Exception),
            (Self: this, Exception: exception),
            preferLocal: false);

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

/// <summary>
/// One pooled completion for every reply in a MULTI/EXEC sequence. Intermediate replies are
/// drained in place; the first queue error replaces EXEC's reply so cluster redirects survive
/// a following EXECABORT without allocating one task source per queued command.
/// </summary>
internal sealed class TransactionPendingResponseSource : PendingResponse, IValueTaskSource<RespValue>
{
    private const int MaxPoolSize = 4096;
    private static readonly LockFreeStack<TransactionPendingResponseSource> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _queueError;
    private int _replyCount;
    private int _firstQueueReply;
    private int _replyIndex;
    private bool _hasQueueError;

    private TransactionPendingResponseSource()
    {
    }

    internal ValueTask<RespValue> Task => new(this, _core.Version);

    internal static TransactionPendingResponseSource Rent(int replyCount, int firstQueueReply)
    {
        if (!Pool.TryPop(out var source))
        {
            source = new TransactionPendingResponseSource();
        }

        source._replyCount = replyCount;
        source._firstQueueReply = firstQueueReply;
        source.PrepareForUse(replyCount);
        return source;
    }

    public override bool TrySetResult(in RespValue result)
    {
        var index = _replyIndex++;
        if (index < _replyCount - 1)
        {
            if (index >= _firstQueueReply && result.IsError && !_hasQueueError)
            {
                _queueError = result;
                _hasQueueError = true;
            }
            else
            {
                result.Dispose();
            }

            return true;
        }

        var completion = result;
        if (_hasQueueError)
        {
            result.Dispose();
            completion = _queueError;
            _queueError = default;
            _hasQueueError = false;
        }

        if (!base.TrySetResult(in completion))
        {
            completion.Dispose();
        }

        // This source owns intermediate disposal and final cancellation races.
        return true;
    }

    protected override void SetResultCore(in RespValue result) => _core.SetResult(result);

    protected override void SetExceptionCore(Exception exception) => _core.SetException(exception);

    RespValue IValueTaskSource<RespValue>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
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
        if (_hasQueueError)
        {
            _queueError.Dispose();
        }

        _queueError = default;
        _replyCount = 0;
        _firstQueueReply = 0;
        _replyIndex = 0;
        _hasQueueError = false;
        _core.Reset();
        Pool.TryPush(this);
    }
}

/// <summary>Poolable raw RESP response source.</summary>
internal sealed class PendingResponseSource : PendingResponse, IValueTaskSource<RespValue>
{
    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = false };
    private PendingResponsePool? _pool;
    private bool _throwOnError;
    private string? _commandName;

    public ValueTask<RespValue> Task => new(this, _core.Version);

    internal void SetPool(PendingResponsePool pool) => _pool = pool;

    internal void Configure(bool throwOnError, string? commandName)
    {
        _throwOnError = throwOnError;
        _commandName = commandName;
    }

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

            var error = ResponseReader.ServerError(in response, _commandName);
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
        _commandName = null;
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

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _response;
    private ResponseConverter<TState, TResult>? _converter;
    private TState _state = default!;
    private bool _hasResponse;
    private bool _transferOwnership;
    private string? _commandName;

    private ConvertedPendingResponseSource()
    {
    }

    public ValueTask<TResult> Task => new(this, _core.Version);

    public static ConvertedPendingResponseSource<TState, TResult> Rent(
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership,
        string? commandName)
    {
        if (!Pool.TryPop(out var source))
        {
            source = new ConvertedPendingResponseSource<TState, TResult>();
        }

        source._state = state;
        source._converter = converter;
        source._transferOwnership = transferOwnership;
        source._commandName = commandName;
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
                throw ResponseReader.ServerError(in _response, _commandName);
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
        _commandName = null;
    }
}

/// <summary>Bounded, allocation-free pool of raw response sources.</summary>
internal sealed class PendingResponsePool
{
    private readonly LockFreeStack<PendingResponseSource> _stack;

    public PendingResponsePool(int maxPoolSize) => _stack = new LockFreeStack<PendingResponseSource>(maxPoolSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PendingResponseSource Rent(bool throwOnError = false, string? commandName = null)
    {
        if (!_stack.TryPop(out var source))
        {
            source = new PendingResponseSource();
            source.SetPool(this);
        }

        source.Configure(throwOnError, commandName);
        source.PrepareForUse();
        return source;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PendingResponseSource source) => _stack.TryPush(source);
}
