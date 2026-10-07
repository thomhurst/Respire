using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Respire.Internal;
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
/// Complete aggregate string children can borrow slices of the root's payload. Keep the root alive
/// while reading children; use <see cref="ToOwned"/> to retain a child beyond the root's disposal.
/// Fragmented replies may own separate child buffers. The same root lifetime contract applies
/// to both parse paths; callers must not depend on an individual child's storage ownership.
/// </remarks>
internal readonly struct RespValue : IEquatable<RespValue>, IDisposable
{
    [Flags]
    internal enum ValueFlags : byte
    {
        None = 0,
        PooledPayload = 1,
        PooledElements = 2,
        TransactionStateCleared = 4,
        DeferredPayload = 8,
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

    // Retained queue errors carry EXEC's confirmed outcome without changing payload ownership.
    internal bool TransactionStateCleared => (_flags & ValueFlags.TransactionStateCleared) != 0;

    internal RespValue WithTransactionStateCleared()
        => new(_type, _flags | ValueFlags.TransactionStateCleared, _integerValue, _payload, _elements, _elementCount);

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

    /// <summary>Parser-local wire offsets; never exposed until the complete root owns their payload.</summary>
    internal static RespValue DeferredString(RespDataType type, int offset, int length)
        => new(type, ValueFlags.DeferredPayload, integerValue: offset, elementCount: length);

    [Conditional("DEBUG")]
    internal void AssertMaterialized()
    {
        Debug.Assert((_flags & ValueFlags.DeferredPayload) == 0,
            "Parser-local payload offsets must be materialized before returning a complete value.");
        if (_elements is null) return;
        for (var i = 0; i < _elementCount; i++)
            _elements[i].AssertMaterialized();
    }

    /// <summary>Copies deferred aggregate payloads into shared or per-child storage.</summary>
    internal RespValue CopyDeferredPayloads(ReadOnlySpan<byte> buffer, int start, int length, int payloadBytes)
    {
        // Top-level attributes can precede a scalar. Keep that scalar's ordinary payload ownership.
        if ((_flags & ValueFlags.DeferredPayload) != 0)
            return RespParser.CopyToPooled(_type, buffer.Slice((int)_integerValue, _elementCount));
        if (length > RespirePools.MaxPooledResponsePayloadLength
            || (length >= 4096 && payloadBytes < length / 8))
        {
            // Avoid unpooled frames and large copies dominated by discarded attributes,
            // integer tokens or framing. Small replies retain the single-copy path.
            CopyDeferredChildren(buffer);
            return this;
        }
        var array = RespirePools.ResponsePayloads.Rent(length);
        buffer.Slice(start, length).CopyTo(array);
        var frame = new ReadOnlyMemory<byte>(array, 0, length);
        BindDeferredPayloads(frame, start);
        return new(_type, _flags | ValueFlags.PooledPayload, _integerValue, frame, _elements, _elementCount);
    }

    private void CopyDeferredChildren(ReadOnlySpan<byte> buffer)
    {
        // Struct copies share _elements, so replacing children updates the original tree.
        if (_elements is null) return;
        for (var i = 0; i < _elementCount; i++)
        {
            var child = _elements[i];
            if ((child._flags & ValueFlags.DeferredPayload) != 0)
                _elements[i] = RespParser.CopyToPooled(child._type, buffer.Slice((int)child._integerValue, child._elementCount));
            else child.CopyDeferredChildren(buffer);
        }
    }

    private void BindDeferredPayloads(ReadOnlyMemory<byte> frame, int start)
    {
        if (_elements is null) return;
        for (var i = 0; i < _elementCount; i++)
        {
            var child = _elements[i];
            if ((child._flags & ValueFlags.DeferredPayload) != 0)
                _elements[i] = new(child._type, payload: frame.Slice((int)child._integerValue - start, child._elementCount));
            else child.BindDeferredPayloads(frame, start);
        }
    }

    /// <summary>Deep-copies this value into GC-owned storage with no pooled-buffer ownership.</summary>
    internal RespValue ToOwned()
    {
        if (_elements is not null)
        {
            var elements = new RespValue[_elementCount];
            for (var i = 0; i < _elementCount; i++)
            {
                elements[i] = _elements[i].ToOwned();
            }

            return new RespValue(_type, elements: elements, elementCount: elements.Length);
        }

        if (!_payload.IsEmpty)
        {
            return new RespValue(_type, payload: _payload.ToArray());
        }

        return new RespValue(_type, integerValue: _integerValue);
    }

    /// <summary>Approximate bytes retained by an owned copy of this value.</summary>
    internal long GetOwnedSize()
    {
        if (_elements is null)
        {
            return 32L + _payload.Length;
        }

        // ToOwned copies children, not the aggregate's shared wire frame.
        long size = 32;
        for (var index = 0; index < _elementCount; index++)
        {
            size += _elements[index].GetOwnedSize();
        }

        return size;
    }

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

    internal ReadOnlyMemory<byte> AsMemory()
        => _type is RespDataType.SimpleString or RespDataType.BulkString
            or RespDataType.Error or RespDataType.BulkError or RespDataType.BigNumber
            ? _payload
            : default;

    public string AsString()
    {
        return _type switch
        {
            RespDataType.SimpleString or RespDataType.BulkString or RespDataType.BigNumber =>
                Utf8String.GetString(_payload),
            RespDataType.VerbatimString => DecodeVerbatimString(),
            RespDataType.Integer => _integerValue.ToString(),
            RespDataType.Boolean => AsBoolean().ToString(),
            RespDataType.Double => AsDouble().ToString(),
            _ => string.Empty,
        };
    }

    private string DecodeVerbatimString()
    {
        var payload = _payload;
        if (payload.Length >= 4)
        {
            payload = payload[4..];
        }

        return Utf8String.GetString(payload);
    }

    public string GetErrorMessage()
        => IsError ? Utf8String.GetString(_payload) : string.Empty;

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
#if DEBUG
            // Expose invalid borrowed-child reads during development without release-path work.
            if (_elements is not null)
                array.AsSpan(segment.Offset, _payload.Length).Fill(0xDD);
#endif
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
                System.Array.Clear(_elements, 0, _elementCount);
                RespirePools.ValueArrays.Return(_elements);
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
                => Utf8String.GetString(AsSpan()),
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

    public override int GetHashCode() => HashCode.Combine(_type, _integerValue,
        _elements is null ? _payload.Length : 0, _elementCount);
}
