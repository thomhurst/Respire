using System.Net.Sockets;
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
        var part = 0;
        while (true)
        {
            var segments = buffer.GetSendSegments(ref part);
            if (segments.Count == 0) return;
            while (segments.Count != 0)
            {
                // No cancellation: even a cancelled response cannot abandon an open RESP frame.
                // Abort closes the socket; the lease is released only after this send has returned.
                var sent = await buffer.SendSegmentsAsync(_socket!, segments).ConfigureAwait(false);
                if (sent <= 0) throw new IOException("Socket closed during gathered SET write.");
                RecordWrite(sent);
                WriteBuffer.ConsumeSentSegments(segments, sent);
            }
        }
    }
}
