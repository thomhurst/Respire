using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Networking;

namespace Respire.Protocol;

/// <summary>
/// A RESP2/RESP3 value as a discriminated-union struct. String-like payloads reference either
/// caller-supplied memory or a pooled buffer copied off the wire; aggregate values (array, map,
/// set, push) reference an element array that may also be pooled.
/// </summary>
/// <remarks>
/// Values produced by the connection own pooled storage: call <see cref="Dispose"/> when done
/// to return buffers to the pools. Forgetting to dispose is safe — the buffers are simply
/// collected by the GC instead of being reused.
/// </remarks>
internal readonly struct RespValue : IEquatable<RespValue>, IDisposable
{
    [Flags]
    internal enum ValueFlags : byte
    {
        None = 0,
        PooledPayload = 1,
        PooledElements = 2,
    }

    private readonly RespDataType _type;
    private readonly ValueFlags _flags;
    private readonly long _integerValue;
    private readonly ReadOnlyMemory<byte> _payload;
    private readonly RespValue[]? _elements;
    private readonly int _elementCount;

    public RespDataType Type => _type;
    public bool IsNull => _type == RespDataType.Null;
    public bool IsError => _type is RespDataType.Error or RespDataType.BulkError;

    private RespValue(RespDataType type, ValueFlags flags = ValueFlags.None, long integerValue = 0,
        ReadOnlyMemory<byte> payload = default, RespValue[]? elements = null, int elementCount = 0)
    {
        _type = type;
        _flags = flags;
        _integerValue = integerValue;
        _payload = payload;
        _elements = elements;
        _elementCount = elementCount;
    }

    public static readonly RespValue Null = new(RespDataType.Null);
    public static readonly RespValue True = new(RespDataType.Boolean, integerValue: 1);
    public static readonly RespValue False = new(RespDataType.Boolean, integerValue: 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Integer(long value) => new(RespDataType.Integer, integerValue: value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Boolean(bool value) => value ? True : False;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Double(double value)
        => new(RespDataType.Double, integerValue: BitConverter.DoubleToInt64Bits(value));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue SimpleString(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.SimpleString, payload: buffer.Slice(startIndex, length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue SimpleString(ReadOnlyMemory<byte> buffer)
        => new(RespDataType.SimpleString, payload: buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue BulkString(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.BulkString, payload: buffer.Slice(startIndex, length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue BulkString(ReadOnlyMemory<byte> buffer)
        => new(RespDataType.BulkString, payload: buffer);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Error(ReadOnlyMemory<byte> buffer, int startIndex, int length)
        => new(RespDataType.Error, payload: buffer.Slice(startIndex, length));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RespValue Error(ReadOnlyMemory<byte> buffer)
        => new(RespDataType.Error, payload: buffer);

    public static RespValue SimpleString(string value) => CreateStringValue(value, RespDataType.SimpleString);

    public static RespValue BulkString(string value) => CreateStringValue(value, RespDataType.BulkString);

    public static RespValue Error(string value) => CreateStringValue(value, RespDataType.Error);

    private static RespValue CreateStringValue(string value, RespDataType type)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new RespValue(type);
        }

        return new RespValue(type, payload: Encoding.UTF8.GetBytes(value));
    }

    public static RespValue Array(params RespValue[] values)
        => new(RespDataType.Array, elements: values, elementCount: values.Length);

    public static RespValue Array(ReadOnlySpan<RespValue> values)
        => new(RespDataType.Array, elements: values.ToArray(), elementCount: values.Length);

    /// <summary>Wire-path factory: payload lives in an array rented from <see cref="RespirePools.ResponsePayloads"/>.</summary>
    internal static RespValue PooledString(RespDataType type, byte[] pooledArray, int length)
        => new(type, ValueFlags.PooledPayload, payload: new ReadOnlyMemory<byte>(pooledArray, 0, length));

    /// <summary>Wire-path factory: elements live in an array rented from <see cref="RespirePools.ValueArrays"/>.</summary>
    internal static RespValue PooledAggregate(RespDataType type, RespValue[] pooledElements, int count)
        => new(type, ValueFlags.PooledElements, elements: pooledElements, elementCount: count);

    public long AsInteger() => _type == RespDataType.Integer ? _integerValue : 0;

    public bool AsBoolean() => _type == RespDataType.Boolean && _integerValue != 0;

    public double AsDouble() => _type == RespDataType.Double ? BitConverter.Int64BitsToDouble(_integerValue) : 0.0;

    /// <summary>The value's element list for aggregate types (map pairs are flattened key,value,key,value).</summary>
    public ReadOnlySpan<RespValue> AsArray()
        => _elements is null ? default : _elements.AsSpan(0, _elementCount);

    public ReadOnlySpan<byte> AsSpan()
    {
        switch (_type)
        {
            case RespDataType.SimpleString:
            case RespDataType.BulkString:
            case RespDataType.Error:
            case RespDataType.BulkError:
            case RespDataType.BigNumber:
                return _payload.Span;
            case RespDataType.VerbatimString:
                // RESP3 verbatim strings carry a 3-char format prefix and a colon ("txt:...").
                var span = _payload.Span;
                return span.Length >= 4 ? span[4..] : span;
            default:
                return default;
        }
    }

    public string AsString()
    {
        return _type switch
        {
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.BigNumber =>
                _payload.IsEmpty ? string.Empty : Encoding.UTF8.GetString(_payload.Span),
            RespDataType.VerbatimString => DecodeVerbatimString(),
            RespDataType.Integer => _integerValue.ToString(),
            RespDataType.Boolean => AsBoolean().ToString(),
            RespDataType.Double => AsDouble().ToString(),
            _ => string.Empty,
        };
    }

    private string DecodeVerbatimString()
    {
        var span = _payload.Span;
        if (span.Length >= 4)
        {
            span = span[4..];
        }

        return span.IsEmpty ? string.Empty : Encoding.UTF8.GetString(span);
    }

    public string GetErrorMessage()
        => IsError ? Encoding.UTF8.GetString(AsSpan()) : string.Empty;

    /// <summary>
    /// If this value is a RESP error reply, disposes it and throws
    /// <see cref="RespireServerException"/> with the server's message.
    /// </summary>
    public void ThrowIfError()
    {
        if (IsError)
        {
            var message = GetErrorMessage();
            Dispose();
            throw new RespireServerException(message);
        }
    }

    /// <summary>
    /// Returns any pooled storage backing this value (recursively for aggregates). Safe to
    /// call multiple times only on distinct copies' first use — treat the value as invalid
    /// afterwards.
    /// </summary>
    public void Dispose()
    {
        if ((_flags & ValueFlags.PooledPayload) != 0
            && _payload.Length > 0
            && MemoryMarshal.TryGetArray(_payload, out var segment)
            && segment.Array is { Length: > 0 } array)
        {
            RespirePools.ResponsePayloads.Return(array);
        }

        if (_elements is not null)
        {
            for (var i = 0; i < _elementCount; i++)
            {
                _elements[i].Dispose();
            }

            if ((_flags & ValueFlags.PooledElements) != 0 && _elements.Length > 0)
            {
                RespirePools.ValueArrays.Return(_elements, clearArray: true);
            }
        }
    }

    public override string ToString()
    {
        return _type switch
        {
            RespDataType.Null => "null",
            RespDataType.Boolean => AsBoolean().ToString(),
            RespDataType.Integer => _integerValue.ToString(),
            RespDataType.Double => AsDouble().ToString(),
            RespDataType.Array or RespDataType.Map or RespDataType.Set or RespDataType.Push
                => $"[{_type}({_elementCount})]",
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.Error
                or RespDataType.BulkError or RespDataType.VerbatimString or RespDataType.BigNumber
                => Encoding.UTF8.GetString(AsSpan()),
            _ => $"Unknown({_type})",
        };
    }

    public bool Equals(RespValue other)
    {
        if (_type != other._type)
        {
            return false;
        }

        switch (_type)
        {
            case RespDataType.Null:
                return true;
            case RespDataType.Boolean:
            case RespDataType.Integer:
            case RespDataType.Double:
                return _integerValue == other._integerValue;
            case RespDataType.Array:
            case RespDataType.Map:
            case RespDataType.Set:
            case RespDataType.Push:
                if (_elementCount != other._elementCount)
                {
                    return false;
                }

                for (var i = 0; i < _elementCount; i++)
                {
                    if (!_elements![i].Equals(other._elements![i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return AsSpan().SequenceEqual(other.AsSpan());
        }
    }

    public override bool Equals(object? obj) => obj is RespValue other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_type, _integerValue, _payload.Length, _elementCount);
}
