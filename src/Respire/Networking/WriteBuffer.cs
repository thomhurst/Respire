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
    private WriterGuard _writerGuard;

    internal long BeginWriter() => _writerGuard.Begin();
    internal long WriterMutationVersion => _writerGuard.Version;
    internal void ValidateWriter(long sequence, long version, bool publishing)
        => _writerGuard.Validate(sequence, version, publishing);
    internal void MarkWriterUnpublished(long sequence) => _writerGuard.MarkUnpublished(sequence);
    internal void MarkWriterPublished() => _writerGuard.MarkPublished();

    // Keep the helper itself out of Release metadata as well as removing its callers.
    private void InvalidateWriters() => _writerGuard.Invalidate();
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
            if (_writerGuard.HasUnpublishedBytes)
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
#if DEBUG
        InvalidateWriters();
#endif
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
    public void Advance(int count)
    {
        _count += count;
#if DEBUG
        InvalidateWriters();
#endif
    }

    /// <summary>Reserves a frame for rewriting while retaining bytes written beyond the committed count.</summary>
    /// <remarks>
    /// The returned span aliases the current buffer array. Consume it before another reservation
    /// that can grow the buffer, or before disposing the buffer; growth can return that array to the pool.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpanForRewrite(int position, int uncommittedLength, int frameLength)
    {
#if DEBUG
        var unpublished = _writerGuard.HasUnpublishedBytes;
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
            _writerGuard.RestoreUnpublished(unpublished);
#endif
        }
        return GetSpan(frameLength);
    }

    public void Reset()
    {
        _count = 0;
#if DEBUG
        _writerGuard.MarkPublished();
        InvalidateWriters();
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
        _writerGuard.MarkPublished();
        InvalidateWriters();
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
#if DEBUG
        InvalidateWriters();
#endif
    }

    public void Release()
    {
        var array = _array;
        _array = [];
        _count = 0;
#if DEBUG
        _writerGuard.MarkPublished();
        InvalidateWriters();
#endif
        if (array.Length > 0)
        {
            RespirePools.WriteBuffers.Return(array);
        }
    }

#if DEBUG
    // The connection's write gate serializes this state. A writer may publish only its
    // unfinished bytes; publishing, discarding, or replacing storage invalidates aliases.
    private struct WriterGuard
    {
        private bool _hasUnpublishedBytes;
        private long _nextSequence;
        private long _unpublishedSequence;
        private long _version;

        internal readonly bool HasUnpublishedBytes => _hasUnpublishedBytes;
        internal readonly long Version => _version;

        internal long Begin()
        {
            if (_hasUnpublishedBytes)
                throw new InvalidOperationException("A previous RESP writer left unfinished bytes. Complete it, or roll back with TruncateTo or Reset, before creating another writer.");
            return _nextSequence = unchecked(_nextSequence + 1);
        }

        internal readonly void Validate(long sequence, long version, bool publishing)
        {
            if (version != _version)
                throw new InvalidOperationException("The RESP writer's cached span was invalidated by buffer mutation.");
            if (_hasUnpublishedBytes && _unpublishedSequence != sequence)
                throw new InvalidOperationException(publishing
                    ? "Only the owning RESP writer can publish its unfinished bytes."
                    : "Only the owning RESP writer can change its unfinished bytes.");
        }

        internal void MarkUnpublished(long sequence)
        {
            _unpublishedSequence = sequence;
            _hasUnpublishedBytes = true;
        }

        internal void MarkPublished() => _hasUnpublishedBytes = false;
        internal void RestoreUnpublished(bool unpublished) => _hasUnpublishedBytes = unpublished;
        internal void Invalidate() => _version++;
    }
#endif
}
