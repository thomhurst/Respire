using Respire.Protocol;

namespace Respire;

/// <summary>
/// The reply of a raw command (<see cref="RespireClient.ExecuteAsync(string, RespireValue[])"/>)
/// as a thin view over the RESP value. This is the one protocol-shaped public result type; the
/// typed command surface returns plain .NET types instead. The root result is a lease over
/// pooled memory — dispose it when done. Nested results obtained via the indexer are views into
/// the root and must not outlive it.
/// </summary>
public readonly struct RespireResult : IDisposable
{
    private readonly RespValue _value;
    private readonly bool _owned;

    internal RespireResult(in RespValue value, bool owned = true)
    {
        _value = value;
        _owned = owned;
    }

    public RespDataType Type => _value.Type;

    public bool IsNull => _value.IsNull;

    /// <summary>True for a RESP error element (top-level errors throw instead).</summary>
    public bool IsError => _value.IsError;

    public string ErrorMessage => _value.GetErrorMessage();

    public string AsString() => _value.AsString();

    public long AsInteger() => _value.AsInteger();

    public double AsDouble() => _value.Type == RespDataType.Double
        ? _value.AsDouble()
        : double.TryParse(_value.AsString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    public bool AsBoolean() => _value.Type == RespDataType.Boolean ? _value.AsBoolean() : _value.AsInteger() != 0;

    public ReadOnlySpan<byte> AsSpan() => _value.AsSpan();

    public byte[] AsBytes() => _value.AsSpan().ToArray();

    /// <summary>Element count for aggregate replies (map pairs count as two elements).</summary>
    public int Count => _value.AsArray().Length;

    /// <summary>A non-owning view of an aggregate element; valid while the root is undisposed.</summary>
    public RespireResult this[int index] => new(in _value.AsArray()[index], owned: false);

    public override string ToString() => _value.ToString();

    public void Dispose()
    {
        if (_owned)
        {
            _value.Dispose();
        }
    }
}
