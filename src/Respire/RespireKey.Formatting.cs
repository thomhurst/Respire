namespace Respire;

public readonly partial struct RespireKey
{
    /// <summary>Returns the text representation. Only an empty format or G is supported; the provider is ignored.</summary>
    public string ToString(string? format, IFormatProvider? formatProvider) => AsValue().ToString(format, formatProvider);

    /// <summary>Writes the same text as ToString without allocating. Only an empty format or G is supported; the provider is ignored.</summary>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => AsValue().TryFormat(destination, out charsWritten, format, provider);

    /// <summary>Writes UTF-8 text without allocating. Invalid binary UTF-8 is replaced, as in ToString; this is not binary serialization.</summary>
    /// <remarks>Only an empty format or G is supported; the provider is ignored.</remarks>
    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
        => AsValue().TryFormat(utf8Destination, out bytesWritten, format, provider);

    /// <summary>Creates a text key, preserving the input verbatim and ignoring the provider.</summary>
    public static RespireKey Parse(string s, IFormatProvider? provider) => new(s);

    /// <summary>Copies the input into an owned text key, ignoring the provider.</summary>
    public static RespireKey Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => new(s.ToString());

    /// <summary>Creates a text key. Null returns false and Empty; all other text is accepted.</summary>
    public static bool TryParse(string? s, IFormatProvider? provider, out RespireKey result)
    {
        result = s is null ? default : new(s);
        return s is not null;
    }

    /// <summary>Copies any input, including empty text, into an owned text key.</summary>
    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out RespireKey result)
    {
        result = Parse(s, provider);
        return true;
    }
}
