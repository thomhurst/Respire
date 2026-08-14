using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A command argument. Implicitly convertible from strings, byte payloads, integers, doubles,
/// and booleans, so command methods take one parameter type instead of an overload matrix.
/// Input-only: command results come back as plain .NET types (<c>string?</c>, <c>long</c>, …),
/// never as this struct.
/// </summary>
public readonly struct RespireValue : IEquatable<RespireValue>
{
    private const int StackallocThreshold = 256;

    private enum Kind : byte
    {
        Null = 0,
        String,
        Bytes,
        Integer,
        UnsignedInteger,
        Single,
        Double,
        Boolean,
    }

    private readonly Kind _kind;
    private readonly string? _string;
    private readonly ReadOnlyMemory<byte> _bytes;
    private readonly long _number;

    private RespireValue(Kind kind, string? s = null, ReadOnlyMemory<byte> bytes = default, long number = 0)
    {
        _kind = kind;
        _string = s;
        _bytes = bytes;
        _number = number;
    }

    /// <summary>Creates a UTF-8 command argument from text.</summary>
    public RespireValue(string value) : this(Kind.String, s: value ?? throw new ArgumentNullException(nameof(value)))
    {
    }

    /// <summary>Creates a binary-safe command argument.</summary>
    public RespireValue(ReadOnlyMemory<byte> value) : this(Kind.Bytes, bytes: value)
    {
    }

    /// <summary>
    /// Represents an absent optional argument. A null value cannot be serialized onto the Redis wire.
    /// </summary>
    public static readonly RespireValue Null = default;

    /// <summary>Whether this is the absent <see cref="Null"/> sentinel.</summary>
    public bool IsNull => _kind == Kind.Null;

    internal static void ThrowIfNull(RespireValue value, string paramName)
    {
        if (value.IsNull)
        {
            throw new ArgumentNullException(
                paramName,
                "A null value cannot be sent as a Redis argument; use an empty string or delete the key.");
        }
    }

    /// <summary>Converts text or null to a command argument.</summary>
    public static implicit operator RespireValue(string? value)
        => value is null ? Null : new RespireValue(value);

    /// <summary>Converts a byte array or null to a binary-safe command argument.</summary>
    public static implicit operator RespireValue(byte[]? value)
        => value is null ? Null : new RespireValue(value.AsMemory());

    /// <summary>Converts read-only bytes to a binary-safe command argument.</summary>
    public static implicit operator RespireValue(ReadOnlyMemory<byte> value) => new(value);

    /// <summary>Converts bytes to a binary-safe command argument.</summary>
    public static implicit operator RespireValue(Memory<byte> value) => new(value);

    /// <summary>Converts an array segment to a binary-safe command argument.</summary>
    public static implicit operator RespireValue(ArraySegment<byte> value) => new(value.AsMemory());

    /// <summary>Converts a key without changing its UTF-8 or binary representation.</summary>
    public static implicit operator RespireValue(RespireKey value) => value.AsValue();

    /// <summary>Converts a GUID using the invariant 36-character <c>D</c> format.</summary>
    public static implicit operator RespireValue(Guid value)
        => new(value.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>Converts an instant using the invariant round-trip <c>O</c> format.</summary>
    public static implicit operator RespireValue(DateTimeOffset value)
        => new(value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Converts a duration using the invariant constant <c>c</c> format.</summary>
    public static implicit operator RespireValue(TimeSpan value)
        => new(value.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>Converts one UTF-16 code unit to a one-character string.</summary>
    public static implicit operator RespireValue(char value)
    {
        if (char.IsSurrogate(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, "An isolated UTF-16 surrogate cannot be encoded as UTF-8.");
        }

        return new RespireValue(value.ToString());
    }

    /// <summary>Converts a signed 64-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(long value) => new(Kind.Integer, number: value);

    /// <summary>Converts an unsigned byte to a command argument.</summary>
    public static implicit operator RespireValue(byte value) => new(Kind.Integer, number: value);

    /// <summary>Converts a signed byte to a command argument.</summary>
    public static implicit operator RespireValue(sbyte value) => new(Kind.Integer, number: value);

    /// <summary>Converts a signed 16-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(short value) => new(Kind.Integer, number: value);

    /// <summary>Converts an unsigned 16-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(ushort value) => new(Kind.Integer, number: value);

    /// <summary>Converts a signed 32-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(int value) => new(Kind.Integer, number: value);

    /// <summary>Converts an unsigned 32-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(uint value) => new(Kind.Integer, number: value);

    /// <summary>Converts an unsigned 64-bit integer to a command argument.</summary>
    public static implicit operator RespireValue(ulong value)
        => new(Kind.UnsignedInteger, number: unchecked((long)value));

    /// <summary>Converts a single-precision number using invariant formatting.</summary>
    public static implicit operator RespireValue(float value)
        => new(Kind.Single, number: BitConverter.SingleToInt32Bits(value));

    /// <summary>Converts a double-precision number using invariant formatting.</summary>
    public static implicit operator RespireValue(double value)
        => new(Kind.Double, number: BitConverter.DoubleToInt64Bits(value));

    /// <summary>Converts a decimal number using invariant formatting.</summary>
    public static implicit operator RespireValue(decimal value)
        => new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Converts a boolean to Redis <c>1</c> or <c>0</c>.</summary>
    public static implicit operator RespireValue(bool value)
        => new(Kind.Boolean, number: value ? 1 : 0);

    /// <summary>Tests two command arguments for value equality.</summary>
    public static bool operator ==(RespireValue left, RespireValue right) => left.Equals(right);

    /// <summary>Tests two command arguments for value inequality.</summary>
    public static bool operator !=(RespireValue left, RespireValue right) => !left.Equals(right);

    /// <summary>Serializes this argument as one RESP bulk string.</summary>
    internal void WriteTo(ref RespWriter writer)
    {
        switch (_kind)
        {
            case Kind.Null:
                throw new ArgumentException(
                    "A null value cannot be sent as a Redis argument; use an empty string or delete the key.");
            case Kind.String:
                writer.WriteBulkString(_string!);
                break;
            case Kind.Bytes:
                writer.WriteBulkString(_bytes.Span);
                break;
            case Kind.Integer:
                writer.WriteBulkInteger(_number);
                break;
            case Kind.UnsignedInteger:
                Span<byte> unsignedDigits = stackalloc byte[20];
                Utf8Formatter.TryFormat(unchecked((ulong)_number), unsignedDigits, out var unsignedWritten);
                writer.WriteBulkString(unsignedDigits[..unsignedWritten]);
                break;
            case Kind.Single:
                Span<byte> singleDigits = stackalloc byte[16];
                Utf8Formatter.TryFormat(BitConverter.Int32BitsToSingle((int)_number), singleDigits, out var singleWritten);
                writer.WriteBulkString(singleDigits[..singleWritten]);
                break;
            case Kind.Double:
                Span<byte> digits = stackalloc byte[32];
                Utf8Formatter.TryFormat(BitConverter.Int64BitsToDouble(_number), digits, out var written);
                writer.WriteBulkString(digits[..written]);
                break;
            case Kind.Boolean:
                writer.WriteBulkString(_number != 0 ? "1"u8 : "0"u8);
                break;
            default:
                throw new InvalidOperationException($"Unsupported RespireValue kind '{_kind}'.");
        }
    }

    internal bool TryGetClusterSlot(out int slot)
    {
        if (_kind == Kind.String)
        {
            slot = ClusterHash.GetSlot(_string!);
            return true;
        }

        if (_kind == Kind.Bytes)
        {
            slot = ClusterHash.GetSlot(_bytes.Span);
            return true;
        }

        slot = GetScalarClusterSlot();
        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int GetScalarClusterSlot()
    {
        switch (_kind)
        {
            case Kind.Integer:
                Span<byte> integerDigits = stackalloc byte[20];
                Utf8Formatter.TryFormat(_number, integerDigits, out var integerWritten);
                return ClusterHash.GetSlot(integerDigits[..integerWritten]);
            case Kind.UnsignedInteger:
                Span<byte> unsignedDigits = stackalloc byte[20];
                Utf8Formatter.TryFormat(unchecked((ulong)_number), unsignedDigits, out var unsignedWritten);
                return ClusterHash.GetSlot(unsignedDigits[..unsignedWritten]);
            case Kind.Single:
                Span<byte> singleDigits = stackalloc byte[16];
                Utf8Formatter.TryFormat(
                    BitConverter.Int32BitsToSingle((int)_number), singleDigits, out var singleWritten);
                return ClusterHash.GetSlot(singleDigits[..singleWritten]);
            case Kind.Double:
                Span<byte> doubleDigits = stackalloc byte[32];
                Utf8Formatter.TryFormat(BitConverter.Int64BitsToDouble(_number), doubleDigits, out var doubleWritten);
                return ClusterHash.GetSlot(doubleDigits[..doubleWritten]);
            case Kind.Boolean:
                return ClusterHash.GetSlot(_number != 0 ? "1"u8 : "0"u8);
            default:
                return ClusterHash.GetSlot(ReadOnlySpan<byte>.Empty);
        }
    }

    internal bool TryGetInt64(out long value)
    {
        switch (_kind)
        {
            case Kind.Integer:
                value = _number;
                return true;
            case Kind.UnsignedInteger when _number >= 0:
                value = _number;
                return true;
            case Kind.String:
                return long.TryParse(
                    _string.AsSpan(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out value);
            case Kind.Bytes:
                return Utf8Parser.TryParse(_bytes.Span, out value, out var consumed)
                    && consumed == _bytes.Length;
            case Kind.Boolean:
                value = _number;
                return true;
            default:
                value = 0;
                return false;
        }
    }

    internal bool EqualsAsciiIgnoreCase(string value)
    {
        if (_kind == Kind.String)
        {
            return string.Equals(_string, value, StringComparison.OrdinalIgnoreCase);
        }

        if (_kind != Kind.Bytes || _bytes.Length != value.Length)
        {
            return false;
        }

        var bytes = _bytes.Span;
        for (var i = 0; i < bytes.Length; i++)
        {
            var actual = bytes[i];
            var expected = value[i];
            if (actual is >= (byte)'a' and <= (byte)'z')
            {
                actual -= (byte)('a' - 'A');
            }

            if (expected is >= 'a' and <= 'z')
            {
                expected -= (char)('a' - 'A');
            }

            if (actual != expected)
            {
                return false;
            }
        }

        return true;
    }

    internal bool IsEmpty
        => _kind switch
        {
            Kind.Null => true,
            Kind.String => _string!.Length == 0,
            Kind.Bytes => _bytes.IsEmpty,
            _ => false,
        };

    /// <summary>
    /// Compares the exact bulk-string payload written to Redis, so equivalent text, binary, and
    /// scalar representations compare equal.
    /// </summary>
    public bool Equals(RespireValue other)
    {
        if (_kind == Kind.Null || other._kind == Kind.Null)
        {
            return _kind == other._kind;
        }

        if (_kind == Kind.Bytes)
        {
            return other.EqualsBytes(_bytes.Span);
        }

        if (other._kind == Kind.Bytes)
        {
            return EqualsBytes(other._bytes.Span);
        }

        if (_kind == Kind.String)
        {
            return other._kind == Kind.String
                ? StringsHaveSamePayload(_string!, other._string!)
                : other.EqualsUtf8(_string!);
        }

        if (other._kind == Kind.String)
        {
            return EqualsUtf8(other._string!);
        }

        Span<byte> left = stackalloc byte[32];
        Span<byte> right = stackalloc byte[32];
        var leftLength = WriteWirePayload(left);
        var rightLength = other.WriteWirePayload(right);
        return left[..leftLength].SequenceEqual(right[..rightLength]);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is RespireValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        if (_kind == Kind.Null)
        {
            return 0;
        }

        if (_kind == Kind.Bytes)
        {
            return HashPayload(_bytes.Span);
        }

        var length = GetWireLength();
        byte[]? rented = null;
        var payload = length <= StackallocThreshold
            ? stackalloc byte[length]
            : (rented = ArrayPool<byte>.Shared.Rent(length));

        try
        {
            WriteWirePayload(payload);
            return HashPayload(payload[..length]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private bool EqualsBytes(ReadOnlySpan<byte> bytes)
    {
        if (_kind == Kind.Bytes)
        {
            return _bytes.Span.SequenceEqual(bytes);
        }

        if (_kind == Kind.String)
        {
            return Utf8Equals(_string!, bytes);
        }

        Span<byte> payload = stackalloc byte[32];
        var length = WriteWirePayload(payload);
        return payload[..length].SequenceEqual(bytes);
    }

    private bool EqualsUtf8(string value)
    {
        Span<byte> payload = stackalloc byte[32];
        var length = WriteWirePayload(payload);
        return Utf8Equals(value, payload[..length]);
    }

    private static bool Utf8Equals(string value, ReadOnlySpan<byte> bytes)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount != bytes.Length)
        {
            return false;
        }

        byte[]? rented = null;
        var encoded = byteCount <= StackallocThreshold
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            Encoding.UTF8.GetBytes(value, encoded);
            return encoded[..byteCount].SequenceEqual(bytes);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private static bool StringsHaveSamePayload(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        if (!ContainsUnpairedSurrogate(left) && !ContainsUnpairedSurrogate(right))
        {
            return false;
        }

        var byteCount = Encoding.UTF8.GetByteCount(left);
        byte[]? rented = null;
        var encoded = byteCount <= StackallocThreshold
            ? stackalloc byte[byteCount]
            : (rented = ArrayPool<byte>.Shared.Rent(byteCount));

        try
        {
            Encoding.UTF8.GetBytes(left, encoded);
            return Utf8Equals(right, encoded[..byteCount]);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
    }

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                    continue;
                }

                return true;
            }

            if (char.IsLowSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static int HashPayload(ReadOnlySpan<byte> payload)
    {
        var hash = new HashCode();
        foreach (var value in payload)
        {
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    internal int GetWireLength()
    {
        if (_kind == Kind.String)
        {
            return Encoding.UTF8.GetByteCount(_string!);
        }

        if (_kind == Kind.Bytes)
        {
            return _bytes.Length;
        }

        Span<byte> buffer = stackalloc byte[32];
        return WriteWirePayload(buffer);
    }

    internal RespireKey AsKey()
    {
        if (_kind == Kind.String)
        {
            return new RespireKey(_string!);
        }

        if (_kind == Kind.Bytes)
        {
            return new RespireKey(_bytes);
        }

        var bytes = new byte[GetWireLength()];
        WriteWirePayload(bytes);
        return new RespireKey(bytes);
    }

    internal int WriteWirePayload(Span<byte> destination)
    {
        switch (_kind)
        {
            case Kind.String:
                return Encoding.UTF8.GetBytes(_string!, destination);
            case Kind.Bytes:
                _bytes.Span.CopyTo(destination);
                return _bytes.Length;
            case Kind.Integer:
                Utf8Formatter.TryFormat(_number, destination, out var integerWritten);
                return integerWritten;
            case Kind.UnsignedInteger:
                Utf8Formatter.TryFormat(unchecked((ulong)_number), destination, out var unsignedWritten);
                return unsignedWritten;
            case Kind.Single:
                Utf8Formatter.TryFormat(BitConverter.Int32BitsToSingle((int)_number), destination, out var singleWritten);
                return singleWritten;
            case Kind.Double:
                Utf8Formatter.TryFormat(BitConverter.Int64BitsToDouble(_number), destination, out var doubleWritten);
                return doubleWritten;
            case Kind.Boolean:
                destination[0] = _number != 0 ? (byte)'1' : (byte)'0';
                return 1;
            default:
                return 0;
        }
    }

    /// <inheritdoc/>
    public override string ToString()
        => _kind switch
        {
            Kind.String => _string!,
            Kind.Bytes => Internal.Utf8String.GetString(_bytes),
            Kind.Integer => _number.ToString(CultureInfo.InvariantCulture),
            Kind.UnsignedInteger => unchecked((ulong)_number).ToString(CultureInfo.InvariantCulture),
            Kind.Single => BitConverter.Int32BitsToSingle((int)_number).ToString(CultureInfo.InvariantCulture),
            Kind.Double => BitConverter.Int64BitsToDouble(_number).ToString(CultureInfo.InvariantCulture),
            Kind.Boolean => _number != 0 ? "1" : "0",
            _ => string.Empty,
        };
}
