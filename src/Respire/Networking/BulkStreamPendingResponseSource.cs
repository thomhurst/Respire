using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Sources;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>Completion source for a GET reply whose bulk payload is consumed as a stream.</summary>
internal sealed class BulkStreamPendingResponseSource : PendingResponse, IValueTaskSource<Stream?>
{
    private ManualResetValueTaskSourceCore<Stream?> _core = new() { RunContinuationsAsynchronously = true };
    private readonly string? _commandName;
    private readonly bool _hasPrefixReply;
    private readonly Action<Exception?>? _onFrameCompleted;
    private RespBulkPayloadPipe? _payload;
    private Exception? _prefixError;
    private Exception? _completionError;
    private Exception? _payloadAbortError;
    private int _prefixReceived;
    private int _replyIndex;
    private bool _isMissing;

    internal BulkStreamPendingResponseSource(
        string? commandName, bool hasPrefixReply, Action<Exception?>? onFrameCompleted)
    {
        _commandName = commandName;
        _hasPrefixReply = hasPrefixReply;
        _onFrameCompleted = onFrameCompleted;
        PrepareForUse(hasPrefixReply ? 2 : 1);
    }

    internal override string? CommandName => _commandName;

    internal ValueTask<Stream?> Task => new(this, _core.Version);

    internal bool IsFinalReply => !_hasPrefixReply || Volatile.Read(ref _prefixReceived) != 0;

    internal bool CanStartStream => Volatile.Read(ref _prefixError) is null && !IsCompleted(State);

    internal void ObservePrefix(in RespValue result)
    {
        if (!_hasPrefixReply || Volatile.Read(ref _prefixReceived) != 0) return;
        if (result.IsError)
            Volatile.Write(ref _prefixError, ResponseReader.ServerError(in result, _commandName));
        Volatile.Write(ref _prefixReceived, 1);
    }

    internal RespBulkPayloadPipe? BeginPayload()
    {
        if (Volatile.Read(ref _payloadAbortError) is { } abortError)
        {
            TrySetException(abortError);
            return null;
        }

        if (!CanStartStream)
        {
            return null;
        }

        var payload = new RespBulkPayloadPipe();
        Volatile.Write(ref _payload, payload);
        if (Volatile.Read(ref _payloadAbortError) is { } lateAbort)
            payload.Complete(lateAbort);
        if (base.TrySetResult(default))
        {
            return payload;
        }

        Volatile.Write(ref _payload, null);
        payload.Dispose();
        return null;
    }

    internal void AbortPayload(Exception exception)
    {
        Interlocked.CompareExchange(ref _payloadAbortError, exception, null);
        Volatile.Read(ref _payload)?.Abort(Volatile.Read(ref _payloadAbortError));
    }

    internal void CompleteMissing()
    {
        if (_prefixError is { } prefixError)
        {
            TrySetException(prefixError);
            _onFrameCompleted?.Invoke(Volatile.Read(ref _completionError) ?? prefixError);
            return;
        }

        _isMissing = true;
        base.TrySetResult(default);
        _onFrameCompleted?.Invoke(Volatile.Read(ref _completionError));
    }

    internal void CompleteDiscardedPayload()
    {
        if (_prefixError is { } prefixError)
        {
            TrySetException(prefixError);
        }
    }

    public override bool TrySetResult(in RespValue result)
    {
        if (_hasPrefixReply && _replyIndex++ == 0)
        {
            if (IsCompleted(State))
            {
                result.Dispose();
                return true;
            }

            if (result.IsError)
            {
                _prefixError = ResponseReader.ServerError(in result, _commandName);
            }

            result.Dispose();
            return true;
        }

        if (_prefixError is { } prefixError)
        {
            result.Dispose();
            TrySetException(prefixError);
            return true;
        }

        return base.TrySetResult(in result);
    }

    protected override void SetResultCore(in RespValue result)
    {
        if (_payload is { } payload)
        {
            _core.SetResult(payload.ReadStream);
            return;
        }

        if (_isMissing)
        {
            _core.SetResult(null);
            return;
        }

        if (result.Type == RespDataType.Null)
        {
            result.Dispose();
            _core.SetResult(null);
            return;
        }

        if (result.IsError)
        {
            var exception = ResponseReader.ServerError(in result, _commandName);
            result.Dispose();
            _core.SetException(exception);
            return;
        }

        result.Dispose();
        _core.SetException(new RespireProtocolException(
            $"{_commandName ?? "GET"} expected a bulk string or null reply."));
    }

    protected override void SetExceptionCore(Exception exception)
        => _core.SetException(exception);

    internal void CompletePayload(Exception? exception)
    {
        _payload?.Complete(exception);
        _onFrameCompleted?.Invoke(exception ?? Volatile.Read(ref _completionError) ?? _prefixError);
    }

    protected override Exception PrepareException(Exception exception)
    {
        Interlocked.CompareExchange(ref _completionError, exception, null);
        return exception;
    }

    Stream? IValueTaskSource<Stream?>.GetResult(short token)
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

    ValueTaskSourceStatus IValueTaskSource<Stream?>.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource<Stream?>.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    protected override void ResetAndReturn()
    {
        // One-shot source. The caller and receive loop own its only references.
    }
}

/// <summary>Bounded bridge from the connection receive loop to one caller-owned stream.</summary>
internal sealed class RespBulkPayloadPipe : IDisposable
{
    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 64 * 1024,
        resumeWriterThreshold: 32 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false));
    private readonly Stream _readStream;
    private int _completed;

    internal RespBulkPayloadPipe() => _readStream = _pipe.Reader.AsStream(leaveOpen: false);

    internal Stream ReadStream => _readStream;

    internal Memory<byte> GetMemory(int sizeHint) => _pipe.Writer.GetMemory(sizeHint);

    internal void Advance(int count) => _pipe.Writer.Advance(count);

    internal ValueTask<FlushResult> FlushAsync() => _pipe.Writer.FlushAsync();

    internal void Complete(Exception? exception = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _pipe.Writer.Complete(exception);
        }
    }

    internal void Abort(Exception exception)
    {
        _pipe.Writer.CancelPendingFlush();
        Complete(exception);
    }

    public void Dispose()
    {
        Complete();
        _readStream.Dispose();
    }
}
