using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
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
    private static readonly LockFreeStack<PooledResponseSource<TState, TResult>> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<TResult> _core = new() { RunContinuationsAsynchronously = true };
    private readonly Action _complete;
    private ValueTask<RespValue> _responseTask;
    private ResponseConverter<TState, TResult>? _converter;
    private TState _state = default!;
    private bool _transferOwnership;

    private PooledResponseSource() => _complete = Complete;

    public static ValueTask<TResult> Create(
        ValueTask<RespValue> responseTask,
        TState state,
        ResponseConverter<TState, TResult> converter,
        bool transferOwnership = false)
    {
        if (responseTask.IsCompletedSuccessfully)
        {
            var response = responseTask.Result;
            var converted = false;
            try
            {
                var result = converter(state, in response);
                converted = true;
                return new ValueTask<TResult>(result);
            }
            finally
            {
                if (!transferOwnership || !converted)
                {
                    response.Dispose();
                }
            }
        }

        if (!Pool.TryPop(out var source))
        {
            source = new PooledResponseSource<TState, TResult>();
        }

        source._responseTask = responseTask;
        source._state = state;
        source._converter = converter;
        source._transferOwnership = transferOwnership;
        responseTask.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(source._complete);
        return new ValueTask<TResult>(source, source._core.Version);
    }

    private void Complete()
    {
        var responseTask = _responseTask;
        var state = _state;
        var converter = _converter!;
        var transferOwnership = _transferOwnership;

        _responseTask = default;
        _state = default!;
        _converter = null;
        _transferOwnership = false;

        var response = default(RespValue);
        var converted = false;
        try
        {
            response = responseTask.GetAwaiter().GetResult();
            var result = converter(state, in response);
            converted = true;
            _core.SetResult(result);
        }
        catch (Exception exception)
        {
            _core.SetException(exception);
        }
        finally
        {
            if (!transferOwnership || !converted)
            {
                response.Dispose();
            }
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
            _core.Reset();
            Pool.TryPush(this);
        }
    }

    ValueTaskSourceStatus IValueTaskSource<TResult>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<TResult>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}
