using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Reservoir;
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
internal abstract partial class PendingResponse
{
    private CancellationTokenRegistration _cancellationRegistration;

    // Low bit: completed. Upper bits: reuse epoch, bumped every time the source goes back to
    // its pool. Completion is a CAS on the whole word so the deadline sweep — which peeks
    // ring slots without holding a reference — can never complete a recycled source: its
    // captured state carries the old epoch and the CAS fails.
    private long _state;
    private int _refs;
    private ClientSideCacheCoordinator.MutationReference _mutationReference;

    internal void BindMutationFence(in ClientSideCacheCoordinator.MutationFence fence)
    {
        if (!fence.IsRequired) return;
        if (_mutationReference.IsRequired)
            throw new InvalidOperationException("A native response already owns a cache mutation fence.");
        _mutationReference = fence.BindNative();
        if (!_mutationReference.IsRequired)
            throw new InvalidOperationException("An accepted mutation must retain its live fence.");
    }

    /// <summary>
    /// Absolute <see cref="Environment.TickCount64"/> deadline stamped at enqueue, or
    /// <see cref="CommandDeadline.None"/>. Written before the ring slot is published, read
    /// afterwards by the deadline sweep (its only reader).
    /// </summary>
    private long _deadline;
    internal CommandDeadline Deadline
    {
        get => CommandDeadline.FromRawValue(Volatile.Read(ref _deadline));
        set => Volatile.Write(ref _deadline, value.RawValue);
    }
    internal long WriteStart;
    internal long WriteEnd;

    /// <summary>Command label used in timeout errors; null when the source carries none.</summary>
    internal virtual string? CommandName => null;

    // Copy before admission; both owners retain this value until the FIFO reply drains.
    // Never retain the caller's pooled observation after its final boundary completes.
    internal int ErrorAttempts { get; set; }

    /// <summary>Completion state captured by the deadline sweep for its epoch-checked CAS.</summary>
    internal long State => Volatile.Read(ref _state);

    internal static bool IsCompleted(long state) => (state & 1) != 0;

    /// <summary>Called after rent, before source is published anywhere.</summary>
    internal void PrepareForUse(int receiveReferences = 1)
    {
        _refs = receiveReferences + 1;
        ErrorAttempts = 0;
    }

    public virtual bool TrySetResult(in RespValue result)
    {
        if (!TryAcquireCompletion())
        {
            return false;
        }

        SetResultCore(in result);
        return true;
    }

    // The receive loop reserves completion before handing the reply to the scheduler.
    // Cancellation must not discard a reply already parsed while an earlier continuation runs.
    internal virtual bool TryReserveResult()
    {
        // The FIFO slot has been dequeued and its full reply parsed. Release the native
        // fence before delivery can run an inline caller and its subsequent cache reads.
        // Multi-reply sources reach this only for their final reply. Cancellation still
        // retains the fence until that reply drains or connection teardown returns us.
        RetireMutationFence();
        return TryAcquireCompletion();
    }

    private void RetireMutationFence()
    {
        if (!_mutationReference.IsRequired) return;
        var mutationReference = _mutationReference;
        _mutationReference = default;
        mutationReference.Release();
    }

    internal virtual bool CompleteReservedResult(in RespValue result)
    {
        SetResultCore(in result);
        return true;
    }

    public bool TrySetException(Exception exception)
    {
        if (!TryAcquireCompletion())
        {
            return false;
        }

        DispatchException(exception);
        return true;
    }

    public bool TrySetCanceled(CancellationToken cancellationToken)
    {
        if (!TryAcquireCompletion())
        {
            return false;
        }

        DispatchException(new OperationCanceledException(cancellationToken));
        return true;
    }

    /// <summary>
    /// Deadline sweep only. <paramref name="observedState"/> is the state captured when the
    /// sweep read this source from its ring slot; the CAS fails if the source completed or
    /// was recycled since, so a stale peek can never time out a different command.
    /// </summary>
    internal bool TrySetTimedOut(long observedState, TimeSpan timeout,
        ref RespireTimeoutDiagnostics? diagnostics, RespireConnection? connection)
    {
        if (Interlocked.CompareExchange(ref _state, observedState | 1, observedState) != observedState)
        {
            return false;
        }

        diagnostics ??= connection?.CaptureTimeoutDiagnostics() ?? RespireTimeoutDiagnostics.Capture();
        DispatchException(new RespireTimeoutException(CommandName ?? "(command)", timeout, null,
            diagnostics.ForCommand(WriteStart, WriteEnd)));
        return true;
    }

