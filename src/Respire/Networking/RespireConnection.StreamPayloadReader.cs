using System.Buffers;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    /// <summary>
    /// Reads filled chunks into one pooled buffer. The caller copies each chunk into the connection
    /// write buffer before starting another fill. Retry and disposal both observe the entire fill,
    /// including synchronous source work and every asynchronous short read.
    /// </summary>
    internal sealed class StreamPayloadReader(Stream source, long length, ArrayPool<byte>? pool = null) : IDisposable
    {
        private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
        private readonly Lock _bufferOwnershipGate = new();
        private CancellationTokenSource? _sourceCancellation;
        private CancellationToken _effectiveCancellation;
        private CancellationTokenRegistration _sourceCancellationRegistration;
        private Task? _sourceCancellationCallbacks;
        private byte[]? _chunk;
        private Task<ReadOnlyMemory<byte>>? _activeFill;
        private Task<ReadOnlyMemory<byte>>? _completedInlineFill;
        private int _completedInlineLength;
        private bool _disposed;
        private long _remaining = length;
        private ReadOnlyMemory<byte> _consumedPrefix;
        private Exception? _unknownPositionReadError;

        // Read only after awaiting the fill task (including its failure), which publishes all state.
        internal bool IsComplete => _remaining == 0;
        internal ReadOnlyMemory<byte> ConsumedPrefix => _consumedPrefix;
        internal Exception? UnknownPositionReadError => _unknownPositionReadError;

        // Consume and copy the previous result before starting another fill. Completed tasks
        // can be reused and describe the current contents of the single owned buffer.
        internal Task<ReadOnlyMemory<byte>> StartRead(CancellationToken cancellationToken)
        {
            lock (_bufferOwnershipGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_activeFill is { IsCompleted: false })
                    throw new InvalidOperationException("A streamed SET chunk fill is already active.");
                var inlineRead = source.GetType() == typeof(MemoryStream);
                if (!inlineRead && _sourceCancellation is null)
                {
                    _sourceCancellation = new CancellationTokenSource();
                    _effectiveCancellation = cancellationToken;
                    // Source callbacks are user code. Run them separately so a blocking callback
                    // cannot hold up the transport's WaitAsync callback on the effective token.
                    _sourceCancellationRegistration = cancellationToken.UnsafeRegister(static state =>
                    {
                        var reader = (StreamPayloadReader)state!;
                        reader._sourceCancellationCallbacks = reader._sourceCancellation!.CancelAsync();
                        ObserveCancellationCallbacks(reader._sourceCancellationCallbacks);
                    }, this);
                }
                var chunk = _chunk ??= _pool.Rent(StreamChunkSize);
                // Exact MemoryStream reads cannot invoke custom synchronous work. Derived streams
                // remain offloaded because their ReadAsync override may block before returning.
                // Publish the whole task before disposal can acquire the gate.
                return _activeFill = inlineRead
                    ? ReadInlineFill(chunk, cancellationToken)
                    : StartBackgroundFill(chunk, cancellationToken);
            }
        }

        private Task<ReadOnlyMemory<byte>> StartBackgroundFill(byte[] chunk, CancellationToken cancellationToken)
            => Task.Run(() => ReadChunkAsync(chunk, cancellationToken).AsTask(), CancellationToken.None);

        private Task<ReadOnlyMemory<byte>> ReadInlineFill(byte[] chunk, CancellationToken cancellationToken)
        {
            var fill = ReadChunkAsync(chunk, cancellationToken);
            if (!fill.IsCompletedSuccessfully) return fill.AsTask();
            var result = fill.Result;
            // The task describes a slice of the reader's single buffer, not a payload snapshot.
            // Reuse it only after the previous chunk has been copied by the transport. A shorter
            // tail needs its own slice; faults and cancellation must retain their original task.
            if (_completedInlineFill is null || _completedInlineLength != result.Length)
            {
                _completedInlineFill = Task.FromResult(result);
                _completedInlineLength = result.Length;
            }
            return _completedInlineFill;
        }

        // Keep the default builder: suspended derived-stream fills still publish ordinary tasks.
        // ValueTask avoids constructing a task only when an exact MemoryStream finishes inline.
        private async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(byte[] chunk, CancellationToken cancellationToken)
        {
            _consumedPrefix = default;
            _unknownPositionReadError = null;
            var target = (int)Math.Min(StreamChunkSize, _remaining);
            var filled = 0;
            try
            {
                while (filled < target)
                {
                    // Cancellation between reads has a known position. A source exception does not.
                    cancellationToken.ThrowIfCancellationRequested();
                    int read;
                    try
                    {
                        var operation = source.ReadAsync(chunk.AsMemory(filled, target - filled),
                            _sourceCancellation?.Token ?? cancellationToken);
                        read = operation.IsCompletedSuccessfully ? operation.Result : await operation.ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        _unknownPositionReadError = error;
                        throw;
                    }
                    if (read == 0) throw new EndOfStreamException("Stream ended before its declared SET length.");
                    filled += read;
                }
                _remaining -= filled;
                return chunk.AsMemory(0, filled);
            }
            finally
            {
                // No reader inspects this memory before the task settles, and it stays owned until
                // retry copies the prefix or disposal returns the buffer after the same task.
                _consumedPrefix = chunk.AsMemory(0, filled);
            }
        }

        public void Dispose()
        {
            byte[]? chunk;
            Task<ReadOnlyMemory<byte>>? activeFill;
            lock (_bufferOwnershipGate)
            {
                if (_disposed) return;
                _disposed = true;
                chunk = _chunk;
                _chunk = null;
                activeFill = _activeFill;
                _completedInlineFill = null;
            }
            // This registration only schedules callbacks; joining it cannot wait on user code.
            _sourceCancellationRegistration.Dispose();
            // The transport's newer wait callback may finish the upload before cancellation
            // reaches this registration. Unregistering it must not lose cancellation of the source.
            if (_effectiveCancellation.IsCancellationRequested && _sourceCancellation is { IsCancellationRequested: false })
            {
                _sourceCancellationCallbacks = _sourceCancellation.CancelAsync();
                ObserveCancellationCallbacks(_sourceCancellationCallbacks);
            }
            _ = ReleaseResourcesAsync(activeFill, chunk, _sourceCancellationCallbacks);
        }

        private async Task ReleaseResourcesAsync(Task? fill, byte[]? chunk, Task? callbacks)
        {
            try { if (fill is not null) await fill.ConfigureAwait(false); }
            catch { /* The original streamed SET owns its failure. */ }
            finally { if (chunk is not null) _pool.Return(chunk); }
            try { if (callbacks is not null) await callbacks.ConfigureAwait(false); }
            catch { /* A source callback cannot escape upload cleanup. */ }
            finally { _sourceCancellation?.Dispose(); }
        }
    }
}
