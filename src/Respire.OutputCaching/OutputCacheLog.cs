using Microsoft.Extensions.Logging;

namespace Respire.OutputCaching;

// Event IDs and names are stable within this package; see website/docs/integrations/log-events.md.
// Keep expensive formatting behind IsEnabled in the helper methods below.
internal static partial class OutputCacheLog
{
    [LoggerMessage(1, LogLevel.Warning, "Respire output-cache tag cleanup failed.", EventName = "OutputCacheTagCleanupFailed")]
    private static partial void OutputCacheTagCleanupFailedMessage(ILogger logger, Exception? exception);

    internal static void OutputCacheTagCleanupFailed(this ILogger logger, Exception? error)
        => OutputCacheTagCleanupFailedMessage(logger, error);

    [LoggerMessage(2, LogLevel.Debug, "Respire output-cache cleanup lost its lock; skipping the remaining sweep and master purge.", EventName = "OutputCacheCleanupLockLost")]
    private static partial void OutputCacheCleanupLockLostMessage(ILogger logger, Exception? exception);

    internal static void OutputCacheCleanupLockLost(this ILogger logger)
        => OutputCacheCleanupLockLostMessage(logger, null);

}
