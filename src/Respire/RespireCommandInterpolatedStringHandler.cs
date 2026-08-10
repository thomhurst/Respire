using System.Globalization;
using System.Runtime.CompilerServices;

namespace Respire;

/// <summary>
/// Lets raw commands be written as interpolated strings:
/// <c>client.ExecuteAsync($"SET {key} {value} EX {60}")</c>. Literal text splits on spaces into
/// command tokens; every interpolation hole becomes exactly one argument and is never
/// re-tokenized — a value containing spaces stays a single argument. Holes use invariant
/// <see cref="IFormattable"/> formatting or <see cref="object.ToString"/>; they are not routed
/// through a Respire serializer.
/// </summary>
[InterpolatedStringHandler]
public struct RespireCommandInterpolatedStringHandler
{
    private readonly List<RespireValue> _tokens;
    private string? _operation;

    /// <summary>Initializes storage for an interpolated raw command.</summary>
    public RespireCommandInterpolatedStringHandler(int literalLength, int formattedCount)
        => _tokens = new List<RespireValue>(formattedCount + 2);

    /// <summary>Appends literal command text, splitting it into space-delimited tokens.</summary>
    public void AppendLiteral(string value)
    {
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _operation ??= word.ToUpperInvariant();
            _tokens.Add(word);
        }
    }

    /// <summary>Appends one protocol argument.</summary>
    public void AppendFormatted(RespireValue value) => _tokens.Add(value);

    /// <summary>Appends one text argument.</summary>
    public void AppendFormatted(string? value) => _tokens.Add(value ?? RespireValue.Null);

    /// <summary>Appends one signed 64-bit integer argument.</summary>
    public void AppendFormatted(long value) => _tokens.Add(value);

    /// <summary>Appends one signed 32-bit integer argument.</summary>
    public void AppendFormatted(int value) => _tokens.Add(value);

    /// <summary>Appends one double-precision argument.</summary>
    public void AppendFormatted(double value) => _tokens.Add(value);

    /// <summary>Appends one boolean argument.</summary>
    public void AppendFormatted(bool value) => _tokens.Add(value);

    /// <summary>Appends one Boolean argument; the format is ignored.</summary>
    public void AppendFormatted(bool value, string? format) => AppendFormatted(value);

    /// <summary>Appends one binary-safe argument.</summary>
    public void AppendFormatted(byte[] value) => _tokens.Add(value);

    /// <summary>Appends one nullable binary-safe argument; the format is ignored.</summary>
    public void AppendFormatted(byte[]? value, string? format)
        => _tokens.Add(value ?? RespireValue.Null);

    /// <summary>Appends one binary-safe argument.</summary>
    public void AppendFormatted(ReadOnlyMemory<byte> value) => _tokens.Add(value);

    /// <summary>Appends one binary-safe argument; the format is ignored.</summary>
    public void AppendFormatted(ReadOnlyMemory<byte> value, string? format) => AppendFormatted(value);

    /// <summary>Appends one Redis key argument.</summary>
    public void AppendFormatted(RespireKey value) => _tokens.Add(value.AsValue());

    /// <summary>Appends one Redis key argument; the format is ignored.</summary>
    public void AppendFormatted(RespireKey value, string? format) => AppendFormatted(value);

    /// <summary>Appends one protocol argument; the format is ignored.</summary>
    public void AppendFormatted(RespireValue value, string? format) => AppendFormatted(value);

    /// <summary>Appends one invariant-formatted argument.</summary>
    public void AppendFormatted<T>(T value)
        => _tokens.Add(Format(value, format: null));

    /// <summary>Appends one invariant-formatted argument using the supplied format.</summary>
    public void AppendFormatted<T>(T value, string? format)
        => _tokens.Add(Format(value, format));

    /// <summary>Appends one aligned protocol argument.</summary>
    public void AppendFormatted(RespireValue value, int alignment)
        => _tokens.Add(Align(value, alignment));

    /// <summary>Appends one aligned protocol argument; the format is ignored.</summary>
    public void AppendFormatted(RespireValue value, int alignment, string? format)
        => AppendFormatted(value, alignment);

    /// <summary>Appends one aligned Redis key argument.</summary>
    public void AppendFormatted(RespireKey value, int alignment)
        => _tokens.Add(Align(value.AsValue(), alignment));

    /// <summary>Appends one aligned Redis key argument; the format is ignored.</summary>
    public void AppendFormatted(RespireKey value, int alignment, string? format)
        => AppendFormatted(value, alignment);

    /// <summary>Appends one aligned Boolean argument.</summary>
    public void AppendFormatted(bool value, int alignment)
        => _tokens.Add(Align((RespireValue)value, alignment));

    /// <summary>Appends one aligned Boolean argument; the format is ignored.</summary>
    public void AppendFormatted(bool value, int alignment, string? format)
        => AppendFormatted(value, alignment);

    /// <summary>Appends one aligned nullable binary-safe argument.</summary>
    public void AppendFormatted(byte[]? value, int alignment)
        => _tokens.Add(value is null
            ? Align(RespireValue.Null, alignment)
            : Align(value.AsMemory(), alignment));

    /// <summary>Appends one aligned nullable binary-safe argument; the format is ignored.</summary>
    public void AppendFormatted(byte[]? value, int alignment, string? format)
        => AppendFormatted(value, alignment);

    /// <summary>Appends one aligned binary-safe argument.</summary>
    public void AppendFormatted(ReadOnlyMemory<byte> value, int alignment)
        => _tokens.Add(Align(value, alignment));

    /// <summary>Appends one aligned binary-safe argument; the format is ignored.</summary>
    public void AppendFormatted(ReadOnlyMemory<byte> value, int alignment, string? format)
        => AppendFormatted(value, alignment);

    /// <summary>Appends one invariant-formatted aligned argument.</summary>
    public void AppendFormatted<T>(T value, int alignment)
        => _tokens.Add(AlignText(value, alignment, format: null));

    /// <summary>Appends one invariant-formatted aligned argument using the supplied format.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
        => _tokens.Add(AlignText(value, alignment, format));

    private static RespireValue Format<T>(T value, string? format)
        => value is null && format is null
            ? RespireValue.Null
            : FormatText(value, format);

    private static string FormatText<T>(T value, string? format)
        => value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static RespireValue AlignText<T>(T value, int alignment, string? format)
    {
        var text = FormatText(value, format);
        var width = Math.Abs(alignment);
        return alignment < 0 ? text.PadRight(width) : text.PadLeft(width);
    }

    private static RespireValue Align(RespireValue value, int alignment)
    {
        if (value.IsNull)
        {
            return Align(ReadOnlyMemory<byte>.Empty, alignment);
        }

        var width = Math.Abs(alignment);
        var length = value.GetWireLength();
        if (length >= width)
        {
            return value;
        }

        var padded = new byte[width];
        padded.AsSpan().Fill((byte)' ');
        value.WriteWirePayload(
            alignment < 0
                ? padded.AsSpan(0, length)
                : padded.AsSpan(width - length, length));
        return padded;
    }

    private static RespireValue Align(ReadOnlyMemory<byte> value, int alignment)
        => Align(new RespireValue(value), alignment);

    internal readonly (string Operation, RespireValue[] Tokens) Build()
    {
        if (_tokens.Count == 0 || _operation is null)
        {
            throw new ArgumentException("The interpolated command must start with a literal command name.");
        }

        return (_operation, [.. _tokens]);
    }
}