    private bool TryAcquireCompletion()
    {
        // Callers hold a reference, so the epoch cannot move under them; the only race is
        // against another completer, and losing that CAS means the source already completed.
        var state = Volatile.Read(ref _state);
        return (state & 1) == 0
            && Interlocked.CompareExchange(ref _state, state | 1, state) == state;
    }

    /// <summary>
    /// Cores run continuations inline (see <see cref="CompletionScheduler"/>), so failure
    /// paths — cancellation callbacks on arbitrary user threads, connection teardown — must
    /// hop to the pool themselves rather than run caller continuations where they stand.
    /// </summary>
    protected virtual void DispatchException(Exception exception)
    {
        // Completion has been claimed, and the caller still owns a reference. Capture
        // observations now: queue latency must not replace the timeout with recovered state.
        exception = PrepareException(exception);
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.Self.SetExceptionCore(state.Exception),
            (Self: this, Exception: exception),
            preferLocal: false);
    }

    protected virtual Exception PrepareException(Exception exception) => exception;

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
    internal virtual void ReleaseRef()
    {
        Debug.Assert(Volatile.Read(ref _refs) >= 1, "Only a live owner can release a response reference.");
        // References are initialized before publication and never added while rented. A
        // count of one therefore belongs exclusively to this owner; the other owners have
        // finished touching the source. Concurrent releases still use the atomic decrement.
        if (Volatile.Read(ref _refs) == 1)
        {
            _refs = 0;
        }
        else if (Interlocked.Decrement(ref _refs) != 0)
        {
            return;
        }

        ReturnToPool();
    }

    protected void ReleaseRefAtomic()
    {
        Debug.Assert(Volatile.Read(ref _refs) >= 1, "Only a live owner can release a response reference.");
        if (Interlocked.Decrement(ref _refs) == 0)
            ReturnToPool();
    }

    private void ReturnToPool()
    {
        // Clear the deadline before the epoch store publishes this source as reusable. The
        // sweep reads State before Deadline, so the release/acquire pairing on _state
        // guarantees that a sweep observing the new epoch can no longer read the previous
        // command's expired deadline and time out the next incarnation.
        Deadline = CommandDeadline.None;

        // Bump the reuse epoch and clear the completed bit in one atomic store, invalidating
        // any state the deadline sweep captured for this incarnation.
        Volatile.Write(ref _state, ((Volatile.Read(ref _state) >> 1) + 1) << 1);
        if (_mutationReference.IsRequired)
        {
            try { RetireMutationFence(); }
            finally { ResetAndReturn(); }
        }
        else ResetAndReturn();
    }

    protected abstract void SetResultCore(in RespValue result);

    protected abstract void SetExceptionCore(Exception exception);

    protected abstract void ResetAndReturn();
}

/// <summary>A discarded reply that still owns a mutation until its native FIFO slot retires.</summary>
internal sealed class MutationDiscardPendingResponse : PendingResponse
{
    private static readonly ObjectPool<MutationDiscardPendingResponse, PoolPolicy> Pool = new(4096);
    private string? _commandName;
    internal override string? CommandName => _commandName;

    internal static MutationDiscardPendingResponse Rent(string? commandName)
    {
        var source = Pool.Rent();
        source._commandName = commandName;
        source.PrepareForUse();
        return source;
    }

    // No caller observes a response or exception. In particular, do not queue a callback
    // against this source after its native owner can return it to the pool.
    protected override void DispatchException(Exception exception) { }
    protected override void SetExceptionCore(Exception exception) { }
    protected override void SetResultCore(in RespValue result) => result.Dispose();
    protected override void ResetAndReturn() => Pool.Return(this);

    private readonly struct PoolPolicy : IPooledObjectPolicy<MutationDiscardPendingResponse>
    {
        public MutationDiscardPendingResponse Create() => new();
        public bool TryReset(MutationDiscardPendingResponse source)
        {
            source._commandName = null;
            return true;
        }
    }
}

