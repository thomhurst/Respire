namespace Respire.Internal;

// The transport can continue draining an abandoned bulk frame to preserve FIFO. Its
// circuit permit must be released as ignored without waiting for that drain to finish.
internal sealed class CircuitStreamCompletion
{
    private CircuitAdmission _admission;
    private readonly CancellationToken _cancellationToken;
    private CancellationTokenRegistration _registration;
    private int _completed;

    internal CircuitStreamCompletion(CircuitAdmission admission, CancellationToken cancellationToken)
    {
        _admission = admission;
        _cancellationToken = cancellationToken;
        _registration = cancellationToken.UnsafeRegister(static state => ((CircuitStreamCompletion)state!).Ignore(), this);
        if (Volatile.Read(ref _completed) != 0) _registration.Unregister();
    }

    internal void Complete(Exception? error)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _registration.Unregister();
        if (error is null && !_cancellationToken.IsCancellationRequested) _admission.Success();
        else if (error is not null) _admission.Failed(error, _cancellationToken);
        _admission.Dispose();
    }

    internal void Ignore()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _registration.Unregister();
        _admission.Dispose();
    }
}

internal sealed class CircuitCompletionStream(Stream inner, CircuitStreamCompletion completion) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { completion.Ignore(); throw; }
    }
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            completion.Ignore();
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        completion.Ignore();
        await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
