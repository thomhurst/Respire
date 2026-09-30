using System.IO.Pipelines;

namespace Respire.Testing;

internal sealed class DuplexPipeStream(PipeReader reader, PipeWriter writer) : Stream
{
    private readonly Stream _input = reader.AsStream();
    private readonly Stream _output = writer.AsStream();
    private int _disposed;
    public override bool CanRead => Volatile.Read(ref _disposed) == 0;
    public override bool CanWrite => CanRead;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _input.ReadAsync(buffer, cancellationToken);
    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _output.WriteAsync(buffer, cancellationToken);
    public override void Flush() => _output.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _output.FlushAsync(cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            reader.CancelPendingRead();
            writer.CancelPendingFlush();
            _input.Dispose();
            _output.Dispose();
        }
        base.Dispose(disposing);
    }
}
