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
    private TaskCompletionSource? _writeCompletion;
#if DEBUG
    // The writer owns unpublished bytes until Complete; rollback explicitly discards them.
    internal bool HasUnpublishedWriterBytes;
    internal long NextWriterSequence;
    internal long UnpublishedWriterSequence;
#endif

    public WriteBuffer(int initialCapacity)
    {
        _array = RespirePools.WriteBuffers.Rent(initialCapacity);
    }

    public int Count => _count;

    public int Capacity => _array.Length;

    public ReadOnlyMemory<byte> WrittenMemory
    {
        get
        {
#if DEBUG
            if (HasUnpublishedWriterBytes)
                throw new InvalidOperationException("Complete or roll back the RESP writer before consuming its buffer.");
#endif
            return _array.AsMemory(0, _count);
        }
    }

    public Task WriteCompletion
        => (_writeCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

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

    /// <summary>Reserves a frame for rewriting while retaining bytes written beyond the committed count.</summary>
    /// <remarks>
    /// The returned span aliases the current buffer array. Consume it before another reservation
    /// that can grow the buffer, or before disposing the buffer; growth can return that array to the pool.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpanForRewrite(int position, int uncommittedLength, int frameLength)
    {
#if DEBUG
        var unpublished = HasUnpublishedWriterBytes;
#endif
        // Growth copies only committed bytes. Include the unpublished suffix before
        // reserving, then reacquire the span after the old array can return to the pool.
        Advance(uncommittedLength);
        try
        {
            GetSpan(checked(position + frameLength - _count));
        }
        finally
        {
            TruncateTo(position);
#if DEBUG
            // Internal growth preserves unpublished bytes rather than abandoning the writer.
            HasUnpublishedWriterBytes = unpublished;
#endif
        }
        return GetSpan(frameLength);
    }

    public void Reset()
    {
        _count = 0;
#if DEBUG
        HasUnpublishedWriterBytes = false;
#endif
    }

    public void CompleteWrite() => Interlocked.Exchange(ref _writeCompletion, null)?.TrySetResult();

    public void FailWrite(Exception exception)
        => Interlocked.Exchange(ref _writeCompletion, null)?.TrySetException(exception);

    /// <summary>Truncates back to a marked position (used to undo a partially written command).</summary>
    public void TruncateTo(int position)
    {
        _count = position;
#if DEBUG
        HasUnpublishedWriterBytes = false;
#endif
    }

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
#if DEBUG
        HasUnpublishedWriterBytes = false;
#endif
        if (array.Length > 0)
        {
            RespirePools.WriteBuffers.Return(array);
        }
    }
}
