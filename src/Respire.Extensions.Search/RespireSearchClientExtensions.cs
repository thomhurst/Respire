using System.Runtime.CompilerServices;
using Respire;

namespace Redis.Search;

/// <summary>Provides Redis Search operations as an extension property on a Respire client.</summary>
public static class RespireSearchClientExtensions
{
    private static readonly ConditionalWeakTable<IRespireClient, RespireSearchClient> Clients = new();

    extension(IRespireClient client)
    {
        /// <summary>Gets the Redis Search operations for this client.</summary>
        /// <remarks>
        /// Reuses the same wrapper for this client instance, including under concurrent access.
        /// Reading this property performs no network I/O. The caller owns the underlying client;
        /// the wrapper preserves its key prefix and routing behavior.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public RespireSearchClient Search
        {
            get
            {
                ArgumentNullException.ThrowIfNull(client);
                return Clients.GetValue(client, static client => new RespireSearchClient(client));
            }
        }
    }
}
