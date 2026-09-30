using System.IO.Pipelines;

namespace Respire.Testing;

internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer, Action? onDispose = null) : Stream
{
    private readonly Stream _input = reader.AsStream();
    private readonly Stream _output = writer.AsStream();
    private readonly object _lifetimeGate = new();
    private int _activeReads;
    private int _activeWrites;
    private bool _cancellationIssued;
    private bool _inputCompleted;
    private bool _outputCompleted;
    private int _disposed;
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => CanRead;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        BeginOperation(read: true);
        try { return _input.Read(buffer, offset, count); }
        finally { EndOperation(read: true); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeginOperation(read: true);
        try { return await _input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        finally { EndOperation(read: true); }
    }
    public override void Write(byte[] buffer, int offset, int count)
    {
        BeginOperation(read: false);
        try { _output.Write(buffer, offset, count); }
        finally { EndOperation(read: false); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeginOperation(read: false);
        try { await _output.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); }
        finally { EndOperation(read: false); }
    }
    public override void Flush()
    {
        // Flush owns the writer side just like Write; disposal must wait for either.
        BeginOperation(read: false);
        try { _output.Flush(); }
        finally { EndOperation(read: false); }
    }
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        BeginOperation(read: false);
        try { await _output.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { EndOperation(read: false); }
    }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                onDispose?.Invoke();
                reader.CancelPendingRead();
                writer.CancelPendingFlush();
            }
            finally
            {
                // Cancellation unblocks pending I/O, but a completed read may still own
                // a result until AdvanceTo returns. Never complete its pipe underneath it.
                lock (_lifetimeGate) _cancellationIssued = true;
                CompleteIdleSides();
            }
        }
        base.Dispose(disposing);
    }

    private void BeginOperation(bool read)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (read) _activeReads++;
            else _activeWrites++;
        }
    }

    private void EndOperation(bool read)
    {
        lock (_lifetimeGate)
        {
            if (read) _activeReads--;
            else _activeWrites--;
        }
        if (Volatile.Read(ref _disposed) != 0) CompleteIdleSides();
    }

    private void CompleteIdleSides()
    {
        bool completeInput;
        bool completeOutput;
        lock (_lifetimeGate)
        {
            // Do not complete a side while Dispose is still issuing cancellation to it.
            if (!_cancellationIssued) return;
            completeInput = _activeReads == 0 && !_inputCompleted;
            completeOutput = _activeWrites == 0 && !_outputCompleted;
            if (completeInput) _inputCompleted = true;
            if (completeOutput) _outputCompleted = true;
        }
        // Completion can resume the peer; do not run it under the lifetime lock.
        try { if (completeInput) _input.Dispose(); }
        finally { if (completeOutput) _output.Dispose(); }
    }
}
