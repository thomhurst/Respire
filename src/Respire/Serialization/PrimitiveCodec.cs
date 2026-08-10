using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Respire.Protocol;

namespace Respire.Serialization;

internal static class PrimitiveCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private enum PrimitiveKind : byte
    {
        None,
        Boolean,
        Character,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        Decimal,
    }

    internal static bool TrySerialize<T>(T value, out RespireValue result)
    {
        switch (TypeCache<T>.Kind)
        {
            case PrimitiveKind.Boolean:
                result = Read<T, bool>(ref value);
                return true;
            case PrimitiveKind.Character:
                result = Read<T, char>(ref value);
                return true;
            case PrimitiveKind.Byte:
                result = (int)Read<T, byte>(ref value);
                return true;
            case PrimitiveKind.SByte:
                result = (int)Read<T, sbyte>(ref value);
                return true;
            case PrimitiveKind.Int16:
                result = (int)Read<T, short>(ref value);
                return true;
            case PrimitiveKind.UInt16:
                result = (int)Read<T, ushort>(ref value);
                return true;
            case PrimitiveKind.Int32:
                result = Read<T, int>(ref value);
                return true;
            case PrimitiveKind.UInt32:
                result = Read<T, uint>(ref value);
                return true;
            case PrimitiveKind.Int64:
                result = Read<T, long>(ref value);
                return true;
            case PrimitiveKind.UInt64:
                result = Read<T, ulong>(ref value);
                return true;
            case PrimitiveKind.Single:
                var single = Read<T, float>(ref value);
                ThrowIfNonFinite(single);
                result = single;
                return true;
            case PrimitiveKind.Double:
                var @double = Read<T, double>(ref value);
                ThrowIfNonFinite(@double);
                result = @double;
                return true;
            case PrimitiveKind.Decimal:
                result = Read<T, decimal>(ref value).ToString(CultureInfo.InvariantCulture);
                return true;
            default:
                result = default;
                return false;
        }
    }

    internal static bool TryDeserialize<T>(ReadOnlySpan<byte> payload, out T? result)
    {
        var kind = TypeCache<T>.Kind;
        if (kind != PrimitiveKind.None
            && TypeCache<T>.IsNullable
            && TrimJsonWhitespace(payload).SequenceEqual("null"u8))
        {
            result = default;
            return true;
        }

        switch (kind)
        {
            case PrimitiveKind.Boolean:
                result = Return<T, bool>(ParseBoolean(payload));
                return true;
            case PrimitiveKind.Character:
                result = Return<T, char>(ParseCharacter(payload));
                return true;
            case PrimitiveKind.Byte:
                result = Return<T, byte>(Parse<byte>(payload));
                return true;
            case PrimitiveKind.SByte:
                result = Return<T, sbyte>(Parse<sbyte>(payload));
                return true;
            case PrimitiveKind.Int16:
                result = Return<T, short>(Parse<short>(payload));
                return true;
            case PrimitiveKind.UInt16:
                result = Return<T, ushort>(Parse<ushort>(payload));
                return true;
            case PrimitiveKind.Int32:
                result = Return<T, int>(Parse<int>(payload));
                return true;
            case PrimitiveKind.UInt32:
                result = Return<T, uint>(Parse<uint>(payload));
                return true;
            case PrimitiveKind.Int64:
                result = Return<T, long>(Parse<long>(payload));
                return true;
            case PrimitiveKind.UInt64:
                result = Return<T, ulong>(Parse<ulong>(payload));
                return true;
            case PrimitiveKind.Single:
                result = Return<T, float>(ParseFiniteSingle(payload));
                return true;
            case PrimitiveKind.Double:
                result = Return<T, double>(ParseFiniteDouble(payload));
                return true;
            case PrimitiveKind.Decimal:
                result = Return<T, decimal>(ParseDecimal(payload));
                return true;
            default:
                result = default;
                return false;
        }
    }

    internal static bool TryDeserialize<T>(in RespValue value, out T? result)
    {
        switch (value.Type)
        {
            case RespDataType.Integer:
                return TryDeserializeInteger(value.AsInteger(), out result);
            case RespDataType.Boolean when TypeCache<T>.Kind == PrimitiveKind.Boolean:
                result = Return<T, bool>(value.AsBoolean());
                return true;
            case RespDataType.Double:
                return TryDeserializeDouble(value.AsDouble(), out result);
            default:
                return TryDeserialize(value.AsSpan(), out result);
        }
    }

    private static bool TryDeserializeInteger<T>(long value, out T? result)
    {
        var kind = TypeCache<T>.Kind;
        result = kind switch
        {
            PrimitiveKind.Boolean => Return<T, bool>(value != 0),
            PrimitiveKind.Byte => Return<T, byte>(checked((byte)value)),
            PrimitiveKind.SByte => Return<T, sbyte>(checked((sbyte)value)),
            PrimitiveKind.Int16 => Return<T, short>(checked((short)value)),
            PrimitiveKind.UInt16 => Return<T, ushort>(checked((ushort)value)),
            PrimitiveKind.Int32 => Return<T, int>(checked((int)value)),
            PrimitiveKind.UInt32 => Return<T, uint>(checked((uint)value)),
            PrimitiveKind.Int64 => Return<T, long>(value),
            PrimitiveKind.UInt64 => Return<T, ulong>(checked((ulong)value)),
            PrimitiveKind.Single => Return<T, float>(value),
            PrimitiveKind.Double => Return<T, double>(value),
            PrimitiveKind.Decimal => Return<T, decimal>(value),
            _ => default,
        };
        return kind is not (PrimitiveKind.None or PrimitiveKind.Character);
    }

    private static bool TryDeserializeDouble<T>(double value, out T? result)
    {
        switch (TypeCache<T>.Kind)
        {
            case PrimitiveKind.Single:
                var single = (float)value;
                if (!float.IsFinite(single))
                {
                    throw InvalidValue<float>();
                }

                result = Return<T, float>(single);
                return true;
            case PrimitiveKind.Double:
                if (!double.IsFinite(value))
                {
                    throw InvalidValue<double>();
                }

                result = Return<T, double>(value);
                return true;
            case PrimitiveKind.Decimal:
                if (!double.IsFinite(value))
                {
                    throw InvalidValue<decimal>();
                }

                try
                {
                    result = Return<T, decimal>(checked((decimal)value));
                }
                catch (OverflowException)
                {
                    throw InvalidValue<decimal>();
                }

                return true;
            default:
                result = default;
                return false;
        }
    }

    private static TValue Read<T, TValue>(ref T value)
        where TValue : struct
    {
        if (TypeCache<T>.IsNullable)
        {
            return Unsafe.As<T, TValue?>(ref value).GetValueOrDefault();
        }

        return Unsafe.As<T, TValue>(ref value);
    }

    private static T? Return<T, TValue>(TValue value)
        where TValue : struct
    {
        if (TypeCache<T>.IsNullable)
        {
            TValue? nullable = value;
            return Unsafe.As<TValue?, T>(ref nullable);
        }

        return Unsafe.As<TValue, T>(ref value);
    }

    private static bool ParseBoolean(ReadOnlySpan<byte> payload)
    {
        if (TryParseBooleanToken(payload, out var value)
            || TryParseBooleanToken(TrimJsonWhitespace(payload), out value))
        {
            return value;
        }

        throw InvalidValue<bool>();
    }

    private static char ParseCharacter(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 2 && payload[0] == (byte)'"' && payload[^1] == (byte)'"')
        {
            try
            {
                var reader = new Utf8JsonReader(payload);
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    throw InvalidValue<char>();
                }

                var value = reader.GetString();
                if (value?.Length != 1 || reader.Read())
                {
                    throw InvalidValue<char>();
                }

                return value[0];
            }
            catch (JsonException)
            {
                throw InvalidValue<char>();
            }
        }

        try
        {
            if (StrictUtf8.GetCharCount(payload) != 1)
            {
                throw InvalidValue<char>();
            }

            Span<char> character = stackalloc char[1];
            _ = StrictUtf8.GetChars(payload, character);
            return character[0];
        }
        catch (DecoderFallbackException)
        {
            throw InvalidValue<char>();
        }
    }

    private static bool TryParseBooleanToken(ReadOnlySpan<byte> payload, out bool value)
    {
        if (payload.SequenceEqual("true"u8) || payload.SequenceEqual("1"u8))
        {
            value = true;
            return true;
        }

        if (payload.SequenceEqual("false"u8) || payload.SequenceEqual("0"u8))
        {
            value = false;
            return true;
        }

        value = default;
        return false;
    }

    private static ReadOnlySpan<byte> TrimJsonWhitespace(ReadOnlySpan<byte> payload)
    {
        while (!payload.IsEmpty && IsJsonWhitespace(payload[0]))
        {
            payload = payload[1..];
        }

        while (!payload.IsEmpty && IsJsonWhitespace(payload[^1]))
        {
            payload = payload[..^1];
        }

        return payload;
    }

    private static bool IsJsonWhitespace(byte value)
        => value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private static TValue Parse<TValue>(ReadOnlySpan<byte> payload)
        where TValue : IUtf8SpanParsable<TValue>
    {
        if (TValue.TryParse(payload, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw InvalidValue<TValue>();
    }

    private static float ParseFiniteSingle(ReadOnlySpan<byte> payload)
    {
        if (float.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && float.IsFinite(value))
        {
            return value;
        }

        throw InvalidValue<float>();
    }

    private static double ParseFiniteDouble(ReadOnlySpan<byte> payload)
    {
        if (double.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value))
        {
            return value;
        }

        throw InvalidValue<double>();
    }

    private static decimal ParseDecimal(ReadOnlySpan<byte> payload)
    {
        if (decimal.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw InvalidValue<decimal>();
    }

    private static FormatException InvalidValue<T>()
        => new($"Redis value is not a valid {typeof(T).Name}.");

    private static void ThrowIfNonFinite(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException("Non-finite floating-point values cannot be stored by generic typed APIs.");
        }
    }

    private static void ThrowIfNonFinite(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("Non-finite floating-point values cannot be stored by generic typed APIs.");
        }
    }

    private static class TypeCache<T>
    {
        private static readonly Type Type = typeof(T);
        private static readonly Type? UnderlyingType = Nullable.GetUnderlyingType(Type);

        internal static readonly bool IsNullable = UnderlyingType is not null;
        internal static readonly PrimitiveKind Kind = GetKind(UnderlyingType ?? Type);
    }

    private static PrimitiveKind GetKind(Type type)
    {
        if (type == typeof(bool)) return PrimitiveKind.Boolean;
        if (type == typeof(char)) return PrimitiveKind.Character;
        if (type == typeof(byte)) return PrimitiveKind.Byte;
        if (type == typeof(sbyte)) return PrimitiveKind.SByte;
        if (type == typeof(short)) return PrimitiveKind.Int16;
        if (type == typeof(ushort)) return PrimitiveKind.UInt16;
        if (type == typeof(int)) return PrimitiveKind.Int32;
        if (type == typeof(uint)) return PrimitiveKind.UInt32;
        if (type == typeof(long)) return PrimitiveKind.Int64;
        if (type == typeof(ulong)) return PrimitiveKind.UInt64;
        if (type == typeof(float)) return PrimitiveKind.Single;
        if (type == typeof(double)) return PrimitiveKind.Double;
        if (type == typeof(decimal)) return PrimitiveKind.Decimal;
        return PrimitiveKind.None;
    }
}
