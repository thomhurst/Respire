using System.Buffers;
using System.Diagnostics;
using Respire.Commands;
using Respire.Protocol;

namespace Respire.Networking;

// Streamed command write path (#645). A streamed SET owns the connection's write path for its
// whole RESP frame: the header, the payload in bounded chunks, then the terminator and options.
// Ordinary producers back off while _streamingActive is set (TryEnqueue, TryEnqueueDirect and the
// retirement drain all check it), and any failure after the header is queued but before the
// frame reaches the socket aborts the connection, so no other bytes can follow a partial frame.
internal sealed partial class RespireConnection
{
    /// <summary>
    /// Payload bytes copied into the write buffer per flush. Bounds write-buffer growth for any
    /// payload size; the docs describe uploads as being sent in chunks of this size.
    /// </summary>
    internal const int StreamChunkSize = 32 * 1024;

    // System.Threading.Timer accepts at most about 49.7 days. CommandTimeout has no upper bound,
    // so a longer deadline re-arms the timer in slices instead of failing to schedule.
    private const long StreamTimeoutTimerSliceMilliseconds = 30L * 24 * 60 * 60 * 1000;

    private enum StreamedSetPhase
    {
        /// <summary>Nothing written: the request and the caller's source are untouched and retryable.</summary>
        NotStarted,

        /// <summary>The frame is open on the wire; a failure must abort the connection.</summary>
        HeaderQueued,

        /// <summary>The terminator is queued and the reply published, but the frame is not yet written.</summary>
        ResponseQueued,

        /// <summary>The complete frame is on the socket; cancellation only abandons the reply wait.</summary>
        FrameWritten,
    }

    /// <summary>
    /// Cancels a streamed upload at its command deadline. The connection's deadline sweep only
    /// inspects commands published to <c>_inflight</c>, and a streamed SET is not published until
    /// its frame is queued (so a reply can never be matched to a partial frame), so each upload
    /// needs its own timer. It follows maintenance windows the same way the sweep does.
    /// </summary>
    /// <remarks>
    /// Lock order: <c>_scheduleGate</c>, then the connection's <c>_maintenancePublicationGate</c>.
    /// Maintenance publication takes only the publication gate and raises
    /// <c>MaintenanceStateChanged</c> after releasing it, so <see cref="Recheck"/> never takes the
    /// two gates in the opposite order.
    /// </remarks>
    private sealed class StreamDeadlineCancellation : IDisposable
    {
        private readonly RespireConnection _connection;
        private readonly long _deadline;
        private readonly CancellationTokenSource _source = new();
        private readonly Timer _timer;
        private readonly Action? _maintenanceChanged;
        private readonly Lock _scheduleGate = new();
        // Bumped by every maintenance change; a Schedule that read an older value recomputes
        // instead of installing (or acting on) a stale deadline.
        private int _version;
        private int _disposed;
        private long _effectiveTimeoutTicks;
        private long _committedTimeoutTicks = long.MinValue;

