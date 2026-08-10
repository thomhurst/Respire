using System.Globalization;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// The reply of a raw command (<see cref="RespireClient.ExecuteAsync(string, RespireValue[])"/>)
/// as a thin view over the RESP value. This is the one protocol-shaped public result type; the
/// typed command surface returns plain .NET types instead. The root result is a lease over
/// pooled memory — dispose it when done; disposal is idempotent across struct copies. Nested
/// results obtained via the indexer are views into the root and must not outlive it.
/// </summary>
public readonly struct RespireResult : IDisposable
{
    private readonly PooledValueOwner? _owner;
    private readonly RespValue _borrowed;

    /// <summary>Root result: owns the pooled reply.</summary>
    internal RespireResult(in RespValue value) => _owner = new PooledValueOwner(in value);

    /// <summary>Nested element view: storage belongs to the root.</summary>
    private RespireResult(in RespValue borrowed, PooledValueOwner? none)
    {
        _borrowed = borrowed;
        _owner = none;
    }

    private RespValue Value => _owner?.Value ?? _borrowed;

    /// <summary>The RESP wire type of this result.</summary>
    public RespDataType Type => Value.Type;

    /// <summary>Whether the server returned RESP null.</summary>
    public bool IsNull => Value.IsNull;

    /// <summary>True for a RESP error element (top-level errors throw instead).</summary>
    public bool IsError => Value.IsError;

    /// <summary>The server error text.</summary>
    public string ErrorMessage => Value.GetErrorMessage();

    /// <summary>Decodes a string-like result as UTF-8.</summary>
    public string AsString() => Value.AsString();

    /// <summary>Reads an integer result.</summary>
    public long AsInteger() => Value.AsInteger();

    /// <summary>
    /// The reply as a double. RESP doubles are returned as-is; other replies are parsed from their
    /// text using the invariant culture.
    /// </summary>
    /// <exception cref="FormatException">The reply is not a RESP double and its text does not parse as one.</exception>
    public double AsDouble()
    {
        var value = Value;
        if (value.Type == RespDataType.Double)
        {
            return value.AsDouble();
        }

        var text = value.AsString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new FormatException($"The {value.Type} reply '{text}' is not a valid double.");
    }

    /// <summary>Reads a RESP boolean or integer flag.</summary>
    public bool AsBoolean()
    {
        var value = Value;
        return value.Type == RespDataType.Boolean ? value.AsBoolean() : value.AsInteger() != 0;
    }

    /// <summary>Returns the borrowed raw bytes for a string-like result.</summary>
    public ReadOnlySpan<byte> AsSpan() => Value.AsSpan();

    /// <summary>Copies a string-like result into a byte array.</summary>
    public byte[] AsBytes() => Value.AsSpan().ToArray();

    /// <summary>Element count for aggregate replies (map pairs count as two elements).</summary>
    public int Count => Value.AsArray().Length;

    /// <summary>A non-owning view of an aggregate element; valid while the root is undisposed.</summary>
    public RespireResult this[int index] => new(in Value.AsArray()[index], none: null);

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();

    /// <summary>Returns pooled buffers (root results only). Safe to call more than once.</summary>
    public void Dispose() => _owner?.Dispose();
}
