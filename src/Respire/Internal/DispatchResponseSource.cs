using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Reservoir;

namespace Respire.Internal;

// Dispatch routes still cross legacy route-family signatures. This view forwards only
// retry bookkeeping; the caller-facing response source retains final publication rights.
internal interface IDispatchObservation
{
    int Attempts(long generation);
    void SetAttempts(long generation, int attempts);
    void Handled(long generation, Exception error);
}

internal static class DispatchResponseSource
{
    internal static ValueTask Run<TState>(TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask> send)
        => Complete(DispatchResponseSource<bool>.Run((State: state, Send: send),
            static (state, observation) => Await(state.Send(state.State, observation))));

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private static async ValueTask<bool> Await(ValueTask response)
    {
        await response.ConfigureAwait(false);
        return true;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private static async ValueTask Complete(ValueTask<bool> response)
        => _ = await response.ConfigureAwait(false);
}

/// <summary>
/// Owns a logical dispatch before preflight and through its final response and cleanup.
/// ErrorObservation storage is acquired only when dispatch encounters a failure.
/// </summary>
internal sealed class DispatchResponseSource<TResult> : IValueTaskSource<TResult>, IDispatchObservation
{
    private static readonly ObjectPool<DispatchResponseSource<TResult>, Policy> Pool = new(4096);
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

    internal static ValueTask<TResult> Run<TState>(TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask<TResult>> send)
    {
        var source = Pool.Rent();
        lock (source._gate)
        {
            source._generation = unchecked(source._generation + 1);
            source._closed = false;
        }
        var observation = new RespireTelemetry.ErrorObservation(source, source._generation);
        ValueTask<TResult> response;
        try { response = send(state, observation); }
        catch (Exception error)
        {
            source.Finish(error);
            Pool.Return(source);
            throw;
        }
        source._response = response;
        return new(source, source._version);
    }

    int IDispatchObservation.Attempts(long generation)
    {
        lock (_gate)
            return generation == _generation && !_closed ? _owner.RetryAttempts : 0;
    }

    void IDispatchObservation.Handled(long generation, Exception error)
    {
        ErrorObservation.Borrower borrower;
        lock (_gate)
        {
            if (generation != _generation || _closed) return;
            if (_owner.IsEmpty) _owner = ErrorObservation.StartFailure();
            borrower = _owner.Borrow();
        }
        try { borrower.RecordHandled(error); }
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
        if (_response.IsCanceled) return ValueTaskSourceStatus.Canceled;
        return _response.IsFaulted ? ValueTaskSourceStatus.Faulted : ValueTaskSourceStatus.Pending;
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
