using System.Buffers;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>
    /// Reads a stream source in filled chunks of at most <see cref="StreamChunkSize"/> bytes into
    /// one pooled buffer. The caller copies each chunk into the connection write buffer before the
    /// next source read reuses this buffer.
    /// </summary>
    private sealed class StreamPayloadReader(Stream source, long length, ArrayPool<byte>? pool = null) : IDisposable
    {
        private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
        private readonly object _bufferOwnershipGate = new();
        private byte[]? _chunk;
        private TaskCompletionSource? _disposeChunkReadCompletion;
        private int _disposed;
        private bool _readingChunk;
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
                chunk = _chunk ??= _pool.Rent(StreamChunkSize);
                // Ownership spans all partial reads, including gaps between ReadAsync calls.
                _readingChunk = true;
            }
            // Fill the chunk before returning it so sources that return small reads (network
            // streams, for example) do not cost one socket write and flush wait per read.
            var target = (int)Math.Min(StreamChunkSize, _remaining);
            var filled = 0;
            try
            {
                while (filled < target)
                {
                    ValueTask<int> readOperation = default;
                    Task<int>? pendingRead = null;
                    try
                    {
                        readOperation = source.ReadAsync(chunk.AsMemory(filled, target - filled), cancellationToken);
                        if (readOperation.IsCompletedSuccessfully)
                        {
                            var synchronousRead = readOperation.Result;
                            if (synchronousRead == 0)
                                throw new EndOfStreamException("Stream ended before its declared SET length.");
                            filled += synchronousRead;
                            continue;
                        }

                        pendingRead = readOperation.AsTask();
                        // Publish buffer ownership before yielding. Dispose can run as soon as the
                        // concurrent socket write fails, before WaitAsync resumes with cancellation.
                        Volatile.Write(ref _filledBeforePendingRead, filled);
                        Volatile.Write(ref _pendingRead, pendingRead);
                        // WaitAsync also bounds streams that ignore their cancellation token.
                        var read = await pendingRead.WaitAsync(cancellationToken).ConfigureAwait(false);
                        if (ReferenceEquals(Volatile.Read(ref _pendingRead), pendingRead))
                        {
                            Volatile.Write(ref _pendingRead, null);
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
            var chunk = Volatile.Read(ref _chunk)!;
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
            if (read > 0) _consumedPrefix = chunk.AsMemory(0, _filledBeforePendingRead + read).ToArray();
            return readError;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            byte[]? chunk;
            Task? activeChunkSettled = null;
            lock (_bufferOwnershipGate)
            {
                chunk = _chunk;
                if (_readingChunk)
                {
                    var settled = _disposeChunkReadCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    activeChunkSettled = settled.Task;
                }
            }
            var pendingRead = Volatile.Read(ref _pendingRead);
            byte[]? retainedBuffer = null;
            Task? bufferSettled = null;
            if (activeChunkSettled is not null && chunk is not null)
            {
                retainedBuffer = chunk;
                bufferSettled = activeChunkSettled;
            }
            else if (pendingRead is { IsCompleted: false } && chunk is not null)
            {
                retainedBuffer = chunk;
                bufferSettled = pendingRead;
            }
            if (chunk is not null && !ReferenceEquals(chunk, retainedBuffer)) _pool.Return(chunk);
            if (retainedBuffer is not null && bufferSettled is not null)
                _ = ReturnChunkAfterReadAsync(bufferSettled, retainedBuffer);
            _chunk = null;
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
