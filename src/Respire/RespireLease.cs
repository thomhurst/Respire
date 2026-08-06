using Respire.Protocol;

namespace Respire;

/// <summary>
/// A zero-copy read result backed by pooled memory. Valid until <see cref="Dispose"/>; the
/// friendly APIs (<c>GetStringAsync</c>, …) never hand out pooled memory — only methods with
/// "Lease" in the name do, so the disposal obligation is always visible at the call site.
/// </summary>
public struct RespireLease : IDisposable
{
    private RespValue _value;
    private bool _disposed;

    internal RespireLease(in RespValue value)
    {
        _value = value;
    }

    /// <summary>True when the key was missing.</summary>
    public readonly bool IsNull => _value.IsNull;

    public readonly int Length => Span.Length;

    /// <summary>The payload. Do not use after <see cref="Dispose"/>.</summary>
    public readonly ReadOnlySpan<byte> Span => _value.AsSpan();

    /// <summary>Decodes the payload as a UTF-8 string (allocates).</summary>
    public readonly override string ToString() => _value.AsString();

    /// <summary>Returns the pooled buffer. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _value.Dispose();
        _value = default;
    }
}
