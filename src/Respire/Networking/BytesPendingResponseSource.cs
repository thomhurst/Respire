using System.Threading.Tasks.Sources;
using Reservoir;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>Owns the final byte array for direct bulk replies; other shapes retain normal conversion.</summary>
internal sealed class BytesPendingResponseSource : PendingResponse, IValueTaskSource<byte[]?>
{
    private const int MaxPoolSize = 4096;
    private static readonly ObjectPool<BytesPendingResponseSource, PoolPolicy> Pool = new(MaxPoolSize);

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private RespValue _response;
    private byte[]? _directResult;
    private bool _hasResponse;
    private bool _hasDirectResult;
    private string? _commandName;
    private bool _observeErrors;
    private ErrorObservation.FinalOwner _observation;
    private RespireTelemetry.DurationObservation _duration;

    private BytesPendingResponseSource()
    {
    }

    public ValueTask<byte[]?> Task => new(this, _core.Version);

    internal override string? CommandName => _commandName;

    public static BytesPendingResponseSource Rent(string? commandName, int errorAttempts = 0, bool observeErrors = true,
        RespireTelemetry.DurationObservation duration = default, ErrorObservation.FinalOwner observation = default)
    {
        var source = Pool.Rent();

        source._commandName = commandName;
        source._observeErrors = observeErrors;
        source._observation = observation;
        source._duration = duration;
        source.PrepareForUse();
        source.ErrorAttempts = errorAttempts;
        return source;
    }

    /// <summary>
    /// Receive loop only, and only before scheduling this source's completion. If a racing
    /// cancellation wins the completion CAS the stored array is simply dropped;
    /// <see cref="ResetAndReturn"/> clears it before the source is pooled.
    /// </summary>
    internal void SetDirectResult(byte[]? result)
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

    byte[]? IValueTaskSource<byte[]?>.GetResult(short token)
    {
        var observation = _observation;
        var observeErrors = _observeErrors;
        Exception? failure = null;
        try
        {
            try
            {
                _core.GetResult(token);
                if (!_hasDirectResult && _response.IsError)
                    throw ResponseReader.ServerError(in _response, _commandName);
            }
            catch (Exception error) { _duration.Complete(_commandName, error); throw; }
            _duration.Complete(_commandName);
            if (_hasDirectResult)
            {
                return _directResult;
            }

            return ResponseReader.BytesOrNull(in _response);
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try { Clear(); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                CompleteCallerInspection(observation, failure, observeErrors);
            }
        }
    }

    ValueTaskSourceStatus IValueTaskSource<byte[]?>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<byte[]?>.OnCompleted(
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
        _observeErrors = false;
        _observation = default;
        // Keep the operation until the receive reference releases a canceled reply.
        _duration = default;
    }

    private readonly struct PoolPolicy : IPooledObjectPolicy<BytesPendingResponseSource>
    {
        public BytesPendingResponseSource Create() => new();

        public bool TryReset(BytesPendingResponseSource source)
        {
            source.Clear();
            source._commandName = null;
            source._core.Reset();
            return true;
        }
    }
}
