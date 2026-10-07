using System.Buffers;
using System.Numerics;

namespace Respire.Networking;

/// <summary>Shared size buckets with fixed retention independent of thread/connection count.</summary>
internal sealed class BoundedResponseArrayPool<T> : ArrayPool<T>
{
    private const int SmallBucketCapacity = 256;
    private const int MediumBucketCapacity = 16;
    private const int LargeBucketCapacity = 1;
    private readonly LockFreeStack<T[]>[] _buckets;
    private readonly int _maximumLength;

    internal BoundedResponseArrayPool(int smallMaximum, int mediumMaximum, int maximumLength)
    {
        if (smallMaximum < 16 || !BitOperations.IsPow2(smallMaximum))
            throw new ArgumentOutOfRangeException(nameof(smallMaximum));
        if (mediumMaximum < smallMaximum || !BitOperations.IsPow2(mediumMaximum))
            throw new ArgumentOutOfRangeException(nameof(mediumMaximum));
        if (maximumLength < mediumMaximum || !BitOperations.IsPow2(maximumLength))
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        _maximumLength = maximumLength;
        _buckets = new LockFreeStack<T[]>[BitOperations.Log2((uint)maximumLength) - 3];
        for (var index = 0; index < _buckets.Length; index++)
        {
            var length = 16 << index;
            // Reuse the existing bounded striped CAS stack. Small buckets have
            // 256 slots in total; medium buckets have 16; large buckets have one.
            var capacity = LargeBucketCapacity;
            if (length <= smallMaximum) capacity = SmallBucketCapacity;
            else if (length <= mediumMaximum) capacity = MediumBucketCapacity;
            _buckets[index] = new LockFreeStack<T[]>(capacity);
        }
    }

    public override T[] Rent(int minimumLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumLength);
        if (minimumLength == 0) return Array.Empty<T>();
        if (minimumLength > _maximumLength) return new T[minimumLength];
        var index = BucketIndex(minimumLength);
        // Do not borrow a larger bucket on exhaustion: its retention budget and
        // speculative capacity remain independent of small-reply pressure.
        return _buckets[index].TryPop(out var array) ? array : new T[16 << index];
    }

    public override void Return(T[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (array.Length == 0) return;
        if (array.Length <= _maximumLength
            && (array.Length < 16 || !BitOperations.IsPow2(array.Length)))
            throw new ArgumentException("The array does not match a response pool bucket.", nameof(array));
        // Preserve the caller's clearing request even when an over-maximum array
        // or a full bucket cannot retain this return. Used RespValue slots are
        // otherwise cleared by their existing parser/root owners before return.
        if (clearArray) Array.Clear(array);
        if (array.Length <= _maximumLength) _buckets[BucketIndex(array.Length)].TryPush(array);
    }

    private static int BucketIndex(int length)
        => BitOperations.Log2((uint)Math.Max(length - 1, 15)) - 3;
}
