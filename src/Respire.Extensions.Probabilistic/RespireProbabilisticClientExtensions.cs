using System.Runtime.CompilerServices;

namespace Respire.Extensions.Probabilistic;

/// <summary>Provides probabilistic operations as an extension property on a Respire client.</summary>
public static class RespireProbabilisticClientExtensions
{
    private static readonly ConditionalWeakTable<IRespireClient, RespireProbabilisticClient> Clients = new();

    extension(IRespireClient client)
    {
        /// <summary>Gets the probabilistic operations for this client.</summary>
        /// <remarks>
        /// Reuses the same wrapper for this client instance, including under concurrent access.
        /// Reading this property performs no network I/O. The caller owns the underlying client;
        /// the wrapper preserves its key prefix and routing behavior.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public RespireProbabilisticClient Probabilistic
        {
            get
            {
                ArgumentNullException.ThrowIfNull(client);
                return Clients.GetValue(client, static client => new RespireProbabilisticClient(client));
            }
        }
    }
}

