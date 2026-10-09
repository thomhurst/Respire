using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Reservoir;

namespace Respire.Internal;

// Dispatch routes still cross legacy route-family signatures. This view forwards only
// retry bookkeeping; the caller-facing response source retains final publication rights.
internal interface IDispatchObservation
{
    bool IsOpen(long generation);
    int Attempts(long generation);
    void SetAttempts(long generation, int attempts);
    bool Handled(long generation, Exception error);
    void Retry(long generation);
}

internal static class DispatchResponseSource
{
    internal static ValueTask Run<TState>(TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask> send)
        => Complete(DispatchResponseSource<bool>.Run((State: state, Send: send),
            static (state, observation) => Await(state.Send(state.State, observation))));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    internal static async ValueTask<bool> Await(ValueTask response)
    {
        await response.ConfigureAwait(false);
        return true;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    internal static async ValueTask Complete(ValueTask<bool> response)
        => _ = await response.ConfigureAwait(false);
}

/// <summary>
/// Owns a logical dispatch before preflight and through its final response and cleanup.
/// ErrorObservation storage is acquired only when dispatch encounters a failure.
/// </summary>
internal sealed class DispatchResponseSource<TResult> : IValueTaskSource<TResult>, IDispatchObservation
{
    // Retained caller responses can outlive native completion across a 50 x 200 burst.
    internal const int MaxPoolSize = 10000;
    private static readonly ObjectPool<DispatchResponseSource<TResult>, Policy> Pool = new(MaxPoolSize);
    private readonly Lock _gate = new();
    private readonly Action _continue;
    private Action<object?>? _continuation;
    private object? _continuationState;
    private ValueTask<TResult> _response;
    private ErrorObservation.FinalOwner _owner;
    private long _generation;
    private short _version;
    private bool _closed;

    private DispatchResponseSource() => _continue = InvokeContinuation;

    // Facet entries can contain spans, so begin ownership without capturing their
    // arguments in a delegate. Construction finishes synchronously before Attach.
    internal static DispatchResponseSource<TResult> Start()
    {
        var source = Pool.Rent();
        lock (source._gate)
        {
            source._generation = unchecked(source._generation + 1);
            source._closed = false;
        }
        return source;
    }

    internal RespireTelemetry.ErrorObservation Observation => new(this, _generation);

    internal ValueTask<TResult> Attach(ValueTask<TResult> response)
    {
        _response = response;
        return new(this, _version);
    }

    internal void Fail(Exception error)
    {
        Finish(error);
        Pool.Return(this);
    }

    // Detached recovery has no caller-facing failure. Its handled observations are
    // complete only after the attempt's borrowers and cleanup have finished.
    internal void CompleteInternal()
    {
        Finish(null);
        Pool.Return(this);
    }

    internal static ValueTask<TResult> Run<TState>(TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask<TResult>> send)
    {
        var source = Start();
        var observation = source.Observation;
        ValueTask<TResult> response;
        try { response = send(state, observation); }
        catch (Exception error)
        {
            source.Fail(error);
            throw;
        }
        // Borrowers can run during send, but the caller cannot access this response until
        // Run returns. Publish the native response before exposing its ValueTask.
        return source.Attach(response);
    }

    int IDispatchObservation.Attempts(long generation)
    {
        lock (_gate)
            return generation == _generation && !_closed ? _owner.RetryAttempts : 0;
    }

    bool IDispatchObservation.IsOpen(long generation)
    {
        lock (_gate) return generation == _generation && !_closed;
    }

    bool IDispatchObservation.Handled(long generation, Exception error)
    {
        ErrorObservation.Borrower borrower;
        lock (_gate)
        {
            if (generation != _generation || _closed) return false;
            if (_owner.IsEmpty) _owner = ErrorObservation.StartFailure();
            borrower = _owner.Borrow();
        }
        try { return borrower.RecordHandled(error); }
        finally { borrower.Complete(); }
    }

    void IDispatchObservation.SetAttempts(long generation, int attempts)
    {
        lock (_gate)
        {
            if (generation != _generation || _closed || (attempts <= 0 && _owner.IsEmpty)) return;
            if (_owner.IsEmpty) _owner = ErrorObservation.StartFailure(attempts);
            else _owner.SetRetryAttempts(attempts);
        }
    }

    void IDispatchObservation.Retry(long generation)
    {
        lock (_gate)
        {
            if (generation != _generation || _closed) return;
            if (_owner.IsEmpty) _owner = ErrorObservation.StartFailure();
            _owner.RecordRetry();
        }
    }

    private void Finish(Exception? error)
    {
        ErrorObservation.FinalOwner owner;
        lock (_gate)
        {
            _closed = true;
            owner = _owner;
            _owner = default;
        }
        ErrorObservation.FinishFinal(owner, error);
    }

    private void InvokeContinuation()
    {
        var continuation = _continuation!;
        var state = _continuationState;
        _continuation = null;
        _continuationState = null;
        // The caller can consume and recycle this source inline. Access no fields afterwards.
        continuation(state);
    }

    TResult IValueTaskSource<TResult>.GetResult(short token)
    {
        ValidateToken(token);
        Exception? failure = null;
        try { return _response.GetAwaiter().GetResult(); }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            Finish(failure);
            Pool.Return(this);
        }
    }
    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token)
    {
        ValidateToken(token);
        if (_response.IsCompletedSuccessfully) return ValueTaskSourceStatus.Succeeded;
        // Pending replies need no cancellation/fault probes. Completion can race the first check.
        if (!_response.IsCompleted) return ValueTaskSourceStatus.Pending;
        if (_response.IsCanceled) return ValueTaskSourceStatus.Canceled;
        return _response.IsFaulted ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Succeeded;
    }
    void IValueTaskSource<TResult>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags)
    {
        ValidateToken(token);
        System.Diagnostics.Debug.Assert(_continuation is null);
        _continuation = continuation;
        _continuationState = state;
        var awaiter = _response.ConfigureAwait((flags & ValueTaskSourceOnCompletedFlags.UseSchedulingContext) != 0).GetAwaiter();
        if ((flags & ValueTaskSourceOnCompletedFlags.FlowExecutionContext) != 0) awaiter.OnCompleted(_continue);
        else awaiter.UnsafeOnCompleted(_continue);
    }

    private void ValidateToken(short token)
    {
        // Once rented again, the 16-bit ValueTask version is the stale awaiter's
        // only guard. ValueTask's single-consumption contract still applies at wraparound.
        if (token != _version || _closed)
            throw new InvalidOperationException("The dispatch response has already been consumed.");
    }

    private readonly struct Policy : IPooledObjectPolicy<DispatchResponseSource<TResult>>
    {
        public DispatchResponseSource<TResult> Create() => new();
        public bool TryReset(DispatchResponseSource<TResult> source)
        {
            source._version = unchecked((short)(source._version + 1));
            source._response = default;
            return true;
        }
    }
}
