using System.Diagnostics;
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
    private readonly Action? _onLifetimeCancelled;
    private readonly CancellationToken _streamCancellationToken;
    private CancellationTokenRegistration _streamCancellationRegistration;
    // Threading: the receive loop owns _replyIndex and _isMissing and publishes _prefixError,
    // _prefixReceived and _payload; deferred completion and Abort can observe them from other
    // threads, so those three (and the abort/completion errors) always use Volatile/Interlocked.
    private RespBulkPayloadPipe? _payload;
    private Exception? _prefixError;
    private Exception? _completionError;
    private Exception? _payloadAbortError;
    private int _prefixReceived;
    private int _replyIndex;
    private int _receivedReplyIndex;
    private bool _isMissing;

    internal BulkStreamPendingResponseSource(
        string? commandName, bool hasPrefixReply, Action<Exception?>? onFrameCompleted,
        CancellationToken streamCancellationToken = default, Action? onLifetimeCancelled = null)
    {
        _commandName = commandName;
        _hasPrefixReply = hasPrefixReply;
        _onFrameCompleted = onFrameCompleted;
        _onLifetimeCancelled = onLifetimeCancelled;
        _streamCancellationToken = streamCancellationToken;
        PrepareForUse(hasPrefixReply ? 2 : 1);
        if (streamCancellationToken.CanBeCanceled)
            _streamCancellationRegistration = streamCancellationToken.UnsafeRegister(
                static (state, token) =>
                {
                    var source = (BulkStreamPendingResponseSource)state!;
                    var error = new OperationCanceledException(token);
                    source.AbortPayload(error);
                    source._onLifetimeCancelled?.Invoke();
                }, this);
    }

    internal override string? CommandName => _commandName;
    internal bool HasPrefixReply => _hasPrefixReply;
    internal Action<Exception?>? OnFrameCompleted => _onFrameCompleted;
    internal CancellationToken StreamCancellationToken => _streamCancellationToken;

    internal ValueTask<Stream?> Task => new(this, _core.Version);

    internal bool IsFinalReply => !_hasPrefixReply || Volatile.Read(ref _prefixReceived) != 0;

    internal bool HasStalledReader(TimeSpan idle)
        => Volatile.Read(ref _payload)?.HasStalledReader(idle) == true;

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
        Volatile.Read(ref _payload)?.CancelPendingOperations();
    }

    internal bool IsPayloadAborted => Volatile.Read(ref _payloadAbortError) is not null;

    internal void CompleteMissing()
    {
        DisposeStreamCancellationRegistration();
        if (Volatile.Read(ref _prefixError) is { } prefixError)
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
        DisposeStreamCancellationRegistration();
        if (Volatile.Read(ref _prefixError) is { } prefixError)
        {
            TrySetException(prefixError);
        }
    }

    internal override bool TryReserveResult()
        => _hasPrefixReply && _receivedReplyIndex++ == 0 || base.TryReserveResult();

    internal override bool CompleteReservedResult(in RespValue result) => CompleteResult(in result, reserved: true);

    public override bool TrySetResult(in RespValue result) => CompleteResult(in result, reserved: false);

    private bool CompleteResult(in RespValue result, bool reserved)
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
                Volatile.Write(ref _prefixError, ResponseReader.ServerError(in result, _commandName));
            }

            result.Dispose();
            return true;
        }

        if (Volatile.Read(ref _prefixError) is { } prefixError)
        {
            RespireTelemetry.RecordDiscardedError(in result, _commandName, ErrorAttempts);
            result.Dispose();
            if (reserved) SetExceptionCore(PrepareException(prefixError));
            else TrySetException(prefixError);
            return true;
        }

        return reserved ? base.CompleteReservedResult(in result) : base.TrySetResult(in result);
    }

    protected override void SetResultCore(in RespValue result)
    {
        if (Volatile.Read(ref _payload) is { } payload)
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
    {
        DisposeStreamCancellationRegistration();
        _core.SetException(exception);
    }

    internal void CompletePayload(Exception? exception)
    {
        // The abort reason explains why the socket stopped. A subsequent read may surface
        // SocketException or cancellation from that same abort; do not let it mask the cause.
        var completionError = Volatile.Read(ref _payloadAbortError) ?? exception;
        Volatile.Read(ref _payload)?.Complete(completionError);
        DisposeStreamCancellationRegistration();
        _onFrameCompleted?.Invoke(
            completionError ?? Volatile.Read(ref _completionError) ?? Volatile.Read(ref _prefixError));
    }

    protected override Exception PrepareException(Exception exception)
    {
        Interlocked.CompareExchange(ref _completionError, exception, null);
        return exception;
    }

    private void DisposeStreamCancellationRegistration()
    {
        _streamCancellationRegistration.Dispose();
        _streamCancellationRegistration = default;
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
        DisposeStreamCancellationRegistration();
        // Prefix errors retained before cancellation need an owner after both replies drain.
        if (_prefixError is { } prefixError && _completionError is { } completionError
            && !ReferenceEquals(prefixError, completionError))
            RespireTelemetry.RecordError(prefixError, internallyHandled: true, ErrorAttempts);
        // One-shot source. The caller and receive loop own its only references.
    }
}

