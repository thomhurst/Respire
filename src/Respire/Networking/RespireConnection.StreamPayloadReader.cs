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

        internal bool IsComplete => _remaining == 0;
        internal ReadOnlyMemory<byte> ConsumedPrefix => _consumedPrefix;

        internal async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            var chunk = _chunk ??= ArrayPool<byte>.Shared.Rent(StreamChunkSize);
            // Fill the chunk before returning it so sources that return small reads (network
            // streams, for example) do not cost one socket write and flush wait per read.
            var target = (int)Math.Min(StreamChunkSize, _remaining);
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
                    if (filled > 0) _consumedPrefix = chunk.AsMemory(0, filled).ToArray();
                    // The read may still be writing into this pooled memory. Retain it until
                    // that read finishes instead of returning it while the source can mutate it.
                    _chunk = null;
                    _ = ReturnChunkAfterReadAsync(pendingRead, chunk);
                    throw;
                }

                if (read == 0) throw new EndOfStreamException("Stream ended before its declared SET length.");
                filled += read;
            }

            _remaining -= filled;
            return chunk.AsMemory(0, filled);
        }

        public void Dispose()
        {
            if (_chunk is not { } chunk) return;
            _chunk = null;
            ArrayPool<byte>.Shared.Return(chunk);
        }

        private static async Task ReturnChunkAfterReadAsync(Task<int> pendingRead, byte[] chunk)
        {
            try { _ = await pendingRead.ConfigureAwait(false); }
            catch { /* The original streamed SET owns its failure. */ }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }
    }
}
