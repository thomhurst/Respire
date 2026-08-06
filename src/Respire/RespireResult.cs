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

    public RespDataType Type => Value.Type;

    public bool IsNull => Value.IsNull;

    /// <summary>True for a RESP error element (top-level errors throw instead).</summary>
    public bool IsError => Value.IsError;

    public string ErrorMessage => Value.GetErrorMessage();

    public string AsString() => Value.AsString();

    public long AsInteger() => Value.AsInteger();

    public double AsDouble()
    {
        var value = Value;
        return value.Type == RespDataType.Double
            ? value.AsDouble()
            : double.TryParse(value.AsString(), System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    public bool AsBoolean()
    {
        var value = Value;
        return value.Type == RespDataType.Boolean ? value.AsBoolean() : value.AsInteger() != 0;
    }

    public ReadOnlySpan<byte> AsSpan() => Value.AsSpan();

    public byte[] AsBytes() => Value.AsSpan().ToArray();

    /// <summary>Element count for aggregate replies (map pairs count as two elements).</summary>
    public int Count => Value.AsArray().Length;

    /// <summary>A non-owning view of an aggregate element; valid while the root is undisposed.</summary>
    public RespireResult this[int index] => new(in Value.AsArray()[index], none: null);

    public override string ToString() => Value.ToString();

    /// <summary>Returns pooled buffers (root results only). Safe to call more than once.</summary>
    public void Dispose() => _owner?.Dispose();
}
