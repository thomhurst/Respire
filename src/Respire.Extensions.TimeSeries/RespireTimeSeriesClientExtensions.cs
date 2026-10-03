using System.Runtime.CompilerServices;

namespace Respire.Extensions.TimeSeries;

/// <summary>Provides RedisTimeSeries operations as an extension property on a Respire client.</summary>
public static class RespireTimeSeriesClientExtensions
{
    private static readonly ConditionalWeakTable<IRespireClient, RespireTimeSeriesClient> Clients = new();

    extension(IRespireClient client)
    {
        /// <summary>Gets the RedisTimeSeries operations for this client.</summary>
        /// <remarks>
        /// Reuses the same wrapper for this client instance, including under concurrent access.
        /// Reading this property performs no network I/O. The caller owns the underlying client;
        /// the wrapper preserves its key prefix and routing behavior.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public RespireTimeSeriesClient TimeSeries
        {
            get
            {
                ArgumentNullException.ThrowIfNull(client);
                return Clients.GetValue(client, static client => new RespireTimeSeriesClient(client));
            }
        }
    }
}
