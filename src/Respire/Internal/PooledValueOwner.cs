using Respire.Protocol;

namespace Respire.Internal;

/// <summary>
/// Idempotent-dispose owner for a pooled <see cref="RespValue"/>. Public lease types
/// (<see cref="RespireResult"/>, <see cref="RespireLease"/>) are structs, so copies share this
/// owner — no matter how many copies get disposed, the pooled buffers return exactly once
/// (double-return would let the pool hand the same array to a concurrent response). After
/// disposal the value reads as null/empty rather than as freed memory.
/// </summary>
internal sealed class PooledValueOwner(in RespValue value)
{
    private RespValue _value = value;
    private int _disposed;

    public RespValue Value => Volatile.Read(ref _disposed) == 0 ? _value : RespValue.Null;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _value.Dispose();
            _value = default;
        }
    }
}