        internal StreamDeadlineCancellation(RespireConnection connection, long deadline)
        {
            _connection = connection;
            _deadline = deadline;
            _effectiveTimeoutTicks = connection._commandTimeout!.Value.Ticks;
            _timer = new Timer(static state => ((StreamDeadlineCancellation)state!).Schedule(),
                this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (connection._maintenanceOptions is not null) _maintenanceChanged = Recheck;
        }

        internal CancellationToken Token => _source.Token;

        /// <summary>The timeout that applied when the deadline fired, including any maintenance relaxation.</summary>
        internal TimeSpan EffectiveTimeout
        {
            get
            {
                var committedTicks = Interlocked.Read(ref _committedTimeoutTicks);
                return committedTicks == long.MinValue
                    ? TimeSpan.FromTicks(Interlocked.Read(ref _effectiveTimeoutTicks))
                    : TimeSpan.FromTicks(committedTicks);
            }
        }

        internal void Start()
        {
            // A maintenance start or completion changes the effective deadline immediately.
            if (_maintenanceChanged is not null) _connection.MaintenanceStateChanged += _maintenanceChanged;
            Schedule();
        }

        // Runs on the receive loop: never cancel inline (that would run caller continuations
        // there); fire the timer so Schedule recomputes the deadline on a pool thread.
        private void Recheck()
        {
            lock (_scheduleGate)
            {
                _version++;
                try { _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }
            }
        }

        private void Schedule()
        {
            while (true)
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                var version = Volatile.Read(ref _version);
                Task? cancellationCallbacks = null;
                lock (_scheduleGate)
                {
                    // A maintenance change raced this calculation; the newest state must win.
                    if (version != _version) continue;
                    lock (_connection._maintenancePublicationGate)
                    {
                        var delay = ComputeDelay(out var effectiveTimeout);
                        Interlocked.Exchange(ref _effectiveTimeoutTicks, effectiveTimeout.Ticks);
                        if (delay is { } next)
                        {
                            try { _timer.Change(next, Timeout.InfiniteTimeSpan); }
                            catch (ObjectDisposedException) { }
                            return;
                        }

                        // Commit cancellation while maintenance-state publication is excluded.
                        // CancelAsync only marks the token here; callbacks run asynchronously.
                        Interlocked.Exchange(ref _committedTimeoutTicks, effectiveTimeout.Ticks);
                        try { cancellationCallbacks = _source.CancelAsync(); }
                        catch (ObjectDisposedException) { }
                    }
                }

                ObserveCancellationCallbacks(cancellationCallbacks);
                return;
            }
        }

        // Null once the effective deadline has passed.
        private TimeSpan? ComputeDelay(out TimeSpan effectiveTimeout)
        {
            effectiveTimeout = _connection._commandTimeout!.Value;
            var now = Environment.TickCount64;
            var remaining = _deadline - now;
            long window = 0;
            if (_connection._maintenanceOptions is not null && _connection._commandTimeout is { } normal)
            {
                // Like the capacity wait and deadline sweep, an active maintenance window relaxes
                // the deadline measured from the command's original start; its end restores it.
                effectiveTimeout = _connection.MaintenanceTimeout(normal, now, out window, out _, _deadline);
                remaining += (long)(effectiveTimeout - normal).TotalMilliseconds;
            }
            if (remaining <= 0) return null;

            var sleep = Math.Min(remaining, StreamTimeoutTimerSliceMilliseconds);
            // Recheck when the window closes so a restored, shorter deadline is enforced.
            if (window > 0) sleep = Math.Min(sleep, window);
            return TimeSpan.FromMilliseconds(sleep);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (_maintenanceChanged is not null) _connection.MaintenanceStateChanged -= _maintenanceChanged;
            _timer.Dispose();
            _source.Dispose();
        }
    }

