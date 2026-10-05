using System.Globalization;
using System.Text;
using System.Text.Json;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Serialization;

public class PrimitiveNumberParityTests
{
    [Test]
    public async Task CanonicalAndFallbackInputsMatchOriginalJsonValidation()
    {
        string[] tokens = ["", "0", "-0", "1", "-1", "+1", "01", "-01", "00", "0.0", "-0.0",
            ".5", "1.", "1.25", "-12.5", "1e2", "1E+2", "1e-2", "0e0", "1e", "1e+", "1e-",
            "1e9999", "1e-9999", "NaN", "Infinity", "-Infinity", "null", "true", "1 2", "1\0",
            "1\v", "1\f", "１", "255", "256", "-128", "-129", "32767", "65536",
            "2147483648", "4294967296", "9223372036854775807", "-9223372036854775808",
            "18446744073709551615", "18446744073709551616", "79228162514264337593543950335",
            "79228162514264337593543950336", "1.7976931348623157e308", "3.4028235e38"];
        foreach (var token in tokens)
        foreach (var text in new[] { token, " \t\r\n" + token + " \t\r\n" })
        {
            var payload = Encoding.UTF8.GetBytes(text);
            await Check<byte>(payload);
            await Check<sbyte>(payload);
            await Check<short>(payload);
            await Check<ushort>(payload);
            await Check<int>(payload);
            await Check<uint>(payload);
            await Check<long>(payload);
            await Check<ulong>(payload);
            await Check<float>(payload);
            await Check<double>(payload);
            await Check<decimal>(payload);
        }
    }

    private static async Task Check<T>(byte[] payload) where T : IUtf8SpanParsable<T>
    {
        var expected = Outcome(() => Original<T>(payload));
        var actual = Outcome(() =>
        {
            if (!PrimitiveCodec.TryDeserialize<T>(payload, out var value))
                throw new InvalidOperationException("Numeric primitive was not recognized.");
            return value!;
        });
        await Assert.That(actual).IsEqualTo(expected);
    }

    private static string Outcome<T>(Func<T> parse)
    {
        try
        {
            var value = parse();
            // Preserve sign bits, including negative zero, rather than only numeric equality.
            if (value is double d) return BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture);
            if (value is float f) return BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture);
            return ((IFormattable)value!).ToString(null, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return "FormatException"; }
    }

    // The pre-optimization contract: validate one JSON number, then use the same numeric parser.
    private static T Original<T>(byte[] bytes) where T : IUtf8SpanParsable<T>
    {
        var payload = bytes.AsSpan().Trim(" \t\r\n"u8);
        try
        {
            var reader = new Utf8JsonReader(payload);
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number || reader.BytesConsumed != payload.Length)
                throw new FormatException();
        }
        catch (JsonException) { throw new FormatException(); }
        if (typeof(T) == typeof(decimal))
        {
            if (!decimal.TryParse(payload, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                throw new FormatException();
            return (T)(object)number;
        }
        if (!T.TryParse(payload, CultureInfo.InvariantCulture, out var value)
            || value is float f && !float.IsFinite(f)
            || value is double d && !double.IsFinite(d))
            throw new FormatException();
        return value;
    }
}
