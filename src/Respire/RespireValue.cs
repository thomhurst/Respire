using System.Buffers.Text;
using System.Globalization;
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
public readonly struct RespireValue
{
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

    public RespireValue(string value) : this(Kind.String, s: value ?? throw new ArgumentNullException(nameof(value)))
    {
    }

    public RespireValue(ReadOnlyMemory<byte> value) : this(Kind.Bytes, bytes: value)
    {
    }

    public static readonly RespireValue Null = default;

    public bool IsNull => _kind == Kind.Null;

    public static implicit operator RespireValue(string? value)
        => value is null ? Null : new RespireValue(value);

    public static implicit operator RespireValue(byte[] value) => new(value.AsMemory());

    public static implicit operator RespireValue(ReadOnlyMemory<byte> value) => new(value);

    public static implicit operator RespireValue(long value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(byte value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(sbyte value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(short value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(ushort value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(int value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(uint value) => new(Kind.Integer, number: value);

    public static implicit operator RespireValue(ulong value)
        => new(Kind.UnsignedInteger, number: unchecked((long)value));

    public static implicit operator RespireValue(float value)
        => new(Kind.Single, number: BitConverter.SingleToInt32Bits(value));

    public static implicit operator RespireValue(double value)
        => new(Kind.Double, number: BitConverter.DoubleToInt64Bits(value));

    public static implicit operator RespireValue(decimal value)
        => new(value.ToString(CultureInfo.InvariantCulture));

    public static implicit operator RespireValue(bool value)
        => new(Kind.Boolean, number: value ? 1 : 0);

    /// <summary>Serializes this argument as one RESP bulk string.</summary>
    internal void WriteTo(ref RespWriter writer)
    {
        switch (_kind)
        {
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
                writer.WriteBulkString([]);
                break;
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

        slot = 0;
        return false;
    }

    public override string ToString()
        => _kind switch
        {
            Kind.String => _string!,
            Kind.Bytes => Encoding.UTF8.GetString(_bytes.Span),
            Kind.Integer => _number.ToString(CultureInfo.InvariantCulture),
            Kind.UnsignedInteger => unchecked((ulong)_number).ToString(CultureInfo.InvariantCulture),
            Kind.Single => BitConverter.Int32BitsToSingle((int)_number).ToString(CultureInfo.InvariantCulture),
            Kind.Double => BitConverter.Int64BitsToDouble(_number).ToString(CultureInfo.InvariantCulture),
            Kind.Boolean => _number != 0 ? "1" : "0",
            _ => string.Empty,
        };
}
