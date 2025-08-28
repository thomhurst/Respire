using System.Buffers;
using System.Runtime.CompilerServices;

namespace Respire.Infrastructure;

/// <summary>
/// High-performance memory pool for Respire operations
/// Provides pooled memory management to reduce GC pressure
/// </summary>
public sealed class RespireMemoryPool : IDisposable
{
    private readonly MemoryPool<byte> _memoryPool;
    private readonly ArrayPool<byte> _smallArrayPool;
    private readonly ArrayPool<byte> _largeArrayPool;
    private volatile bool _disposed;
    
    // Common buffer sizes for Redis operations
    public const int SmallBufferSize = 1024;      // 1KB for most commands
    public const int MediumBufferSize = 4096;     // 4KB for larger commands
    public const int LargeBufferSize = 16384;     // 16KB for bulk operations
    public const int HighPerformanceBufferSize = 131072; // 128KB for high-performance scenarios
    
    public static readonly RespireMemoryPool Shared = new();
    
    private RespireMemoryPool()
    {
        _memoryPool = MemoryPool<byte>.Shared;
        _smallArrayPool = ArrayPool<byte>.Create(maxArrayLength: SmallBufferSize, maxArraysPerBucket: 50);
        _largeArrayPool = ArrayPool<byte>.Create(maxArrayLength: LargeBufferSize, maxArraysPerBucket: 20);
    }
    
    /// <summary>
    /// Rents a memory buffer from the pool
    /// </summary>
    /// <param name="minimumLength">Minimum required buffer size</param>
    /// <returns>Pooled memory that must be disposed when done</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public IMemoryOwner<byte> RentMemory(int minimumLength = SmallBufferSize)
    {
        ThrowIfDisposed();
        return _memoryPool.Rent(minimumLength);
    }
    
    /// <summary>
    /// Rents a byte array from the appropriate pool
    /// </summary>
    /// <param name="minimumLength">Minimum required array size</param>
    /// <returns>Pooled array that must be returned via ReturnArray</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte[] RentArray(int minimumLength)
    {
        ThrowIfDisposed();
        
        return minimumLength <= SmallBufferSize 
            ? _smallArrayPool.Rent(minimumLength)
            : _largeArrayPool.Rent(minimumLength);
    }
    
    /// <summary>
    /// Returns a previously rented array to the pool
    /// </summary>
    /// <param name="array">Array to return</param>
    /// <param name="clearArray">Whether to clear the array contents</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ReturnArray(byte[] array, bool clearArray = false)
    {
        if (_disposed || array == null) return;
        
        if (array.Length <= SmallBufferSize)
        {
            _smallArrayPool.Return(array, clearArray);
        }
        else
        {
            _largeArrayPool.Return(array, clearArray);
        }
    }
    
    /// <summary>
    /// Creates a pooled buffer writer for efficient sequential writing
    /// </summary>
    /// <param name="initialCapacity">Initial buffer capacity</param>
    /// <returns>Pooled buffer writer</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public PooledBufferWriter CreateBufferWriter(int initialCapacity = SmallBufferSize)
    {
        ThrowIfDisposed();
        return new PooledBufferWriter(this, initialCapacity);
    }
    
    /// <summary>
    /// Allocates a pinned high-performance buffer for zero-copy operations (inspired by RespireDirectClient)
    /// Use this for maximum performance scenarios where GC pressure must be minimized
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte[] GetPinnedBuffer(int size = HighPerformanceBufferSize)
    {
        return GC.AllocateUninitializedArray<byte>(size, pinned: true);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RespireMemoryPool));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        
        // Note: We don't dispose the shared memory pool as it's shared
        // The array pools will be cleaned up by the GC
    }
}

/// <summary>
/// Pooled buffer writer that efficiently manages memory allocation
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly RespireMemoryPool _pool;
    private byte[] _buffer;
    private int _position;
    private bool _disposed;
    
    internal PooledBufferWriter(RespireMemoryPool pool, int initialCapacity)
    {
        _pool = pool;
        _buffer = pool.RentArray(initialCapacity);
        _position = 0;
    }
    
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _position);
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _position);
    public int WrittenCount => _position;
    public int Capacity => _buffer.Length;
    public int FreeCapacity => _buffer.Length - _position;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int count)
    {
        if (count < 0 || count > FreeCapacity)
            throw new ArgumentOutOfRangeException(nameof(count));
        
        _position += count;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsMemory(_position);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        CheckAndResizeBuffer(sizeHint);
        return _buffer.AsSpan(_position);
    }
    
    /// <summary>
    /// Writes pre-compiled command bytes directly to the buffer
    /// </summary>
    /// <param name="commandBytes">Pre-compiled command bytes to write</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WritePreCompiledCommand(ReadOnlySpan<byte> commandBytes)
    {
        CheckAndResizeBuffer(commandBytes.Length);
        commandBytes.CopyTo(_buffer.AsSpan(_position));
        _position += commandBytes.Length;
    }
    
    /// <summary>
    /// Writes a bulk string directly to the buffer
    /// </summary>
    /// <param name="value">String value to write</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteBulkString(string value)
    {
        var encodedLength = System.Text.Encoding.UTF8.GetByteCount(value);
        var totalLength = 1 + GetIntegerStringLength(encodedLength) + 2 + encodedLength + 2; // $<len>\r\n<data>\r\n
        
        CheckAndResizeBuffer(totalLength);
        var span = _buffer.AsSpan(_position);
        
        var written = 0;
        span[written++] = (byte)'$';
        written += WriteInteger(span[written..], encodedLength);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        written += System.Text.Encoding.UTF8.GetBytes(value, span[written..]);
        span[written++] = (byte)'\r';
        span[written++] = (byte)'\n';
        
        _position += written;
    }
    
    /// <summary>
    /// Clears the buffer for reuse
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear()
    {
        _position = 0;
    }
    
    private void CheckAndResizeBuffer(int sizeHint)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PooledBufferWriter));
        
        if (sizeHint <= FreeCapacity) return;
        
        var newSize = Math.Max(_buffer.Length * 2, _position + sizeHint);
        var newBuffer = _pool.RentArray(newSize);
        
        if (_position > 0)
        {
            _buffer.AsSpan(0, _position).CopyTo(newBuffer);
        }
        
        _pool.ReturnArray(_buffer);
        _buffer = newBuffer;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteInteger(Span<byte> buffer, int value)
    {
        if (value == 0)
        {
            buffer[0] = (byte)'0';
            return 1;
        }
        
        var written = 0;
        if (value < 0)
        {
            buffer[written++] = (byte)'-';
            value = -value;
        }
        
        var digits = GetIntegerStringLength(value);
        var start = written;
        written += digits;
        
        for (int i = written - 1; i >= start; i--)
        {
            buffer[i] = (byte)('0' + (value % 10));
            value /= 10;
        }
        
        return written;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetIntegerStringLength(int value)
    {
        if (value == 0) return 1;
        if (value < 0) return GetIntegerStringLength(-value) + 1;
        
        return value switch
        {
            < 10 => 1,
            < 100 => 2,
            < 1000 => 3,
            < 10000 => 4,
            < 100000 => 5,
            < 1000000 => 6,
            < 10000000 => 7,
            < 100000000 => 8,
            < 1000000000 => 9,
            _ => 10
        };
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        if (_buffer != null)
        {
            _pool.ReturnArray(_buffer);
            _buffer = null!;
        }
    }
}