using Microsoft.Extensions.Logging;

namespace Respire.OutputCaching;

// Keep legacy EventId values and names, including unnamed event zero.
// Cached typed delegates check IsEnabled before boxing or creating log state.
internal static class OutputCacheLog
{
    private static readonly Action<ILogger, Exception?> OutputCacheTagCleanupFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Respire output-cache tag cleanup failed.");

    internal static void OutputCacheTagCleanupFailed(this ILogger logger, Exception? error)
        => OutputCacheTagCleanupFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> OutputCacheCleanupLockLostMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Respire output-cache cleanup lost its lock; skipping the remaining sweep and master purge.");

    internal static void OutputCacheCleanupLockLost(this ILogger logger)
        => OutputCacheCleanupLockLostMessage(logger, null);

}
