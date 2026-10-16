using Microsoft.Extensions.Logging;

namespace Respire.Idempotency;

internal static partial class IdempotencyLog
{
    [LoggerMessage(1, LogLevel.Warning, "Idempotent response exceeded the configured buffer limit.", EventName = "ResponseLimitExceeded")]
    internal static partial void ResponseLimitExceeded(ILogger logger);

    [LoggerMessage(2, LogLevel.Warning, "Could not abandon an idempotency claim; it will expire at its TTL.", EventName = "AbandonFailed")]
    internal static partial void AbandonFailed(ILogger logger, Exception error);

    [LoggerMessage(3, LogLevel.Warning, "Could not store an idempotent response; delivering the buffered response and attempting claim cleanup.", EventName = "CompletionFailed")]
    internal static partial void CompletionFailed(ILogger logger, Exception error);
}
