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

    internal const int MaxPooledResponsePayloadLength = 64 * 1024 * 1024;

    /// <summary>Response payload storage handed to <see cref="RespValue"/> instances.</summary>
    public static readonly ArrayPool<byte> ResponsePayloads = CreateResponsePayloadPool();

    /// <summary>Element storage for RESP array/map/set responses.</summary>
    public static readonly ArrayPool<RespValue> ValueArrays = CreateValueArrayPool();

    internal static ArrayPool<byte> CreateResponsePayloadPool()
        => ArrayPool<byte>.Create(MaxPooledResponsePayloadLength, 64);

    internal static ArrayPool<RespValue> CreateValueArrayPool()
        => ArrayPool<RespValue>.Create(64 * 1024, 64);
}
