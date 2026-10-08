using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Reservoir;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

internal delegate TResult ResponseConverter<TState, TResult>(TState state, in RespValue response);

/// <summary>
/// Converts an owned RESP response into a caller-facing result without an async state machine.
/// Instances are pooled per state/result pair and recycled after the returned ValueTask is consumed.
/// </summary>
internal sealed class PooledResponseSource<TState, TResult> : IValueTaskSource<TResult>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<PooledResponseSource<TState, TResult>, PoolPolicy> Pool = new(MaxPoolSize);

    // Successful network replies can reuse the CompletionScheduler owner. Failures
    // may originate in a caller's cancellation callback and require their own dispatch.
    private ManualResetValueTaskSourceCore<TResult> _core = new() { RunContinuationsAsynchronously = false };
    private readonly Action _complete;
    private ValueTask<RespValue> _responseTask;
    private ResponseConverter<TState, TResult>? _converter;
    private TState _state = default!;
    private bool _transferOwnership;
    private RespireTelemetry.ErrorObservation _observation;

    private PooledResponseSource() => _complete = Complete;

    // Incomplete inputs must publish successful replies on an owner that can safely run
    // caller code inline, outside receive-loop continuations and locks (CompletionScheduler
    // for network replies). Faults/cancellation may originate on any owner and are dispatched.
    public static ValueTask<TResult> Create(
        ValueTask<RespValue> responseTask,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership = false,
        RespireTelemetry.ErrorObservation observation = default)
    {
        if (responseTask.IsCompletedSuccessfully)
        {
            var response = default(RespValue);
            var converted = false;
            var received = false;
            try
            {
                response = responseTask.Result;
                received = true;
                var result = converter(state, in response);
                converted = true;
                return new ValueTask<TResult>(result);
            }
            catch (Exception error)
            {
                if (!observation.IsEmpty) observation.Final(error);
                else if (received) RespireTelemetry.RecordError(error, internallyHandled: false);
                throw;
            }
            finally
            {
                if (!transferOwnership || !converted)
                {
                    response.Dispose();
                }
                observation.Dispose();
            }
        }

        var source = Pool.Rent();

        source._responseTask = responseTask;
        source._state = state;
        source._converter = converter;
        source._transferOwnership = transferOwnership;
        source._observation = observation;
        responseTask.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(source._complete);
        return new ValueTask<TResult>(source, source._core.Version);
    }

    private void Complete()
    {
        var responseTask = _responseTask;
        var state = _state;
        var converter = _converter!;
        var transferOwnership = _transferOwnership;
        var observation = _observation;

        _responseTask = default;
        _state = default!;
        _converter = null;
        _transferOwnership = false;
        _observation = default;

        var response = default(RespValue);
        var converted = false;
        var received = false;
        TResult result = default!;
        Exception? error = null;
        try
        {
            response = responseTask.GetAwaiter().GetResult();
            received = true;
            result = converter(state, in response);
            converted = true;
        }
        catch (Exception exception)
        {
            // A supplied lease owns both transport and conversion. Without a lease, the
            // send already owns its failures and only successful replies add conversion errors.
            if (observation.IsEmpty && received) RespireTelemetry.RecordError(exception, internallyHandled: false);
            error = exception;
        }
        finally
        {
            if (!transferOwnership || !converted)
            {
                response.Dispose();
            }
        }

        if (error is not null) observation.Final(error);
        observation.Dispose();

        // Publishing can run the caller inline, returning this instance to the pool and
        // renting it again. Finish cleanup first and never catch a caller's exception here.
        // Task.WaitAsync cancellation in cache/discovery wrappers can complete inline in
        // CancellationTokenSource.Cancel, independently of PendingResponse.DispatchException.
        // Set this on every completion: Reset preserves this property across pooled reuse.
        _core.RunContinuationsAsynchronously = error is not null;
        if (error is null)
        {
            _core.SetResult(result);
        }
        else
        {
            _core.SetException(error);
        }
    }

    TResult IValueTaskSource<TResult>.GetResult(short token)
    {
        try
        {
            return _core.GetResult(token);
        }
        finally
        {
            Pool.Return(this);
        }
    }

    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<TResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    private readonly struct PoolPolicy : IPooledObjectPolicy<PooledResponseSource<TState, TResult>>
    {
        public PooledResponseSource<TState, TResult> Create() => new();

        public bool TryReset(PooledResponseSource<TState, TResult> source)
        {
            source._core.Reset();
            return true;
        }
    }
}
