using System.Buffers;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>
    /// Reads a stream source in filled chunks of at most <see cref="StreamChunkSize"/> bytes into
    /// two pooled buffers so the alternate chunk can fill while the caller writes the current one.
    /// </summary>
    internal sealed class StreamPayloadReader(Stream source, long length, ArrayPool<byte>? pool = null) : IDisposable
    {
        private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
        private readonly object _bufferOwnershipGate = new();
        private byte[]? _chunk;
        private byte[]? _alternateChunk;
        private byte[]? _activeChunkBuffer;
        private TaskCompletionSource? _disposeChunkReadCompletion;
        private byte[]? _pendingBuffer;
        private int _disposed;
        private bool _readingChunk;
        private bool _useAlternate;
        private long _remaining = length;
        private ReadOnlyMemory<byte> _consumedPrefix;
        private Task<int>? _pendingRead;
        private int _filledBeforePendingRead;
        private Exception? _unknownPositionReadError;

        internal bool IsComplete => _remaining == 0;
        internal ReadOnlyMemory<byte> ConsumedPrefix => _consumedPrefix;
        internal bool HasPendingRead => Volatile.Read(ref _pendingRead) is not null;
        internal Exception? UnknownPositionReadError => _unknownPositionReadError;

        internal async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(CancellationToken cancellationToken)
        {
            byte[] chunk;
            // Allocate and publish ownership together. Dispose must not return the buffer between
            // the disposed check and assignment of the active fill.
            lock (_bufferOwnershipGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                chunk = _useAlternate
                    ? _alternateChunk ??= _pool.Rent(StreamChunkSize)
                    : _chunk ??= _pool.Rent(StreamChunkSize);
                _useAlternate = !_useAlternate;
                // Ownership spans all partial reads, including gaps between ReadAsync calls.
                _readingChunk = true;
                _activeChunkBuffer = chunk;
            }
            // Fill the chunk before returning it so sources that return small reads (network
            // streams, for example) do not cost one socket write and flush wait per read.
            var target = (int)Math.Min(StreamChunkSize, _remaining);
            var filled = 0;
            try
            {
                while (filled < target)
                {
                    Task<int>? pendingRead = null;
                    try
                    {
                        pendingRead = source.ReadAsync(chunk.AsMemory(filled, target - filled), cancellationToken).AsTask();
                        // Publish buffer ownership before yielding. Dispose can run as soon as the
                        // concurrent socket write fails, before WaitAsync resumes with cancellation.
                        Volatile.Write(ref _pendingBuffer, chunk);
                        Volatile.Write(ref _filledBeforePendingRead, filled);
                        Volatile.Write(ref _pendingRead, pendingRead);
                        // WaitAsync also bounds streams that ignore their cancellation token.
                        var read = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (ReferenceEquals(Volatile.Read(ref _pendingRead), pendingRead))
                        {
                            Volatile.Write(ref _pendingRead, null);
                            Volatile.Write(ref _pendingBuffer, null);
                        }
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
                            Volatile.Write(ref _pendingBuffer, chunk);
                            Volatile.Write(ref _filledBeforePendingRead, filled);
                            Volatile.Write(ref _pendingRead, pendingRead);
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
                            Volatile.Write(ref _pendingRead, null);
                            Volatile.Write(ref _pendingBuffer, null);
                            _unknownPositionReadError = error;
                        }
                        throw;
                    }
                }

                _remaining -= filled;
                return chunk.AsMemory(0, filled);
            }
            finally
            {
                var pendingRead = Volatile.Read(ref _pendingRead);
                TaskCompletionSource? disposeCompletion;
                lock (_bufferOwnershipGate)
                {
                    _readingChunk = false;
                    _activeChunkBuffer = null;
                    disposeCompletion = _disposeChunkReadCompletion;
                }
                if (disposeCompletion is not null)
                {
                    if (pendingRead is { IsCompleted: false })
                        _ = SettleChunkReadAfterSourceReadAsync(disposeCompletion, pendingRead);
                    else
                        disposeCompletion.TrySetResult();
                }
            }
        }

        internal async ValueTask<Exception?> CompletePendingReadForRetryAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _pendingRead) is not { } pendingRead) return null;
            var chunk = Volatile.Read(ref _pendingBuffer)!;
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
            Volatile.Write(ref _pendingRead, null);
            Volatile.Write(ref _pendingBuffer, null);
            if (read > 0) _consumedPrefix = chunk.AsMemory(0, _filledBeforePendingRead + read).ToArray();
            return readError;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            byte[]? activeChunkBuffer = null;
            Task? activeChunkSettled = null;
            lock (_bufferOwnershipGate)
            {
                if (_readingChunk)
                {
                    activeChunkBuffer = _activeChunkBuffer;
                    var settled = _disposeChunkReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    activeChunkSettled = settled.Task;
                }
            }
            var pendingBuffer = Volatile.Read(ref _pendingBuffer);
            var pendingRead = Volatile.Read(ref _pendingRead);
            byte[]? retainedBuffer = null;
            Task? bufferSettled = null;
            if (activeChunkBuffer is not null)
            {
                retainedBuffer = activeChunkBuffer;
                bufferSettled = activeChunkSettled;
            }
            else if (pendingRead is { IsCompleted: false } && pendingBuffer is not null)
            {
                retainedBuffer = pendingBuffer;
                bufferSettled = pendingRead;
            }
            if (_chunk is { } chunk && !ReferenceEquals(chunk, retainedBuffer)) _pool.Return(chunk);
            if (_alternateChunk is { } alternate && !ReferenceEquals(alternate, retainedBuffer)) _pool.Return(alternate);
            if (retainedBuffer is not null && bufferSettled is not null)
                _ = ReturnChunkAfterReadAsync(bufferSettled, retainedBuffer);
            _chunk = null;
            _alternateChunk = null;
        }

        private static async Task SettleChunkReadAfterSourceReadAsync(TaskCompletionSource settled, Task<int> pendingRead)
        {
            try { _ = await pendingRead.ConfigureAwait(false); }
            catch { /* The original streamed SET owns its failure. */ }
            finally { settled.TrySetResult(); }
        }

        private async Task ReturnChunkAfterReadAsync(Task bufferSettled, byte[] chunk)
        {
            try { await bufferSettled.ConfigureAwait(false); }
            catch { /* The original streamed SET owns its failure. */ }
            finally { _pool.Return(chunk); }
        }
    }
}
