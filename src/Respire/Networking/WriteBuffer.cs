using System.Runtime.CompilerServices;

namespace Respire.Networking;

/// <summary>
/// A growable byte buffer over a rented array, used as one half of the connection's
/// double-buffered write coalescing scheme. Not thread-safe; the connection's write gate
/// serializes producers, and the flush loop owns a buffer exclusively while sending it.
/// </summary>
internal sealed class WriteBuffer
{
    private byte[] _array;
    private int _count;

    public WriteBuffer(int initialCapacity)
    {
        _array = RespirePools.WriteBuffers.Rent(initialCapacity);
    }

    public int Count => _count;

    public int Capacity => _array.Length;

    public ReadOnlyMemory<byte> WrittenMemory => _array.AsMemory(0, _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Append(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        _count += bytes.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint)
    {
        if (_array.Length - _count < sizeHint)
        {
            Grow(sizeHint);
        }

        return _array.AsSpan(_count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count) => _count += count;

    public void Reset() => _count = 0;

    /// <summary>Truncates back to a marked position (used to undo a partially written command).</summary>
    public void TruncateTo(int position) => _count = position;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Grow(int sizeHint)
    {
        var required = (long)_count + sizeHint;
        var newSize = (int)Math.Min(Math.Max((long)_array.Length * 2, required), Array.MaxLength);
        if (newSize < required)
        {
            throw new InvalidOperationException("Cannot grow write buffer: maximum size reached.");
        }

        var newArray = RespirePools.WriteBuffers.Rent(newSize);
        _array.AsSpan(0, _count).CopyTo(newArray);
        RespirePools.WriteBuffers.Return(_array);
        _array = newArray;
    }

    public void Release()
    {
        var array = _array;
        _array = [];
        _count = 0;
        if (array.Length > 0)
        {
            RespirePools.WriteBuffers.Return(array);
        }
    }
}
