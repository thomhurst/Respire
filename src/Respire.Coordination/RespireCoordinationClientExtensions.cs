using System.Runtime.CompilerServices;

namespace Respire.Coordination;

/// <summary>Provides coordination operations on an existing Respire client.</summary>
public static class RespireCoordinationClientExtensions
{
    private static readonly ConditionalWeakTable<IRespireClient, RespireCoordination> Clients = new();

    extension(IRespireClient client)
    {
        /// <summary>Gets the coordination operations for this client.</summary>
        /// <remarks>
        /// Reuses the same wrapper for this client instance, including under concurrent access.
        /// Reading this property performs no network I/O. The caller retains ownership of the client.
        /// Key-prefixed views get their own wrapper and preserve their configured prefix.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The client is null.</exception>
        public RespireCoordination Coordination
        {
            get
            {
                ArgumentNullException.ThrowIfNull(client);
                return Clients.GetValue(client, static client => new RespireCoordination(client));
            }
        }
    }
}
