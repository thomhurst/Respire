using System.Globalization;
using Respire.Internal;

namespace Respire;

public readonly partial struct RespireValue
{
    /// <summary>Returns the invariant text representation. Only an empty format or G is supported; the provider is ignored.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        ArgumentFormatting.ValidateFormat(format);
        return ToString();
    }

    /// <summary>Writes the same text as ToString without allocating. Only an empty format or G is supported; the provider is ignored.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        ArgumentFormatting.ValidateFormat(format);
        switch (_kind)
        {
            case Kind.String:
            case Kind.PreEncoded:
                return ArgumentFormatting.TryCopy(_string, destination, out charsWritten);
            case Kind.Bytes:
                return ArgumentFormatting.TryDecode(_bytes.Span, default, destination, out charsWritten);
            case Kind.Prefixed:
                charsWritten = 0;
                if (_string is null)
                    return ArgumentFormatting.TryDecode(_prefix!.Bytes, _bytes.Span, destination, out charsWritten);
                if (_prefix!.Text is { } prefix)
                {
                    if (!ArgumentFormatting.TryCopy(prefix, destination, out var first)
                        || !ArgumentFormatting.TryCopy(_string, destination[first..], out var last)) return false;
                    charsWritten = first + last;
                    return true;
                }
                if (!ArgumentFormatting.TryDecode(_prefix.Bytes, default, destination, out var prefixWritten)
                    || !ArgumentFormatting.TryNormalize(_string.AsSpan(), destination[prefixWritten..], out var suffixWritten)) return false;
                charsWritten = prefixWritten + suffixWritten;
                return true;
            case Kind.Integer:
                return _number.TryFormat(destination, out charsWritten, provider: CultureInfo.InvariantCulture);
            case Kind.UnsignedInteger:
                return unchecked((ulong)_number).TryFormat(destination, out charsWritten, provider: CultureInfo.InvariantCulture);
            case Kind.Single:
                return BitConverter.Int32BitsToSingle((int)_number).TryFormat(destination, out charsWritten, provider: CultureInfo.InvariantCulture);
            case Kind.Double:
                return BitConverter.Int64BitsToDouble(_number).TryFormat(destination, out charsWritten, provider: CultureInfo.InvariantCulture);
            default:
                return ArgumentFormatting.TryCopy(_kind == Kind.Boolean ? (_number != 0 ? "1" : "0") : "", destination, out charsWritten);
        }
    }

    /// <summary>Writes the UTF-8 text representation without allocating. Invalid binary UTF-8 is replaced, as in ToString; this is not binary serialization.</summary>
    /// <remarks>Only an empty format or G is supported; the provider is ignored.</remarks>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
    {
        ArgumentFormatting.ValidateFormat(format);
        if (_kind is Kind.String or Kind.PreEncoded)
            return ArgumentFormatting.TryEncode(_string, utf8Destination, out bytesWritten);
        if (_kind == Kind.Prefixed && _prefix!.Text is { } prefix && _string is not null)
            return ArgumentFormatting.TryEncode(prefix, _string, utf8Destination, out bytesWritten);
        if (_kind == Kind.Bytes)
            return ArgumentFormatting.TryNormalize(_bytes.Span, default, utf8Destination, out bytesWritten);
        if (_kind == Kind.Prefixed && (_string is null || _prefix!.Text is null))
        {
            if (_string is null)
                return ArgumentFormatting.TryNormalize(_prefix!.Bytes, _bytes.Span, utf8Destination, out bytesWritten);
            bytesWritten = 0;
            if (!ArgumentFormatting.TryNormalize(_prefix!.Bytes, default, utf8Destination, out var first)
                || !ArgumentFormatting.TryEncode(_string, utf8Destination[first..], out var last)) return false;
            bytesWritten = first + last;
            return true;
        }

        bytesWritten = 0;
        if (GetWireLength() > utf8Destination.Length) return false;
        bytesWritten = WriteWirePayload(utf8Destination);
        return true;
    }

    /// <summary>Creates a text argument, preserving the input verbatim and ignoring the provider.</summary>
    public static RespireValue Parse(string s, IFormatProvider? provider) => new(s);

    /// <summary>Copies the input into an owned text argument, ignoring the provider.</summary>
    public static RespireValue Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => new(s.ToString());

    /// <summary>Creates a text argument. Null returns false and the Null sentinel; all other text is accepted.</summary>
    public static bool TryParse(string? s, IFormatProvider? provider, out RespireValue result)
    {
        result = s;
        return s is not null;
    }

    /// <summary>Copies any input, including empty text, into an owned text argument.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out RespireValue result)
    {
        result = Parse(s, provider);
        return true;
    }
}