/// <summary>
/// One pooled completion for an atomic multi-command sequence. Intermediate replies are drained
/// in place; the first configured error replaces the final reply. This covers MULTI/EXEC queue
/// errors and validated command preludes without allocating one source per reply.
/// </summary>
internal sealed class MultiReplyPendingResponseSource : PendingResponse, IValueTaskSource<RespValue>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<MultiReplyPendingResponseSource, PoolPolicy> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _queueError;
    private int _replyCount;
    private int _firstQueueReply;
    private int _replyIndex;
    private int _receivedReplyIndex;
    private bool _hasQueueError;
    private string? _commandName;

    private RespireConnection? _timeoutConnection;
    private TimeSpan? _cancellationTimeout;
    private CancellationToken _callerToken;
    private CancellationToken _deadlineToken;

    internal void ConfigureTimeout(RespireConnection connection, TimeSpan? timeout,
        CancellationToken callerToken, CancellationToken deadlineToken)
    {
        _timeoutConnection = timeout.HasValue ? connection : null;
        _cancellationTimeout = timeout;
        _callerToken = callerToken;
        _deadlineToken = deadlineToken;
    }

    private MultiReplyPendingResponseSource()
    {
    }

    internal override string? CommandName => _commandName;

    // Multi-reply operations release one reference per reply. Keep their intermediate
    // releases on the original atomic path rather than adding a final-owner probe.
    internal override void ReleaseRef() => ReleaseRefAtomic();

    internal ValueTask<RespValue> Task => new(this, _core.Version);

    internal static MultiReplyPendingResponseSource Rent(
        int replyCount,
        int firstQueueReply,
        string commandName)
    {
        var source = Pool.Rent();

        source._replyCount = replyCount;
        source._firstQueueReply = firstQueueReply;
        source._commandName = commandName;
        source.PrepareForUse(replyCount);
        return source;
    }

    internal override bool TryReserveResult()
        => _receivedReplyIndex++ < _replyCount - 1 || base.TryReserveResult();

    internal override bool CompleteReservedResult(in RespValue result) => CompleteResult(in result, reserved: true);

    public override bool TrySetResult(in RespValue result) => CompleteResult(in result, reserved: false);

    private bool CompleteResult(in RespValue result, bool reserved)
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
                RespireTelemetry.RecordDiscardedError(in result, _commandName, ErrorAttempts);
                result.Dispose();
            }

            return true;
        }

        var completion = result;
        if (_hasQueueError)
        {
            completion = _queueError;
            // EXECABORT also wraps rejected EXEC commands. Only the canonical queue-abort
            // reply (or a completed EXEC array/null) proves that transaction state cleared.
            if (_commandName == "MULTI/EXEC" && (result.Type == RespDataType.Array || result.IsNull
                || result.IsError && result.AsMemory().Span.SequenceEqual(
                    "EXECABORT Transaction discarded because of previous errors."u8)))
                completion = completion.WithTransactionStateCleared();
            RespireTelemetry.RecordDiscardedError(in result, _commandName, ErrorAttempts);
            result.Dispose();
            _queueError = default;
            _hasQueueError = false;
        }

        if (!(reserved ? base.CompleteReservedResult(in completion) : base.TrySetResult(in completion)))
        {
            RespireTelemetry.RecordDiscardedError(in completion, _commandName, ErrorAttempts);
            completion.Dispose();
        }

        // This source owns intermediate disposal and final cancellation races.
        return true;
    }

    protected override void SetResultCore(in RespValue result) => _core.SetResult(result);

    protected override Exception PrepareException(Exception exception)
    {
        // Called synchronously after winning completion, before dispatching to the pool.
        // Successful transactions still return their original pooled ValueTask unchanged.
        if (_cancellationTimeout is { } timeout && exception is OperationCanceledException cancelled
            && RespireConnection.IsDeadlineCancellation(cancelled, _deadlineToken, _callerToken))
        {
            exception = new RespireTimeoutException(CommandName ?? "MULTI/EXEC", timeout, cancelled,
                _timeoutConnection!.CaptureTimeoutDiagnostics(WriteStart, WriteEnd));
        }
        return exception;
    }

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

    protected override void ResetAndReturn() => Pool.Return(this);

    private readonly struct PoolPolicy : IPooledObjectPolicy<MultiReplyPendingResponseSource>
    {
        public MultiReplyPendingResponseSource Create() => new();

        public bool TryReset(MultiReplyPendingResponseSource source)
        {
            if (source._hasQueueError)
            {
                RespireTelemetry.RecordDiscardedError(in source._queueError, source._commandName, source.ErrorAttempts);
                source._queueError.Dispose();
            }

            source._queueError = default;
            source._replyCount = 0;
            source._firstQueueReply = 0;
            source._replyIndex = 0;
            source._receivedReplyIndex = 0;
            source._hasQueueError = false;
            source._commandName = null;
            source._timeoutConnection = null;
            source._cancellationTimeout = null;
            source._callerToken = default;
            source._deadlineToken = default;
            source._core.Reset();
            return true;
        }
    }
}

