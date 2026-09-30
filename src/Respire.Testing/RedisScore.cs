using System.Globalization;
using System.Numerics;
using System.Text;

namespace Respire.Testing;

internal static class RedisScore
{
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out double value)
    {
        value = 0;
        if (bytes.IsEmpty || bytes[0] <= 32 || bytes[^1] <= 32 || bytes.Contains((byte)0)) return false;
        var text = Encoding.ASCII.GetString(bytes);
        var unsigned = text.AsSpan();
        var negative = unsigned[0] == '-';
        if (unsigned[0] is '+' or '-') unsigned = unsigned[1..];
        if (unsigned.Equals("inf", StringComparison.OrdinalIgnoreCase) || unsigned.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = negative ? double.NegativeInfinity : double.PositiveInfinity;
            return true;
        }
        if (unsigned.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return TryHexScore(unsigned[2..], negative, out value);
        if (!double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                CultureInfo.InvariantCulture, out value) || !double.IsFinite(value)) return false;
        if (value != 0) return true;
        // strtod rejects underflow to zero but accepts actual zero, including a large exponent.
        foreach (var digit in unsigned)
        {
            if (digit is 'e' or 'E') break;
            if (digit is >= '1' and <= '9') return false;
        }
        return true;
    }

    private static bool TryHexScore(ReadOnlySpan<char> text, bool negative, out double value)
    {
        value = 0;
        var exponentIndex = text.IndexOfAny('p', 'P');
        var mantissaText = exponentIndex < 0 ? text : text[..exponentIndex];
        var exponent = 0;
        if (exponentIndex >= 0)
        {
            var exponentText = text[(exponentIndex + 1)..];
            if (exponentText.IsEmpty) return false;
            var exponentNegative = exponentText[0] == '-';
            if (exponentText[0] is '+' or '-') exponentText = exponentText[1..];
            if (exponentText.IsEmpty) return false;
            foreach (var digit in exponentText)
            {
                if (digit is < '0' or > '9') return false;
                // Beyond this cap, even a maximum-size request's mantissa cannot
                // bring the exponent back into the binary64 range.
                exponent = Math.Min(100_000_000, exponent * 10 + digit - '0');
            }
            if (exponentNegative) exponent = -exponent;
        }
        var digits = new StringBuilder(mantissaText.Length + 1).Append('0');
        var point = false;
        var fractionDigits = 0;
        foreach (var digit in mantissaText)
        {
            if (digit == '.' && !point) { point = true; continue; }
            if (!char.IsAsciiHexDigit(digit)) return false;
            digits.Append(digit);
            if (point) fractionDigits++;
        }
        if (digits.Length == 1) return false;
        var mantissa = BigInteger.Parse(digits.ToString(), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        if (mantissa.IsZero) { value = negative ? -0d : 0d; return true; }
        exponent -= 4 * fractionDigits;
        var bits = mantissa.GetBitLength();
        if (bits + exponent > 1024 || bits + exponent < -1074) return false;
        // Round once to the binary64 significand (or subnormal grid), ties to even.
        var shift = (int)Math.Max(bits - 53, -1074L - exponent);
        if (shift > 0)
        {
            var truncated = mantissa >> shift;
            var remainder = mantissa - (truncated << shift);
            var halfway = BigInteger.One << (shift - 1);
            if (remainder > halfway || (remainder == halfway && !truncated.IsEven)) truncated++;
            mantissa = truncated;
            exponent += shift;
        }
        value = Math.ScaleB((double)mantissa, exponent);
        if (negative) value = -value;
        return double.IsFinite(value) && value != 0;
    }
}
