namespace Respire.Caching;

/// <summary>Creates distributed-cache adapters over existing Respire clients.</summary>
public static class RespireDistributedCacheClientExtensions
{
    /// <summary>Creates a distributed-cache adapter without taking ownership of the client.</summary>
    /// <param name="client">Caller-owned Respire client.</param>
    /// <param name="options">Optional cache key prefix and value codec.</param>
    /// <returns>A new adapter using the supplied client and cache options.</returns>
    /// <remarks>
    /// Performs no network I/O. Disposing the adapter does not dispose the client.
    /// The instance name is appended to any existing client key prefix.
    /// If passed a <see cref="RespireCacheRegistrationOptions"/> instance, its connection settings
    /// are ignored because the adapter uses the supplied client.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    public static RespireDistributedCache AsDistributedCache(this IRespireClient client, RespireCacheOptions? options = null)
        => new(client, options);
}
