using System.Buffers;
using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// Dedicated, bounded array pools for the wire path.
/// </summary>
/// <remarks>
/// <see cref="ArrayPool{T}.Shared"/> uses per-thread/per-core caches, so its retention is
/// not bounded by one fixed response-storage budget. Response pools use fixed shared buckets
/// that accept cross-thread returns; write buffers retain their bounded BCL pool.
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
        => new BoundedResponseArrayPool<byte>(4096, 1024 * 1024, MaxPooledResponsePayloadLength);

    internal static ArrayPool<RespValue> CreateValueArrayPool()
        => new BoundedResponseArrayPool<RespValue>(64, 1024, 64 * 1024);
}
