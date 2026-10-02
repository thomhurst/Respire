using System.Buffers;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>
    /// Reads a stream source in filled chunks of at most <see cref="StreamChunkSize"/> bytes into
    /// one pooled buffer. Each returned chunk is valid until the next read or <see cref="Dispose"/>.
    /// </summary>
    private sealed class StreamPayloadReader(Stream source, long length) : IDisposable
    {
        private byte[]? _chunk;
        private long _remaining = length;
        private ReadOnlyMemory<byte> _consumedPrefix;
        private Task<int>? _pendingRead;
        private int _filledBeforePendingRead;
        private Exception? _unknownPositionReadError;

        internal bool IsComplete => _remaining == 0;
        internal ReadOnlyMemory<byte> ConsumedPrefix => _consumedPrefix;
        internal bool HasPendingRead => _pendingRead is not null;
        internal Exception? UnknownPositionReadError => _unknownPositionReadError;

        internal async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            var chunk = _chunk ??= ArrayPool<byte>.Shared.Rent(StreamChunkSize);
            // Fill the chunk before returning it so sources that return small reads (network
            // streams, for example) do not cost one socket write and flush wait per read.
            var target = (int)Math.Min(StreamChunkSize, _remaining);
            var filled = 0;
            while (filled < target)
            {
                Task<int>? pendingRead = null;
                try
                {
                    pendingRead = source.ReadAsync(chunk.AsMemory(filled, target - filled), cancellationToken).AsTask();
                    // WaitAsync also bounds streams that ignore their cancellation token.
                    var read = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new EndOfStreamException("Stream ended before its declared SET length.");
                    filled += read;
                }
                catch (Exception error)
                {
                    if (filled > 0) _consumedPrefix = chunk.AsMemory(0, filled).ToArray();
                    if (pendingRead is { IsCompleted: false })
                    {
                        // Retirement retry must wait for a non-cooperative read before replaying
                        // the source. It may consume more bytes and still write into this buffer.
                        _pendingRead = pendingRead;
                        _filledBeforePendingRead = filled;
                    }
                    else if (pendingRead is { Status: TaskStatus.RanToCompletion })
                    {
                        var completedRead = pendingRead.GetAwaiter().GetResult();
                        if (completedRead > 0) _consumedPrefix = chunk.AsMemory(0, filled + completedRead).ToArray();
                    }
                    else
                    {
                        // A failed read has no byte count. The source may have advanced before
                        // throwing, so a retirement retry cannot safely replay it.
                        _unknownPositionReadError = error;
                    }
                    throw;
                }
            }

            _remaining -= filled;
            return chunk.AsMemory(0, filled);
        }

        internal async ValueTask<Exception?> CompletePendingReadForRetryAsync(CancellationToken cancellationToken)
        {
            if (_pendingRead is not { } pendingRead) return null;
            var chunk = _chunk!;
            int read;
            Exception? readError = null;
            try
            {
                read = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException error)
            {
                // The canceled source read may have advanced before throwing. Stream.ReadAsync
                // exposes no count, so its position is unknown and the payload cannot be replayed.
                readError = error;
                read = 0;
            }
            catch (Exception error)
            {
                // A faulted Stream read reports no byte count. Its source position is unknown, so
                // the caller must fail instead of retrying a potentially shifted payload.
                readError = error;
                read = 0;
            }
            _pendingRead = null;
            if (read > 0) _consumedPrefix = chunk.AsMemory(0, _filledBeforePendingRead + read).ToArray();
            return readError;
        }

        public void Dispose()
        {
            if (_chunk is not { } chunk) return;
            _chunk = null;
            if (_pendingRead is { } pendingRead)
            {
                _pendingRead = null;
                _ = ReturnChunkAfterReadAsync(pendingRead, chunk);
            }
            else ArrayPool<byte>.Shared.Return(chunk);
        }

        private static async Task ReturnChunkAfterReadAsync(Task<int> pendingRead, byte[] chunk)
        {
            try { _ = await pendingRead.ConfigureAwait(false); }
            catch { /* The original streamed SET owns its failure. */ }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }
    }
}
