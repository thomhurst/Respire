using System.Buffers;

namespace Respire.Commands;

internal sealed class SequencePayloadStream : Stream
{
    private ReadOnlySequence<byte> _remaining;
    private readonly long _length;

    internal SequencePayloadStream(ReadOnlySequence<byte> sequence)
    {
        _remaining = sequence;
        _length = sequence.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = (int)Math.Min(buffer.Length, _remaining.Length);
        _remaining.Slice(0, count).CopyTo(buffer.Span);
        _remaining = _remaining.Slice(count);
        return ValueTask.FromResult(count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var destination = buffer.AsSpan(offset, count);
        var copied = (int)Math.Min(destination.Length, _remaining.Length);
        _remaining.Slice(0, copied).CopyTo(destination);
        _remaining = _remaining.Slice(copied);
        return copied;
    }

    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
