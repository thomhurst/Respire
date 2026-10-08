using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Commands;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private bool TryEnqueueGathered(in GatheredSetCommand command, PendingResponse source,
        CommandDeadline deadline, out bool startedBatch, out Task? writeTask, bool trackWrite,
        int discardRepliesBefore, bool retainRepliesBefore, string? discardedOperation)
    {
        startedBatch = false;
        writeTask = null;
        lock (_writeGate)
        {
            ThrowIfRetired();
            if (_dead) throw ClosedBeforeEnqueue();
            if (_streamingActive || _credentialRenewalPending || !_inflight.HasCapacity(discardRepliesBefore + 1))
                return false;
            if (!_activeBuffer.PrepareBorrowedPayload())
                return TryEnqueueDirect(in command, command.GetWriteSizeHint(), source, deadline,
                    out startedBatch, out writeTask, trackWrite, discardRepliesBefore, retainRepliesBefore,
                    discardedOperation);

            var mark = _activeBuffer.Count;
            startedBatch = mark == 0 && _inflight.Count == 0;
            int offset;
            int frameLength;
            try
            {
                offset = command.WriteEnvelope(_activeBuffer);
                frameLength = checked(_activeBuffer.Count - mark + command.Payload.Count);
            }
            catch { _activeBuffer.TruncateTo(mark); throw; }
            _activeBuffer.AddBorrowedPayload(offset, command.Payload, command.Lease);
            if (_responseTimeout is not null) _activeReplyCount += discardRepliesBefore + 1;
            var start = StampWritePosition(source, frameLength);
            StampDeadline(source, deadline);
            for (var index = 0; index < discardRepliesBefore; index++)
                _inflight.TryEnqueue(retainRepliesBefore ? source : InflightRing.DiscardSentinel, start);
            if (discardedOperation is not null) _inflight.TryEnqueueDiscard(discardedOperation, _producerProgress.EnqueuedBytes);
            else _inflight.TryEnqueue(source, _producerProgress.EnqueuedBytes);
            if (trackWrite) writeTask = _activeBuffer.WriteCompletion;
            return true;
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendGatheredBufferAsync(WriteBuffer buffer)
    {
        var watchWrite = _responseTimeout is not null;
        if (watchWrite) SetGatheredWriteDeadline(Stopwatch.GetTimestamp());
        try
        {
            var part = 0;
            while (true)
            {
                var segments = buffer.GetSendSegments(ref part);
                if (segments.Count == 0) return;
                while (segments.Count != 0)
                {
                    // Response cancellation cannot abandon an open RESP frame. When configured,
                    // the connection watchdog bounds stalled writes by closing the whole socket.
                    // The lease is released only after this kernel send has returned.
                    var sent = await buffer.SendSegmentsAsync(_socket!, segments).ConfigureAwait(false);
                    if (sent <= 0) throw new IOException("Socket closed during gathered SET write.");
                    RecordWrite(sent);
                    if (watchWrite) SetGatheredWriteDeadline(Stopwatch.GetTimestamp());
                    WriteBuffer.ConsumeSentSegments(segments, sent);
                }
            }
        }
        finally { if (watchWrite) SetGatheredWriteDeadline(0); }
    }

    private void SetGatheredWriteDeadline(long timestamp)
    {
        // Clearing or advancing progress cannot race the watchdog's terminal decision.
        // This gate is used only when the existing watchdog is configured.
        lock (_receiveDeadlineGate) Volatile.Write(ref _flushProgress.GatheredWriteDeadlineTimestamp, timestamp);
    }

    private bool TryAbortStalledGatheredWrite(TimeSpan timeout)
    {
        var timestamp = Volatile.Read(ref _flushProgress.GatheredWriteDeadlineTimestamp);
        if (timestamp == 0 || Stopwatch.GetElapsedTime(timestamp) < timeout) return false;
        bool closed;
        lock (_receiveDeadlineGate)
        {
            var effectiveTimeout = MaintenanceTimeout(timeout, Environment.TickCount64, out _, out _);
            if (timestamp != _flushProgress.GatheredWriteDeadlineTimestamp
                || Volatile.Read(ref _responseTimeoutSuppressions) != 0
                || Stopwatch.GetElapsedTime(timestamp) < effectiveTimeout) return false;
            closed = Abort(new RespireConnectionException(
                $"Connection to {Host}:{Port} gathered SET write made no progress for {effectiveTimeout}."),
                publishConnectionMetrics: false);
        }
        if (closed) _connectionMetrics?.Closed(_abortReason, Volatile.Read(ref _peerClosed));
        return closed;
    }
}
