using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Keva.Core.Protocol;

/// <summary>
/// Zero-allocation RESP value using discriminated union pattern
/// </summary>
[StructLayout(LayoutKind.Explicit)]
public readonly struct ZeroAllocRespValue : IEquatable<ZeroAllocRespValue>
{
    [FieldOffset(0)]
    private readonly RespDataType _type;
    
    [FieldOffset(4)]
    private readonly long _integerValue;
    
    [FieldOffset(4)]
    private readonly bool _booleanValue;
    
    [FieldOffset(12)]
    private readonly int _startIndex;
    
    [FieldOffset(16)]
    private readonly int _length;
    
    // Reference to the original buffer for string/bulk string values
    [FieldOffset(24)]
    private readonly ReadOnlyMemory<byte> _bufferRef;
    
    public RespDataType Type => _type;
    public bool IsNull => _type == RespDataType.Null;
    public bool IsError => _type == RespDataType.Error;
    
    private ZeroAllocRespValue(RespDataType type, long integerValue = 0, bool booleanValue = false, 
                               ReadOnlyMemory<byte> bufferRef = default, int startIndex = 0, int length = 0)
    {
        _type = type;
        _integerValue = integerValue;
        _booleanValue = booleanValue;
        _bufferRef = bufferRef;
        _startIndex = startIndex;
        _length = length;
    }
    
    // Static factory methods
    public static readonly ZeroAllocRespValue Null = new(RespDataType.Null);
    public static readonly ZeroAllocRespValue True = new(RespDataType.Boolean, booleanValue: true);
    public static readonly ZeroAllocRespValue False = new(RespDataType.Boolean, booleanValue: false);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ZeroAllocRespValue Integer(long value) => new(RespDataType.Integer, integerValue: value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ZeroAllocRespValue SimpleString(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.SimpleString, bufferRef: buffer, startIndex: startIndex, length: length);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ZeroAllocRespValue BulkString(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.BulkString, bufferRef: buffer, startIndex: startIndex, length: length);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ZeroAllocRespValue Error(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.Error, bufferRef: buffer, startIndex: startIndex, length: length);
    
    public long AsInteger() => _type == RespDataType.Integer ? _integerValue : 0;
    
    public bool AsBoolean() => _type == RespDataType.Boolean ? _booleanValue : false;
    
    public ReadOnlySpan<byte> AsSpan()
    {
        return _type switch
        {
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.Error 
                => _bufferRef.Span.Slice(_startIndex, _length),
            _ => ReadOnlySpan<byte>.Empty
        };
    }
    
    public override string ToString()
    {
        return _type switch
        {
            RespDataType.Null => "null",
            RespDataType.Boolean => _booleanValue.ToString(),
            RespDataType.Integer => _integerValue.ToString(),
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.Error 
                => System.Text.Encoding.UTF8.GetString(AsSpan()),
            _ => $"Unknown({_type})"
        };
    }
    
    public bool Equals(ZeroAllocRespValue other)
    {
        if (_type != other._type)
        {
            return false;
        }

        return _type switch
        {
            RespDataType.Null => true,
            RespDataType.Boolean => _booleanValue == other._booleanValue,
            RespDataType.Integer => _integerValue == other._integerValue,
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.Error 
                => AsSpan().SequenceEqual(other.AsSpan()),
            _ => false
        };
    }
    
    public override bool Equals(object? obj) => obj is ZeroAllocRespValue other && Equals(other);
    
    public override int GetHashCode() => HashCode.Combine(_type, _integerValue, _startIndex, _length);
}