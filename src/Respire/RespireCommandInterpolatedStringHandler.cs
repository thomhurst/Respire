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

    public RespireCommandInterpolatedStringHandler(int literalLength, int formattedCount)
        => _tokens = new List<RespireValue>(formattedCount + 2);

    public void AppendLiteral(string value)
    {
        foreach (var word in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            _operation ??= word.ToUpperInvariant();
            _tokens.Add(word);
        }
    }

    public void AppendFormatted(RespireValue value) => _tokens.Add(value);

    public void AppendFormatted(string? value) => _tokens.Add(value ?? RespireValue.Null);

    public void AppendFormatted(long value) => _tokens.Add(value);

    public void AppendFormatted(int value) => _tokens.Add(value);

    public void AppendFormatted(double value) => _tokens.Add(value);

    public void AppendFormatted(bool value) => _tokens.Add(value);

    public void AppendFormatted(byte[] value) => _tokens.Add(value);

    public void AppendFormatted(ReadOnlyMemory<byte> value) => _tokens.Add(value);

    public void AppendFormatted(RespireKey value) => _tokens.Add(value.AsValue());

    public void AppendFormatted<T>(T value)
        => _tokens.Add(Format(value, format: null));

    public void AppendFormatted<T>(T value, string? format)
        => _tokens.Add(Format(value, format));

    public void AppendFormatted<T>(T value, int alignment)
        => _tokens.Add(Align(Format(value, format: null), alignment));

    public void AppendFormatted<T>(T value, int alignment, string? format)
        => _tokens.Add(Align(Format(value, format), alignment));

    private static RespireValue Format<T>(T value, string? format)
        => value switch
        {
            null when format is null => RespireValue.Null,
            null => string.Empty,
            IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private static RespireValue Align(RespireValue value, int alignment)
    {
        var text = value.IsNull ? string.Empty : value.ToString();
        return alignment < 0
            ? text.PadRight(-alignment)
            : text.PadLeft(alignment);
    }

    internal readonly (string Operation, RespireValue[] Tokens) Build()
    {
        if (_tokens.Count == 0 || _operation is null)
        {
            throw new ArgumentException("The interpolated command must start with a literal command name.");
        }

        return (_operation, [.. _tokens]);
    }
}
