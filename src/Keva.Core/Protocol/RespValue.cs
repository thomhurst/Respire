using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Keva.Core.Protocol;

[StructLayout(LayoutKind.Auto)]
public readonly struct RespValue : IEquatable<RespValue>
{
    private readonly object? _value;
    private readonly RespDataType _type;
    
    public RespDataType Type => _type;
    
    public bool IsNull => _type == RespDataType.Null || (_type == RespDataType.BulkString && _value == null);
    public bool IsError => _type == RespDataType.Error || _type == RespDataType.BulkError;
    
    private RespValue(RespDataType type, object? value)
    {
        _type = type;
        _value = value;
    }
    
    // Static factory methods for zero-allocation construction
    public static readonly RespValue Null = new(RespDataType.Null, null);
    public static readonly RespValue True = new(RespDataType.Boolean, true);
    public static readonly RespValue False = new(RespDataType.Boolean, false);
    public static readonly RespValue EmptyArray = new(RespDataType.Array, System.Array.Empty<RespValue>());
    public static readonly RespValue EmptyBulkString = new(RespDataType.BulkString, ReadOnlyMemory<byte>.Empty);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue SimpleString(string value) => new(RespDataType.SimpleString, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue SimpleString(ReadOnlyMemory<byte> value) => new(RespDataType.SimpleString, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Error(string message) => new(RespDataType.Error, message);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Error(ReadOnlyMemory<byte> message) => new(RespDataType.Error, message);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Integer(long value) => new(RespDataType.Integer, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue BulkString(ReadOnlyMemory<byte> value) => new(RespDataType.BulkString, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue BulkString(string? value)
    {
        if (value == null)
        {
            return Null;
        }
        return new(RespDataType.BulkString, Encoding.UTF8.GetBytes(value));
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Array(params RespValue[] values) => new(RespDataType.Array, values);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Array(ReadOnlyMemory<RespValue> values) => new(RespDataType.Array, values);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Map(ReadOnlyMemory<RespValue> values) => new(RespDataType.Map, values);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Set(ReadOnlyMemory<RespValue> values) => new(RespDataType.Set, values);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Double(double value) => new(RespDataType.Double, value);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Boolean(bool value) => value ? True : False;
    
    // Type-safe accessors
    public string AsString()
    {
        return _type switch
        {
            RespDataType.SimpleString when _value is string str => str,
            RespDataType.SimpleString when _value is ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            RespDataType.SimpleString when _value is byte[] arr => Encoding.UTF8.GetString(arr),
            RespDataType.Error when _value is string err => err,
            RespDataType.Error when _value is ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            RespDataType.BulkString when _value is ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
            RespDataType.BulkString when _value is byte[] arr => Encoding.UTF8.GetString(arr),
            _ => throw new InvalidOperationException($"Cannot convert {_type} to string")
        };
    }
    
    public ReadOnlyMemory<byte> AsBytes()
    {
        return _type switch
        {
            RespDataType.SimpleString when _value is ReadOnlyMemory<byte> bytes => bytes,
            RespDataType.SimpleString when _value is byte[] arr => arr,
            RespDataType.SimpleString when _value is string str => Encoding.UTF8.GetBytes(str),
            RespDataType.BulkString when _value is ReadOnlyMemory<byte> bytes => bytes,
            RespDataType.BulkString when _value is byte[] arr => arr,
            RespDataType.Error when _value is ReadOnlyMemory<byte> bytes => bytes,
            RespDataType.Error when _value is byte[] arr => arr,
            _ => throw new InvalidOperationException($"Cannot convert {_type} to bytes")
        };
    }
    
    public long AsInteger()
    {
        return _type switch
        {
            RespDataType.Integer when _value is long l => l,
            RespDataType.Integer when _value is int i => i,
            _ => throw new InvalidOperationException($"Cannot convert {_type} to integer")
        };
    }
    
    public double AsDouble()
    {
        return _type switch
        {
            RespDataType.Double when _value is double d => d,
            RespDataType.Integer when _value is long l => l,
            _ => throw new InvalidOperationException($"Cannot convert {_type} to double")
        };
    }
    
    public bool AsBoolean()
    {
        return _type switch
        {
            RespDataType.Boolean when _value is bool b => b,
            RespDataType.Integer when _value is long l => l != 0,
            _ => throw new InvalidOperationException($"Cannot convert {_type} to boolean")
        };
    }
    
    public ReadOnlyMemory<RespValue> AsArray()
    {
        return _type switch
        {
            RespDataType.Array when _value is RespValue[] arr => arr,
            RespDataType.Array when _value is ReadOnlyMemory<RespValue> mem => mem,
            RespDataType.Set when _value is ReadOnlyMemory<RespValue> mem => mem,
            RespDataType.Map when _value is ReadOnlyMemory<RespValue> mem => mem,
            _ => throw new InvalidOperationException($"Cannot convert {_type} to array")
        };
    }
    
    public string GetErrorMessage()
    {
        if (!IsError)
        {
            throw new InvalidOperationException("Not an error value");
        }
        return AsString();
    }
    
    public bool Equals(RespValue other)
    {
        if (_type != other._type)
        {
            return false;
        }

        return _type switch
        {
            RespDataType.Null => true,
            RespDataType.Boolean or RespDataType.Integer or RespDataType.Double => Equals(_value, other._value),
            RespDataType.SimpleString or RespDataType.Error or RespDataType.BulkString => 
                AsBytes().Span.SequenceEqual(other.AsBytes().Span),
            RespDataType.Array or RespDataType.Set or RespDataType.Map => 
                AsArray().Span.SequenceEqual(other.AsArray().Span),
            _ => Equals(_value, other._value)
        };
    }
    
    public override bool Equals(object? obj) => obj is RespValue other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(_type, _value);
    
    public static bool operator ==(RespValue left, RespValue right) => left.Equals(right);
    public static bool operator !=(RespValue left, RespValue right) => !left.Equals(right);
}