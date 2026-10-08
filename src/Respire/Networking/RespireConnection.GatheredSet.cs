using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Commands;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    private bool TryEnqueueGathered(in GatheredSetCommand command, PendingResponse source,
        CommandDeadline deadline, out bool startedBatch, out Task? writeTask, bool trackWrite,
        int discardRepliesBefore, bool retainRepliesBefore, string? discardedOperation,
        CommandWriteObservation? writeObservation = null)
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
                    discardedOperation, writeObservation);

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
            if (_responseTimeout is not null && !_activeBuffer.HasBorrowedPayloads)
            {
                // Arm when ownership is accepted, even if a preceding copied buffer is stalled.
                // Further enqueues must not postpone a stall already being watched.
                if (_flushProgress.GatheredWriteBufferCount++ == 0)
                    Volatile.Write(ref _flushProgress.GatheredWriteDeadlineTimestamp, Stopwatch.GetTimestamp());
            }
            _activeBuffer.AddBorrowedPayload(offset, command.Payload, command.Lease);
            if (_responseTimeout is not null) _activeReplyCount += discardRepliesBefore + 1;
            var start = StampWritePosition(source, frameLength);
            writeObservation?.RecordQueued(this, start, _producerProgress.EnqueuedBytes);
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
                WriteBuffer.ConsumeSentSegments(segments, sent);
            }
        }
    }

    private void CompleteGatheredWriteWatch()
    {
        // The sender has finished every byte in this buffer, including copied trailing commands.
        // Keep watching if the producer accepted another borrowed buffer while this one sent.
        lock (_writeGate)
        {
            Debug.Assert(_flushProgress.GatheredWriteBufferCount > 0);
            if (--_flushProgress.GatheredWriteBufferCount == 0)
                Volatile.Write(ref _flushProgress.GatheredWriteDeadlineTimestamp, 0);
        }
    }

    private bool TryAbortStalledGatheredWrite(TimeSpan timeout)
    {
        var timestamp = Volatile.Read(ref _flushProgress.GatheredWriteDeadlineTimestamp);
        if (timestamp == 0 || Stopwatch.GetElapsedTime(timestamp) < timeout) return false;
        bool closed;
        lock (_writeGate)
        {
            var effectiveTimeout = MaintenanceTimeout(timeout, Environment.TickCount64, out _, out _);
            if (timestamp != _flushProgress.GatheredWriteDeadlineTimestamp
                || Stopwatch.GetElapsedTime(timestamp) < effectiveTimeout) return false;
            closed = Abort(new RespireConnectionException(
                $"Connection to {Host}:{Port} gathered SET write made no progress for {effectiveTimeout}."),
                publishConnectionMetrics: false);
        }
        if (closed) _connectionMetrics?.Closed(_abortReason, Volatile.Read(ref _peerClosed));
        return closed;
    }
}
