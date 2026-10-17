using Microsoft.Extensions.Logging;

namespace Respire;

internal static partial class RespireLog
{
    [LoggerMessage(140, LogLevel.Information, "Recycled Redis connection to {Host}:{Port} after its maximum lifetime", EventName = "ConnectionRecycled")]
    private static partial void ConnectionRecycledMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionRecycled(this ILogger logger, string host, int port)
        => ConnectionRecycledMessage(logger, host, port, null);
}