/// <summary>Poolable raw RESP response source.</summary>
internal sealed class PendingResponseSource : PendingResponse, IValueTaskSource<RespValue>
{
    private ManualResetValueTaskSourceCore<RespValue> _core = new() { RunContinuationsAsynchronously = false };
    private readonly PendingResponsePool? _pool;
    private bool _throwOnError;
    private string? _commandName;

    internal PendingResponseSource()
    {
    }

    private PendingResponseSource(PendingResponsePool pool) => _pool = pool;

    public ValueTask<RespValue> Task => new(this, _core.Version);

    internal override string? CommandName => _commandName;

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
        if (_pool is { } pool)
        {
            pool.Return(this);
            return;
        }

        ResetForPool();
    }

    private void ResetForPool()
    {
        _throwOnError = false;
        _commandName = null;
        _core.Reset();
    }

    internal readonly struct PoolPolicy(PendingResponsePool pool) : IPooledObjectPolicy<PendingResponseSource>
    {
        public PendingResponseSource Create() => new(pool);

        public bool TryReset(PendingResponseSource source)
        {
            source.ResetForPool();
            return true;
        }
    }
}

/// <summary>
/// Poolable typed response source. Receive loop stores raw response and signals completion;
/// conversion and disposal occur when caller consumes result, outside receive loop.
/// </summary>
internal sealed class ConvertedPendingResponseSource<TState, TResult> : PendingResponse, IValueTaskSource<TResult>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<ConvertedPendingResponseSource<TState, TResult>, PoolPolicy> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _response;
    private ResponseConverter<TState, TResult>? _converter;
    private TState _state = default!;
    private bool _hasResponse;
    private bool _transferOwnership;
    private string? _commandName;
    private bool _observeErrors;
    private RespireTelemetry.DurationObservation _duration;

    private ConvertedPendingResponseSource()
    {
    }

    public ValueTask<TResult> Task => new(this, _core.Version);

    internal override string? CommandName => _commandName;

    public static ConvertedPendingResponseSource<TState, TResult> Rent(
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership,
        string? commandName, int errorAttempts = 0, bool observeErrors = true,
        RespireTelemetry.DurationObservation duration = default)
    {
        var source = Pool.Rent();

        source._state = state;
        source._converter = converter;
        source._transferOwnership = transferOwnership;
        source._commandName = commandName;
        source._observeErrors = observeErrors;
        source._duration = duration;
        source.PrepareForUse();
        source.ErrorAttempts = errorAttempts;
        return source;
    }

    protected override void SetResultCore(in RespValue result)
    {
        _response = result;
        _hasResponse = true;
        _duration.MarkCompleted();
        _core.SetResult(true);
    }

    protected override void SetExceptionCore(Exception exception)
    {
        _duration.MarkCompleted();
        _core.SetException(exception);
    }

    TResult IValueTaskSource<TResult>.GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
            if (_response.IsError) throw ResponseReader.ServerError(in _response, _commandName);
            // Telemetry ends at the response, before user conversion and its failures.
            _duration.Complete(_commandName);

            var result = _converter!(_state, in _response);
            if (_transferOwnership)
            {
                _hasResponse = false;
            }

            return result;
        }
        catch (Exception error)
        {
            // Successful response completion already consumes duration before conversion.
            _duration.Complete(_commandName, error);
            if (_observeErrors) RespireTelemetry.RecordError(error, internallyHandled: false, ErrorAttempts);
            throw;
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

    protected override void ResetAndReturn() => Pool.Return(this);

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
        _duration = default;
        // The receive loop still needs the operation when a canceled caller finishes first.
        // Clear it only after both references are released and the source returns to its pool.
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<ConvertedPendingResponseSource<TState, TResult>>
    {
        public ConvertedPendingResponseSource<TState, TResult> Create() => new();

        public bool TryReset(ConvertedPendingResponseSource<TState, TResult> source)
        {
            source.ClearResponse();
            source._commandName = null;
            source._core.Reset();
            return true;
        }
    }
}

