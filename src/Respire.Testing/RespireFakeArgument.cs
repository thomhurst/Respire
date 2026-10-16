using System.Text;

namespace Respire.Testing;

/// <summary>Matches one complete wire argument, either by exact bytes or as a wildcard.</summary>
public sealed class RespireFakeArgument
{
    private readonly byte[]? _bytes;
    private RespireFakeArgument(byte[]? bytes) => _bytes = bytes;

    /// <summary>Matches exactly one argument of any length, including an empty argument.</summary>
    public static RespireFakeArgument Any { get; } = new(null);

    /// <summary>Matches a UTF-8 argument, case-sensitively.</summary>
    public static RespireFakeArgument Exact(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(Encoding.UTF8.GetBytes(value));
    }

    /// <summary>Copies and matches an argument's exact bytes.</summary>
    public static RespireFakeArgument Exact(ReadOnlyMemory<byte> value) => new(value.ToArray());

    /// <summary>Converts a string to an exact UTF-8 match.</summary>
    public static implicit operator RespireFakeArgument(string value) => Exact(value);

    internal bool Matches(byte[] value) => _bytes is null || _bytes.AsSpan().SequenceEqual(value);
    /// <summary>Returns a printable, binary-safe representation of this matcher.</summary>
    public override string ToString() => _bytes is null ? "<any>" : Format(_bytes);

    internal static string Format(ReadOnlySpan<byte> bytes)
    {
        var result = new StringBuilder("\"");
        foreach (var value in bytes)
        {
            if (value is >= 32 and <= 126 && value is not ((byte)'"') and not ((byte)'\\'))
                result.Append((char)value);
            else result.Append("\\x").Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return result.Append('"').ToString();
    }
}
