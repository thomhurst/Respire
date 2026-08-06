using System.Buffers;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// Dedicated, bounded array pools for the wire path.
/// </summary>
/// <remarks>
/// <see cref="ArrayPool{T}.Shared"/> keeps per-thread/per-core stacks that grow but never
/// shrink; buffers rented on one thread and returned on another (the norm here — commands are
/// serialized on caller threads, responses are completed on the receive loop) accumulate in
/// every visited thread's cache, so the retained working set grows with thread count. Dedicated
/// <see cref="ArrayPool{T}.Create(int,int)"/> pools use a single bounded store instead.
/// </remarks>
internal static class RespirePools
{
    /// <summary>Outgoing command serialization buffers (write coalescing buffers).</summary>
    public static readonly ArrayPool<byte> WriteBuffers = ArrayPool<byte>.Create(4 * 1024 * 1024, 32);

    /// <summary>Response payload storage handed to <see cref="RespireValue"/> instances.</summary>
    public static readonly ArrayPool<byte> ResponsePayloads = ArrayPool<byte>.Create(64 * 1024 * 1024, 64);

    /// <summary>Element storage for RESP array/map/set responses.</summary>
    public static readonly ArrayPool<RespireValue> ValueArrays = ArrayPool<RespireValue>.Create(64 * 1024, 64);
}