    // Cancellation callbacks registered by a caller's stream are user code; they may throw or
    // block. CancelAsync runs them off this thread, and any fault is observed here.
    private static void ObserveCancellationCallbacks(Task? callbacks)
    {
        if (callbacks is { IsFaulted: true }) _ = callbacks.Exception;
        else if (callbacks is not null)
            _ = callbacks.ContinueWith(static completed => _ = completed.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private ValueTask<RespValue> SendStreamingAsync<TCommand>(
        in TCommand command, CancellationToken cancellationToken, bool armCommandDeadline)
        where TCommand : struct, IRespCommand
        => command is StreamedSetCommand streamedSet
            ? SendStreamedSetAsync(streamedSet, cancellationToken, armCommandDeadline)
            : throw new NotSupportedException(
                $"Streaming command {typeof(TCommand).Name} has no connection write path.");

    private async ValueTask<RespValue> SendStreamedSetAsync(
        StreamedSetCommand command, CancellationToken cancellationToken, bool armCommandDeadline)
    {
        var deadline = armCommandDeadline && _commandTimeoutMilliseconds != 0
            ? Environment.TickCount64 + _commandTimeoutMilliseconds
            : 0;
        using var timeoutCancellation = deadline == 0 ? null : new StreamDeadlineCancellation(this, deadline);
        timeoutCancellation?.Start();
        // Streamed SETs are rare and large, so one linked source per call is cheap. It observes the
        // caller, the command deadline and a connection abort; the reply is not yet published to
        // _inflight while the frame is written, so nothing else could complete this call.
        using var linkedCancellation = timeoutCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closedCancellation.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCancellation.Token, _closedCancellation.Token);
        var effectiveCancellation = linkedCancellation.Token;
        try
        {
            await WaitForStreamingGateAsync(effectiveCancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (TranslateStreamedSetCancellation(
            error, effectiveCancellation, cancellationToken, timeoutCancellation, StreamedSetPhase.NotStarted) is { } translated)
        {
            // A deadline here means another streamed SET held the frame for the whole timeout.
            throw translated;
        }

        PendingResponseSource source;
        try
        {
            source = _sourcePool.Rent(throwOnError: true, commandName: "SET");
        }
        catch
        {
            _streamingGate.Release();
            throw;
        }

        var phase = StreamedSetPhase.NotStarted;
        var ownsWritePath = false;
        try
        {
            // Respect the credential-renewal fence like ordinary commands: AUTH must be admitted
            // and acknowledged before a (potentially long) upload takes the wire.
            await WaitForStreamingAdmissionAsync(static connection =>
            {
                if (connection._credentialRenewalPending) return false;
                connection._streamingActive = true;
                return true;
            }, effectiveCancellation).ConfigureAwait(false);
            ownsWritePath = true;

            await DrainBufferedWritesAsync(effectiveCancellation).ConfigureAwait(false);
            await WaitForStreamingAdmissionAsync(
                static connection => connection._inflight.Capacity - connection._inflight.Count > 0,
                effectiveCancellation).ConfigureAwait(false);
            source.Deadline = deadline;

            // AppendStreamingStart rejects a retired or closed connection before writing any
            // bytes, so the request (and the caller's stream) stays untouched and retryable.
            var write = AppendStreamingStart(command, out var startedBatch, out var requestWriteStart);
            phase = StreamedSetPhase.HeaderQueued;
            ScheduleFlush(startedBatch);
            // A peer that stops reading stalls the socket write; bound every wait by the caller,
            // the deadline and connection abort so the failure path can close the partial frame.
            await write.WaitAsync(effectiveCancellation).ConfigureAwait(false);

            await WriteStreamedPayloadAsync(command, effectiveCancellation).ConfigureAwait(false);

            var finalWrite = AppendStreamingEnd(command, source, requestWriteStart, out startedBatch);
            phase = StreamedSetPhase.ResponseQueued;
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            await finalWrite.WaitAsync(effectiveCancellation).ConfigureAwait(false);
            phase = StreamedSetPhase.FrameWritten;
        }
        catch (Exception error)
        {
            // One failure path for every phase: each exception type only decides what the caller
            // sees, while the abort-versus-reclaim decision depends on the phase alone.
            var translated = error is OperationCanceledException canceled
                ? TranslateStreamedSetCancellation(
                    canceled, effectiveCancellation, cancellationToken, timeoutCancellation, phase)
                : null;
            await FailStreamedSetAsync(source, phase, error, translated is RespireTimeoutException)
                .ConfigureAwait(false);
            if (translated is null) throw;
            throw translated;
        }
        finally
        {
            if (ownsWritePath)
            {
                lock (_writeGate) _streamingActive = false;
                _capacitySignal.Signal();
            }
            _streamingGate.Release();
        }

        return await source.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Maps a cancellation of the linked token to what the caller should see: a closed connection,
    /// the command timeout, or the caller's own token. Returns <see langword="null"/> when the
    /// cancellation came from elsewhere and should propagate unchanged.
    /// </summary>
    private Exception? TranslateStreamedSetCancellation(
        OperationCanceledException error,
        CancellationToken effectiveCancellation,
        CancellationToken callerToken,
        StreamDeadlineCancellation? timeoutCancellation,
        StreamedSetPhase phase)
    {
        if (IsClosedCancellation(error, effectiveCancellation, callerToken))
            return ClosedDuringStreamedSet(error);
        if (timeoutCancellation is not null && IsDeadlineCancellation(error, effectiveCancellation, callerToken))
        {
            return new RespireTimeoutException("SET", timeoutCancellation.EffectiveTimeout, error,
                CaptureTimeoutDiagnostics(stage: phase == StreamedSetPhase.NotStarted
                    ? RespireCommandStage.WaitingForCapacity
                    : RespireCommandStage.Writing));
        }

        return callerToken.IsCancellationRequested
            ? new OperationCanceledException(error.Message, error, callerToken)
            : null;
    }

    private async ValueTask FailStreamedSetAsync(
        PendingResponseSource source, StreamedSetPhase phase, Exception error, bool timedOut)
    {
        if (phase is StreamedSetPhase.HeaderQueued or StreamedSetPhase.ResponseQueued)
        {
            // Abort is a no-op when the connection is already dead.
            Abort(new RespireConnectionException(timedOut
                ? $"Streamed SET on {Host}:{Port} timed out before its RESP frame completed."
                : $"Streamed SET on {Host}:{Port} did not complete; connection was closed to preserve RESP framing.",
                error));
        }

        if (phase < StreamedSetPhase.ResponseQueued) ReclaimUnpublished(source);
        else await ObserveStreamedSetResponseAsync(source).ConfigureAwait(false);
    }

    private async ValueTask WaitForStreamingGateAsync(CancellationToken cancellationToken)
    {
        using var gateCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gateWait = _streamingGate.WaitAsync(gateCancellation.Token);
        if (await Task.WhenAny(gateWait, _retiredSignal.Task).ConfigureAwait(false) == _retiredSignal.Task)
        {
            gateCancellation.Cancel();
            try
            {
                await gateWait.ConfigureAwait(false);
                _streamingGate.Release();
            }
            catch (OperationCanceledException error)
            {
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(error.Message, error, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfRetired();
        }

        try { await gateWait.ConfigureAwait(false); }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(error.Message, error, cancellationToken);
        }
    }

    /// <summary>
    /// Waits until <paramref name="admit"/> succeeds under <c>_writeGate</c>. The condition is
    /// checked before any waiter exists, so the common uncontended case registers nothing. On a
    /// failed check the waiter is created and the condition re-checked before parking: the
    /// capacity signal is a broadcast generation pulse, so a pulse between the two checks
    /// completes that waiter instead of being lost.
    /// </summary>
    private async ValueTask WaitForStreamingAdmissionAsync(
        Func<RespireConnection, bool> admit, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryAdmitStreaming(admit)) return;
            var signaled = _capacitySignal.WaitAsync(cancellationToken);
            // Rarely abandoned when this re-check passes; it completes at the next shared pulse
            // and never consumes a wakeup meant for another producer.
            if (TryAdmitStreaming(admit)) return;
            await signaled.ConfigureAwait(false);
        }
    }

    private bool TryAdmitStreaming(Func<RespireConnection, bool> admit)
    {
        lock (_writeGate)
        {
            ThrowIfStreamingUnavailable(rejectRetired: true);
            return admit(this);
        }
    }

    // Before the header is queued a retired connection rejects the command so it can be retried
    // on the replacement. After that the command was accepted, and retirement drains it instead.
    private void ThrowIfStreamingUnavailable(bool rejectRetired)
    {
        if (rejectRetired) ThrowIfRetired();
        if (_dead) throw new RespireConnectionException($"Connection to {Host}:{Port} is closed.");
    }

    private static async ValueTask ObserveStreamedSetResponseAsync(PendingResponseSource source)
    {
        try
        {
            using var response = await source.Task.ConfigureAwait(false);
        }
        catch
        {
            // Aborted or cancelled commands still need GetResult to release the caller reference.
        }
    }

    private ValueTask WriteStreamedPayloadAsync(StreamedSetCommand command, CancellationToken cancellationToken)
        => command.SourceStream is { } stream
            ? CopyStreamPayloadAsync(stream, command.Length, cancellationToken)
            : CopySequencePayloadAsync(command.Sequence, cancellationToken);

    private async ValueTask CopySequencePayloadAsync(ReadOnlySequence<byte> payload, CancellationToken cancellationToken)
    {
        // The payload is already in memory: copy its segments straight into the write buffer,
        // coalescing small segments and splitting large ones so every flush stays bounded. No
        // stream adapter, pooled chunk or per-read task is needed, and a read cannot fail.
        while (!payload.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chunk = payload.Slice(0, Math.Min(payload.Length, StreamChunkSize));
            var write = AppendStreamingBytes(in chunk);
            payload = payload.Slice(chunk.End);
            ScheduleFlush(startedBatch: false);
            await write.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask CopyStreamPayloadAsync(Stream source, long length, CancellationToken cancellationToken)
    {
        byte[]? chunk = ArrayPool<byte>.Shared.Rent(StreamChunkSize);
        try
        {
            var remaining = length;
            while (remaining > 0)
            {
                // Fill the chunk before appending so sources that return small reads (network
                // streams, for example) do not cost one socket write and flush wait per read.
                var target = (int)Math.Min(StreamChunkSize, remaining);
                var filled = 0;
                while (filled < target)
                {
                    var pendingRead = source.ReadAsync(chunk.AsMemory(filled, target - filled), cancellationToken).AsTask();
                    int read;
                    try
                    {
                        // WaitAsync also bounds streams that ignore their cancellation token.
                        read = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        // The read may still be writing into this pooled memory. Retain it until
                        // that read finishes instead of returning it while the source can mutate it.
                        _ = ReturnChunkAfterReadAsync(pendingRead, chunk);
                        chunk = null;
                        throw;
                    }

                    if (read == 0) throw new EndOfStreamException("Stream ended before its declared SET length.");
                    filled += read;
                }

                remaining -= filled;
                var write = AppendStreamingBytes(chunk.AsSpan(0, filled));
                ScheduleFlush(startedBatch: false);
                await write.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (chunk is not null) ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static async Task ReturnChunkAfterReadAsync(Task<int> pendingRead, byte[] chunk)
    {
        try { _ = await pendingRead.ConfigureAwait(false); }
        catch { /* The original streamed SET owns its failure. */ }
        finally { ArrayPool<byte>.Shared.Return(chunk); }
    }

    private bool IsClosedCancellation(
        OperationCanceledException error, CancellationToken effectiveCancellation, CancellationToken callerToken)
        => _closedCancellation.IsCancellationRequested && !callerToken.IsCancellationRequested
            && error.CancellationToken == effectiveCancellation;

    private RespireConnectionException ClosedDuringStreamedSet(OperationCanceledException error)
        => new($"Connection to {Host}:{Port} closed before the streamed SET completed.",
            Volatile.Read(ref _abortReason) ?? error);

    private async Task DrainBufferedWritesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? write = null;
            var schedule = false;
            lock (_writeGate)
            {
                ThrowIfStreamingUnavailable(rejectRetired: true);
                if (_activeBuffer.Count > 0)
                {
                    write = _activeBuffer.WriteCompletion;
                    schedule = true;
                }
                else if (Volatile.Read(ref _sending))
                {
                    write = _spareBuffer.WriteCompletion;
                    // The flush loop clears _sending outside this lock before completing the
                    // buffer. If it did so while this waiter was created, its completion may
                    // already have run; re-check instead of waiting for the buffer's next send.
                    Interlocked.MemoryBarrier();
                    if (!Volatile.Read(ref _sending)) continue;
                }
                else
                {
                    return;
                }
            }

            if (schedule) ScheduleFlush(startedBatch: false);
            if (write is null) continue;
            var drained = write.WaitAsync(cancellationToken);
            // Nothing of this frame is written yet, so retirement can still reject it for a retry
            // on the replacement; a stalled earlier write must not pin it to this connection.
            if (await Task.WhenAny(drained, _retiredSignal.Task).ConfigureAwait(false) != drained)
            {
                _ = drained.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                ThrowIfRetired();
            }
            await drained.ConfigureAwait(false);
        }
    }

    private Task AppendStreamingStart(
        StreamedSetCommand command, out bool startedBatch, out long requestWriteStart)
    {
        lock (_writeGate)
        {
            ThrowIfStreamingUnavailable(rejectRetired: true);
            var start = _activeBuffer.Count;
            // An earlier reply may still be pending after its frame has been sent and the
            // flush loop has parked. Wake inline whenever this header starts an empty buffer.
            startedBatch = start == 0;
            requestWriteStart = _enqueuedBytes;
            try
            {
                var writer = new RespWriter(_activeBuffer);
                command.WriteStart(ref writer);
            }
            catch
            {
                // Header serialization can touch caller-owned memory. Roll back any bytes it
                // appended so a later command cannot flush a partial RESP frame.
                _activeBuffer.TruncateTo(start);
                throw;
            }
            Volatile.Write(ref _enqueuedBytes, _enqueuedBytes + _activeBuffer.Count - start);
            return _activeBuffer.WriteCompletion;
        }
    }

    private Task AppendStreamingBytes(ReadOnlySpan<byte> bytes)
    {
        lock (_writeGate)
        {
            ThrowIfStreamingUnavailable(rejectRetired: false);
            _activeBuffer.Append(bytes);
            Volatile.Write(ref _enqueuedBytes, _enqueuedBytes + bytes.Length);
            return _activeBuffer.WriteCompletion;
        }
    }

    private Task AppendStreamingBytes(in ReadOnlySequence<byte> bytes)
    {
        lock (_writeGate)
        {
            ThrowIfStreamingUnavailable(rejectRetired: false);
            foreach (var segment in bytes) _activeBuffer.Append(segment.Span);
            Volatile.Write(ref _enqueuedBytes, _enqueuedBytes + bytes.Length);
            return _activeBuffer.WriteCompletion;
        }
    }

    private Task AppendStreamingEnd(
        StreamedSetCommand command, PendingResponse source, long requestWriteStart, out bool startedBatch)
    {
        lock (_writeGate)
        {
            ThrowIfStreamingUnavailable(rejectRetired: false);
            // Capacity was checked before the header, and _streamingActive has kept every other
            // producer out of _inflight since, so the slot is still free.
            Debug.Assert(_inflight.Capacity - _inflight.Count > 0, "Streamed SET lost its reserved in-flight slot.");
            var start = _activeBuffer.Count;
            startedBatch = start == 0 && _inflight.Count == 0;
            var writer = new RespWriter(_activeBuffer);
            command.WriteEnd(ref writer);
            // The request's write range spans the header, payload and trailer appends, so it is
            // stamped here rather than by the single-append StampWritePosition helper.
            var requestWriteEnd = _enqueuedBytes + _activeBuffer.Count - start;
            source.WriteStart = requestWriteStart;
            source.WriteEnd = requestWriteEnd;
            Volatile.Write(ref _enqueuedBytes, requestWriteEnd);
            if (!_inflight.TryEnqueue(source, requestWriteEnd))
                throw new InvalidOperationException("No in-flight slot remained for streamed SET response.");
            // Count the reply only once it is published, so a failed enqueue cannot leak a count.
            if (_responseTimeout is not null) _activeReplyCount++;
            return _activeBuffer.WriteCompletion;
        }
    }
}
