using Microsoft.Extensions.Logging;

namespace Respire.Internal;

internal static partial class ConfigurationLog
{
    [LoggerMessage(141, LogLevel.Warning,
        "Connection string option '{Option}' is ignored by Respire. Create connects lazily; ConnectAsync waits for a connection. Other ignored settings use Respire defaults.",
        EventName = "IgnoredConnectionStringOption")]
    internal static partial void IgnoredConnectionStringOption(ILogger logger, string option);
}