/// <summary>Bounded bridge from the connection receive loop to one caller-owned stream.</summary>
internal sealed class RespBulkPayloadPipe : IDisposable
{
    private readonly Lock _flushGate = new();
    private readonly Pipe _pipe = new(new PipeOptions(
        pauseWriterThreshold: 64 * 1024,
        resumeWriterThreshold: 32 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false));
    private readonly Stream _readStream;
    private int _errorAttempts;
    private int _reportedReadError;
    private int _completed;
    private bool _flushCancelled;
    private long _lastReaderProgress = Stopwatch.GetTimestamp();

    internal RespBulkPayloadPipe()
        => _readStream = new ProgressTrackingStream(_pipe.Reader.AsStream(leaveOpen: false), this);

    internal Stream ReadStream => _readStream;

    internal static void SetErrorAttempts(Stream? stream, int attempts)
    {
        if (stream is ProgressTrackingStream tracked) tracked.SetErrorAttempts(attempts);
    }

    private void RecordReadError(Exception error, CancellationToken readCancellation = default)
    {
        // Invalid Stream API usage does not establish a failed Redis payload. A failed
        // payload can throw on every later read; report its caller boundary only once.
        if (error is ArgumentException or ObjectDisposedException or NotSupportedException) return;
        // Canceling one read does not terminate the payload; a later read may succeed or fail.
        if (error is OperationCanceledException cancelled && readCancellation.IsCancellationRequested
            && cancelled.CancellationToken == readCancellation)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false, _errorAttempts);
            return;
        }
        if (Interlocked.Exchange(ref _reportedReadError, 1) == 0)
            RespireTelemetry.RecordError(error, internallyHandled: false, _errorAttempts);
    }

    internal Memory<byte> GetMemory(int sizeHint) => _pipe.Writer.GetMemory(sizeHint);

    internal void Advance(int count) => _pipe.Writer.Advance(count);

    internal ValueTask<FlushResult> FlushAsync()
    {
        lock (_flushGate)
        {
            return _flushCancelled
                ? ValueTask.FromResult(new FlushResult(isCanceled: true, isCompleted: false))
                : _pipe.Writer.FlushAsync();
        }
    }

    internal void Complete(Exception? exception = null)
    {
        lock (_flushGate)
        {
            if (_completed != 0) return;
            _completed = 1;
            _pipe.Writer.Complete(exception);
        }
    }

    internal void CancelPendingOperations()
    {
        lock (_flushGate)
        {
            if (_completed != 0) return;
            _flushCancelled = true;
            _pipe.Writer.CancelPendingFlush();
            _pipe.Reader.CancelPendingRead();
        }
    }

    internal bool HasStalledReader(TimeSpan idle)
        => Stopwatch.GetElapsedTime(Volatile.Read(ref _lastReaderProgress)) >= idle;

    private void MarkReaderProgress() => Volatile.Write(ref _lastReaderProgress, Stopwatch.GetTimestamp());

    public void Dispose()
    {
        Complete();
        _readStream.Dispose();
    }

    private sealed class ProgressTrackingStream(Stream inner, RespBulkPayloadPipe owner) : Stream
    {
        internal void SetErrorAttempts(int attempts) => owner._errorAttempts = attempts;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => inner.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return RecordProgress(inner.Read(buffer, offset, count)); }
            catch (Exception error) { owner.RecordReadError(error); throw; }
        }
        public override int Read(Span<byte> buffer)
        {
            try { return RecordProgress(inner.Read(buffer)); }
            catch (Exception error) { owner.RecordReadError(error); throw; }
        }
        public override int ReadByte()
        {
            try
            {
                var value = inner.ReadByte();
                if (value >= 0) owner.MarkReaderProgress();
                return value;
            }
            catch (Exception error) { owner.RecordReadError(error); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            try { return RecordProgressAsync(inner.ReadAsync(buffer, offset, count, cancellationToken), cancellationToken); }
            catch (Exception error) { owner.RecordReadError(error, cancellationToken); throw; }
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { return RecordProgressAsync(inner.ReadAsync(buffer, cancellationToken), cancellationToken); }
            catch (Exception error) { owner.RecordReadError(error, cancellationToken); throw; }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        private int RecordProgress(int read)
        {
            if (read > 0) owner.MarkReaderProgress();
            return read;
        }

        private async Task<int> RecordProgressAsync(Task<int> read, CancellationToken cancellationToken)
        {
            try { return RecordProgress(await read.ConfigureAwait(false)); }
            catch (Exception error) { owner.RecordReadError(error, cancellationToken); throw; }
        }

        private async ValueTask<int> RecordProgressAsync(ValueTask<int> read, CancellationToken cancellationToken)
        {
            try { return RecordProgress(await read.ConfigureAwait(false)); }
            catch (Exception error) { owner.RecordReadError(error, cancellationToken); throw; }
        }
    }
}
