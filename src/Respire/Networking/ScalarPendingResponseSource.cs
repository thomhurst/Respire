using System.Threading.Tasks.Sources;
using Reservoir;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// Poolable response source for the scalar reply shapes (INCR, SET NX, HSET, EXISTS, ...).
/// Behaves exactly like <see cref="ConvertedPendingResponseSource{TState, TResult}"/> without
/// ownership transfer, but the value-type converter is a generic argument: each shape gets
/// its own specialized code and a direct conversion call instead of a delegate invocation.
/// </summary>
internal sealed class ScalarPendingResponseSource<TConverter, TResult> : PendingResponse, IValueTaskSource<TResult>
    where TConverter : struct, IScalarResponseConverter<TResult>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<ScalarPendingResponseSource<TConverter, TResult>, PoolPolicy> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _response;
    private bool _hasResponse;
    private string? _commandName;
    private bool _observeErrors;
    private RespireTelemetry.DurationObservation _duration;

    private ScalarPendingResponseSource()
    {
    }

    public ValueTask<TResult> Task => new(this, _core.Version);

    internal override string? CommandName => _commandName;

    public static ScalarPendingResponseSource<TConverter, TResult> Rent(
        string? commandName, int errorAttempts = 0, bool observeErrors = true,
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
        var observeErrors = _observeErrors;
        Exception? failure = null;
        try
        {
            try
            {
                _core.GetResult(token);
                if (_response.IsError) throw ResponseReader.ServerError(in _response, _commandName);
            }
            catch (Exception error) { _duration.Complete(_commandName, error); throw; }
            // Telemetry ends at the response, before conversion and its failures.
            _duration.Complete(_commandName);

            return TConverter.Convert(in _response);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { ClearResponse(); }
            catch (Exception error)
            {
                if (failure is null) { failure = error; throw; }
            }
            finally
            {
                // No renter lends a final owner: native admission observes errors itself, and an
                // observed send leaves them to its dispatch owner (observeErrors: false).
                CompleteCallerInspection(default, failure, observeErrors);
            }
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
        try { DisposeResponse(ref _response, ref _hasResponse); }
        finally
        {
            _response = default;
            _hasResponse = false;
            _observeErrors = false;
            _duration = default;
            // The receive loop still needs the operation when a canceled caller finishes first.
            // Clear it only after both references are released and the source returns to its pool.
        }
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<ScalarPendingResponseSource<TConverter, TResult>>
    {
        public ScalarPendingResponseSource<TConverter, TResult> Create() => new();

        public bool TryReset(ScalarPendingResponseSource<TConverter, TResult> source)
        {
            try { source.ClearResponse(); }
            finally
            {
                source._commandName = null;
                source._core.Reset();
            }
            return true;
        }
    }
}
