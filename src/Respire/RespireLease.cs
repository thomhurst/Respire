using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A zero-copy read result backed by pooled memory. Valid until <see cref="Dispose"/>.
/// Copies share one owner: disposal is idempotent across copies. Access after disposal throws
/// <see cref="ObjectDisposedException"/>.
/// </summary>
public readonly struct RespireLease : IDisposable
{
    private readonly PooledValueOwner? _owner;

    internal RespireLease(in RespValue value) => _owner = new PooledValueOwner(in value);

    private RespValue Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _owner!.Value;
        }
    }

    /// <summary>Gets whether this lease (or any copy sharing its owner) has been disposed.</summary>
    public bool IsDisposed => _owner is null || _owner.IsDisposed;

    /// <summary>True when the key was missing.</summary>
    public bool IsNull => Value.IsNull;

    /// <summary>The leased payload length in bytes, or zero for a null or disposed lease.</summary>
    public int Length => Span.Length;

    /// <summary>The payload. Do not use after <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Span => Value.AsSpan();

    /// <summary>Copies the payload into a new array that remains valid after disposal.</summary>
    public byte[] ToArray() => Span.ToArray();

    /// <summary>Copies the payload when <paramref name="destination"/> is large enough.</summary>
    public bool TryCopyTo(Span<byte> destination)
    {
        var source = Span;
        if (source.Length > destination.Length)
        {
            return false;
        }

        source.CopyTo(destination);
        return true;
    }

    /// <summary>Decodes the payload as a UTF-8 string (allocates).</summary>
    public override string ToString() => Value.AsString();

    /// <summary>Returns the pooled buffer. Safe to call more than once (and on copies).</summary>
    public void Dispose() => _owner?.Dispose();
}
