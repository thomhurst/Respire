using Respire.Compression;

namespace Respire.Caching;

/// <summary>
/// Cache behaviour for <see cref="RespireDistributedCache"/>. Used where a client is already
/// supplied; see <see cref="RespireCacheRegistrationOptions"/> for DI registrations that can
/// create their own client.
/// </summary>
/// <remarks>
/// Inheritance supports <see cref="RespireCacheRegistrationOptions"/>; this type is not intended
/// as an extension point for external subclasses.
/// </remarks>
public class RespireCacheOptions
{
    /// <summary>
    /// Prefix prepended to every cache key, so several apps (or caches) can share one Redis
    /// without colliding. Same semantics as the Microsoft Redis cache's InstanceName.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// Optional codec for the cache's data field, including HybridCache L2 payloads.
    /// Null preserves raw bytes and Microsoft Redis cache interoperability.
    /// </summary>
    /// <remarks>
    /// Captured when the cache is constructed; independent of the client's serializer.
    /// All readers and writers sharing a key namespace must use compatible codecs.
    /// Built-in codecs reject unframed legacy entries. Implementations must be thread-safe.
    /// Built-in buffer decoders advance the destination only on success but may modify uncommitted memory.
    /// Custom decoders control their own partial-output behavior; the cache cannot roll back writer changes.
    /// </remarks>
    public IRespireValueCodec? ValueCodec { get; set; }
}
