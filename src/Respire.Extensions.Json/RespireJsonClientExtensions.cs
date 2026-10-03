using System.Runtime.CompilerServices;

namespace Respire.Extensions.Json;

/// <summary>Provides RedisJSON operations as an extension property on a Respire client.</summary>
public static class RespireJsonClientExtensions
{
    private static readonly ConditionalWeakTable<IRespireClient, RespireJsonClient> Clients = new();

    extension(IRespireClient client)
    {
        /// <summary>Gets the RedisJSON operations for this client.</summary>
        /// <remarks>
        /// Reuses the same wrapper for this client instance, including under concurrent access.
        /// Reading this property performs no network I/O. The caller owns the underlying client;
        /// the wrapper preserves its key prefix and routing behavior.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public RespireJsonClient Json
        {
            get
            {
                ArgumentNullException.ThrowIfNull(client);
                return Clients.GetValue(client, static client => new RespireJsonClient(client));
            }
        }
    }
}

