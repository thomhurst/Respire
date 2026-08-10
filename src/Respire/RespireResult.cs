using System.Collections;
using System.Globalization;
using Respire.Internal;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// The reply of a command (<see cref="RespireClient.ExecuteAsync(RespireCommand, RespireValue[])"/>)
/// as a thin view over the RESP value. This is the one protocol-shaped public result type; the
/// typed command surface returns plain .NET types instead. The root result is a lease over
/// pooled memory — dispose it when done; disposal is idempotent across struct copies. Nested
/// results obtained via the indexer are views into the root and must not outlive it.
/// </summary>
public readonly struct RespireResult : IDisposable, IReadOnlyList<RespireResult>
{
    private readonly PooledValueOwner? _owner;
    private readonly PooledValueOwner? _lifetime;
    private readonly RespValue _borrowed;
    private readonly IRespireSerializer? _serializer;

    /// <summary>Root result: owns the pooled reply.</summary>
    internal RespireResult(in RespValue value, IRespireSerializer? serializer = null)
    {
        _owner = new PooledValueOwner(in value);
        _serializer = serializer ?? RespireSerializer.Default;
    }

    /// <summary>Nested element view: storage belongs to the root.</summary>
    private RespireResult(
        in RespValue borrowed, PooledValueOwner lifetime, IRespireSerializer? serializer)
    {
        _borrowed = borrowed;
        _lifetime = lifetime;
        _serializer = serializer;
    }

    private PooledValueOwner? Lifetime => _owner ?? _lifetime;

    private RespValue Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _owner?.Value ?? _borrowed;
        }
    }

    /// <summary>Gets whether the root result owning this view has been disposed.</summary>
    public bool IsDisposed => Lifetime is null || Lifetime.IsDisposed;

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

    /// <summary>
    /// Converts the reply using primitive fast paths or the serializer configured on the client
    /// that produced this result.
    /// </summary>
    public T? As<T>()
    {
        var value = Value;
        if (value.IsNull)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)value.AsString();
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)value.AsSpan().ToArray();
        }

        if (PrimitiveCodec.TryDeserialize<T>(in value, out var primitive))
        {
            return primitive;
        }

        return (_serializer ?? RespireSerializer.Default).Deserialize<T>(value.AsSpan());
    }

    /// <summary>Element count for aggregate replies (map pairs count as two elements).</summary>
    public int Count => Value.AsArray().Length;

    /// <summary>A non-owning view of an aggregate element; valid while the root is undisposed.</summary>
    public RespireResult this[int index]
        => new(in Value.AsArray()[index], Lifetime!, _serializer);

    /// <summary>Enumerates non-owning aggregate element views without allocating.</summary>
    public Enumerator GetEnumerator() => new(this);

    IEnumerator<RespireResult> IEnumerable<RespireResult>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public override string ToString() => Value.ToString();

    /// <summary>Returns pooled buffers (root results only). Safe to call more than once.</summary>
    public void Dispose() => _owner?.Dispose();

    /// <summary>Enumerates non-owning views over an aggregate result's elements.</summary>
    public struct Enumerator : IEnumerator<RespireResult>
    {
        private readonly RespireResult _result;
        private int _index;

        internal Enumerator(RespireResult result)
        {
            _result = result;
            _index = -1;
        }

        /// <summary>Gets the element at the current enumerator position.</summary>
        public readonly RespireResult Current => _result[_index];

        readonly object IEnumerator.Current => Current;

        /// <summary>Advances to the next aggregate element.</summary>
        public bool MoveNext()
        {
            var next = _index + 1;
            if (next >= _result.Count)
            {
                return false;
            }

            _index = next;
            return true;
        }

        /// <summary>Resets the enumerator to its initial position.</summary>
        public void Reset() => _index = -1;

        /// <summary>Releases the enumerator. No resources are owned.</summary>
        public readonly void Dispose()
        {
        }
    }
}
