using System.Globalization;
using System.Text;

namespace Respire.VectorData;

/// <summary>Supported filter storage shapes.</summary>
public enum RespireVectorDataFilterKind
{
    /// <summary>Ordinal string equality, stored as an encoded case-sensitive TAG.</summary>
    String,
    /// <summary>Finite numeric values, stored as NUMERIC. Supported CLR types are byte through int/uint, float and double.</summary>
    Numeric,
    /// <summary>Boolean values stored as NUMERIC 0 or 1.</summary>
    Boolean,
    /// <summary>String arrays/lists, stored as comma-separated encoded case-sensitive TAGs.</summary>
    StringCollection,
}

/// <summary>Explicit property name, hash field and storage shape for expression filters.</summary>
public sealed record RespireVectorDataFilterField(string PropertyName, string StorageName, RespireVectorDataFilterKind Kind);

/// <summary>Lossless TAG storage for ordinal filtering, including empty strings, whitespace and Redis syntax.</summary>
public static class RespireVectorDataFilterEncoding
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Encodes a string as a single safe TAG token. Null values must be omitted from the hash.</summary>
    public static string EncodeTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "s" + Convert.ToHexString(Utf8.GetBytes(value));
    }

    /// <summary>Encodes collection elements as comma-separated TAG tokens. Null elements are unsupported.</summary>
    public static string EncodeTags(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return string.Join(',', values.Select(EncodeTag));
    }

    /// <summary>Decodes a token written by <see cref="EncodeTag"/>.</summary>
    public static string DecodeTag(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || value[0] != 's') throw new FormatException("Expected an encoded VectorData TAG.");
        return Utf8.GetString(Convert.FromHexString(value[1..]));
    }

    /// <summary>Formats finite NUMERIC values with their exact double representation. Promotes float values losslessly.</summary>
    public static string EncodeNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "Filter numbers must be finite.");
        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
