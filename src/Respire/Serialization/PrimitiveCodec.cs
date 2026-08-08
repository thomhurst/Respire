using System.Globalization;
using System.Runtime.CompilerServices;

namespace Respire.Serialization;

internal static class PrimitiveCodec
{
    private enum PrimitiveKind : byte
    {
        None,
        Boolean,
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
                result = Read<T, bool>(ref value) ? "true" : "false";
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
        switch (TypeCache<T>.Kind)
        {
            case PrimitiveKind.Boolean:
                result = Return<T, bool>(ParseBoolean(payload));
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
                result = Return<T, float>(Parse<float>(payload));
                return true;
            case PrimitiveKind.Double:
                result = Return<T, double>(Parse<double>(payload));
                return true;
            case PrimitiveKind.Decimal:
                result = Return<T, decimal>(Parse<decimal>(payload));
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
        if (payload.SequenceEqual("true"u8) || payload.SequenceEqual("1"u8))
        {
            return true;
        }

        if (payload.SequenceEqual("false"u8) || payload.SequenceEqual("0"u8))
        {
            return false;
        }

        throw InvalidValue<bool>();
    }

    private static TValue Parse<TValue>(ReadOnlySpan<byte> payload)
        where TValue : IUtf8SpanParsable<TValue>
    {
        if (TValue.TryParse(payload, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        throw InvalidValue<TValue>();
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
