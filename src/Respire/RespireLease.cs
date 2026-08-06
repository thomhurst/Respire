using Respire.Internal;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A zero-copy read result backed by pooled memory. Valid until <see cref="Dispose"/>; the
/// friendly APIs (<c>GetStringAsync</c>, …) never hand out pooled memory — only methods with
/// "Lease" in the name do, so the disposal obligation is always visible at the call site.
/// Copies share one owner: disposal is idempotent across copies, and a disposed lease reads
/// as null/empty.
/// </summary>
public readonly struct RespireLease : IDisposable
{
    private readonly PooledValueOwner? _owner;

    internal RespireLease(in RespValue value) => _owner = new PooledValueOwner(in value);

    private RespValue Value => _owner?.Value ?? default;

    /// <summary>True when the key was missing (or the lease has been disposed).</summary>
    public bool IsNull => Value.IsNull;

    public int Length => Span.Length;

    /// <summary>The payload. Do not use after <see cref="Dispose"/>.</summary>
    public ReadOnlySpan<byte> Span => Value.AsSpan();

    /// <summary>Decodes the payload as a UTF-8 string (allocates).</summary>
    public override string ToString() => Value.AsString();

    /// <summary>Returns the pooled buffer. Safe to call more than once (and on copies).</summary>
    public void Dispose() => _owner?.Dispose();
}
