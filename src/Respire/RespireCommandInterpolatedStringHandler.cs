using System.Globalization;
using System.Runtime.CompilerServices;

namespace Respire;

/// <summary>
/// Lets raw commands be written as interpolated strings:
/// <c>client.ExecuteAsync($"SET {key} {value} EX {60}")</c>. Literal text splits on spaces into
/// command tokens; every interpolation hole becomes exactly one argument and is never
/// re-tokenized — a value containing spaces stays a single argument. Arguments serialize
/// straight into the RESP frame.
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
        => _tokens.Add(value switch
        {
            null => RespireValue.Null,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        });

    internal readonly (string Operation, RespireValue[] Tokens) Build()
    {
        if (_tokens.Count == 0 || _operation is null)
        {
            throw new ArgumentException("The interpolated command must start with a literal command name.");
        }

        return (_operation, [.. _tokens]);
    }
}
