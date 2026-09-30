using System.Buffers;

namespace Respire.Serialization;

// Per-operation scratch storage. Clear the entire rental, including uncommitted writes,
// because serializers and failed decoders can leave sensitive bytes beyond WrittenCount.
internal sealed class PooledByteBufferWriter(ArrayPool<byte>? pool = null) : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
    private byte[] _buffer = [];
    private int _written;
    private bool _disposed;

    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _buffer.Length - _written) throw new ArgumentOutOfRangeException(nameof(count));
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        sizeHint = Math.Max(1, sizeHint);
        if (sizeHint <= _buffer.Length - _written) return;
        var required = checked(_written + sizeHint);
        var capacity = (int)Math.Min(Array.MaxLength, Math.Max((long)required, Math.Max(256L, (long)_buffer.Length * 2)));
        if (capacity < required) throw new OutOfMemoryException();
        var replacement = _pool.Rent(capacity);
        WrittenSpan.CopyTo(replacement);
        if (_buffer.Length != 0) _pool.Return(_buffer, clearArray: true);
        _buffer = replacement;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_buffer.Length != 0) _pool.Return(_buffer, clearArray: true);
        _buffer = [];
        _written = 0;
    }
}