/// <summary>
/// Poolable response source for commands whose public result is <c>string?</c> (GET, HGET,
/// LINDEX, ...). The receive loop completes the common case — a small, fully buffered bulk
/// string — by decoding straight from the receive buffer via <see cref="SetDirectResult"/>,
/// skipping the pooled payload rent/copy/return and <see cref="RespValue"/> lifetime that the
/// generic converter path pays. Every other reply shape (errors, simple strings, fragmented
/// or direct-filled bulks) still arrives as a <see cref="RespValue"/> and is converted when
/// the caller consumes the result, outside the receive loop.
/// </summary>
internal sealed class StringPendingResponseSource : PendingResponse, IValueTaskSource<string?>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<StringPendingResponseSource, PoolPolicy> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _response;
    private string? _directResult;
    private bool _hasResponse;
    private bool _hasDirectResult;
    private string? _commandName;
    private bool _observeErrors;
    private RespireTelemetry.DurationObservation _duration;

    private StringPendingResponseSource()
    {
    }

    public ValueTask<string?> Task => new(this, _core.Version);

    internal override string? CommandName => _commandName;

    public static StringPendingResponseSource Rent(string? commandName, int errorAttempts = 0, bool observeErrors = true,
        RespireTelemetry.DurationObservation duration = default)
    {
        var source = Pool.Rent();

        source._commandName = commandName;
        source._observeErrors = observeErrors;
        source._duration = duration;
        source.PrepareForUse();
        source.ErrorAttempts = errorAttempts;
        return source;
    }

    /// <summary>
    /// Receive loop only, and only before scheduling this source's completion. If a racing
    /// cancellation wins the completion CAS the stored string is simply dropped;
    /// <see cref="ResetAndReturn"/> clears it before the source is pooled.
    /// </summary>
    internal void SetDirectResult(string? result)
    {
        _directResult = result;
        _hasDirectResult = true;
    }

    protected override void SetResultCore(in RespValue result)
    {
        _duration.MarkCompleted();
        if (_hasDirectResult)
        {
            // Direct completions carry a default RespValue; nothing to retain.
            _core.SetResult(true);
            return;
        }

        _response = result;
        _hasResponse = true;
        _core.SetResult(true);
    }

    protected override void SetExceptionCore(Exception exception)
    {
        _duration.MarkCompleted();
        _core.SetException(exception);
    }

    string? IValueTaskSource<string?>.GetResult(short token)
    {
        try
        {
            _core.GetResult(token);
            if (!_hasDirectResult && _response.IsError)
                throw ResponseReader.ServerError(in _response, _commandName);
            _duration.Complete(_commandName);
            if (_hasDirectResult)
            {
                return _directResult;
            }

            return ResponseReader.StringOrNull(in _response);
        }
        catch (Exception error)
        {
            _duration.Complete(_commandName, error);
            if (_observeErrors) RespireTelemetry.RecordError(error, internallyHandled: false, ErrorAttempts);
            throw;
        }
        finally
        {
            Clear();
            ReleaseCallerRef();
        }
    }

    ValueTaskSourceStatus IValueTaskSource<string?>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<string?>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    protected override void ResetAndReturn() => Pool.Return(this);

    private void Clear()
    {
        if (_hasResponse)
        {
            _response.Dispose();
        }

        _response = default;
        _directResult = null;
        _hasResponse = false;
        _hasDirectResult = false;
        _commandName = null;
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<StringPendingResponseSource>
    {
        public StringPendingResponseSource Create() => new();

        public bool TryReset(StringPendingResponseSource source)
        {
            source.Clear();
            source._duration = default;
            source._core.Reset();
            return true;
        }
    }
}

/// <summary>Bounded, allocation-free pool of raw response sources.</summary>
internal sealed class PendingResponsePool
{
    private readonly ObjectPool<PendingResponseSource, PendingResponseSource.PoolPolicy> _pool;

    public PendingResponsePool(int maxPoolSize)
        => _pool = new(new PendingResponseSource.PoolPolicy(this), maxPoolSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PendingResponseSource Rent(bool throwOnError = false, string? commandName = null)
    {
        var source = _pool.Rent();
        source.Configure(throwOnError, commandName);
        source.PrepareForUse();
        return source;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Return(PendingResponseSource source) => _pool.Return(source);
}
