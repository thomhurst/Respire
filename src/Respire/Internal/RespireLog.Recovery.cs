using Microsoft.Extensions.Logging;

namespace Respire;

internal static partial class RespireLog
{
    private static readonly Action<ILogger, RespireEndpoint, Exception?> NearestReadCandidateUnavailableMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Nearest read candidate unavailable at {Endpoint}");

    internal static void NearestReadCandidateUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => NearestReadCandidateUnavailableMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> ReadReplicaUnavailableMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Read replica unavailable at {Endpoint}");

    internal static void ReadReplicaUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => ReadReplicaUnavailableMessage(logger, endpoint, error);

    private static readonly Action<ILogger, int, string, int, long, int, Exception?> ClusterMigrationBudgetRejectedMessage =
        LoggerMessage.Define<int, string, int, long, int>(LogLevel.Debug, default,
            "Rejected {Count} Cluster SMIGRATED entries from {Host}:{Port} (sequence {Sequence}): slot ranges exceed {Limit} slots in total.");

    internal static void ClusterMigrationBudgetRejected(this ILogger logger, int count, string host, int port, long sequence, int limit)
        => ClusterMigrationBudgetRejectedMessage(logger, count, host, port, sequence, limit, null);

    private static readonly Action<ILogger, string, int, long, string, Exception?> ClusterMigrationSlotsRejectedMessage =
        LoggerMessage.Define<string, int, long, string>(LogLevel.Debug, default,
            "Rejected a Cluster SMIGRATED entry from {Host}:{Port} (sequence {Sequence}): invalid slot list {Slots}.");

    internal static void ClusterMigrationSlotsRejected(this ILogger logger, string host, int port, long sequence, string slots)
        => ClusterMigrationSlotsRejectedMessage(logger, host, port, sequence, slots, null);

    private static readonly Action<ILogger, Exception?> ClusterTopologyRefreshFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Redis Cluster topology refresh failed");

    internal static void ClusterTopologyRefreshFailed(this ILogger logger, Exception? error)
        => ClusterTopologyRefreshFailedMessage(logger, error);

    private static readonly Action<ILogger, int, TimeSpan, Exception?> ClusterTopologyRefreshRetryMessage =
        LoggerMessage.Define<int, TimeSpan>(LogLevel.Warning, default,
            "Redis Cluster topology refresh failed {ConsecutiveFailures} consecutive time(s); retrying in {RetryDelay}");

    internal static void ClusterTopologyRefreshRetry(this ILogger logger, int consecutiveFailures, TimeSpan retryDelay)
        => ClusterTopologyRefreshRetryMessage(logger, consecutiveFailures, retryDelay, null);

    private static readonly Action<ILogger, Exception?> ClusterTopologyWorkerFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Redis Cluster topology refresh worker failed; continuing");

    internal static void ClusterTopologyWorkerFailed(this ILogger logger, Exception? error)
        => ClusterTopologyWorkerFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ClusterReadonlyRecoveryFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Redis Cluster READONLY recovery failed");

    internal static void ClusterReadonlyRecoveryFailed(this ILogger logger, Exception? error)
        => ClusterReadonlyRecoveryFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ClusterReadonlyRecoveryBeforeRefreshFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Redis Cluster READONLY recovery failed before topology refresh");

    internal static void ClusterReadonlyRecoveryBeforeRefreshFailed(this ILogger logger, Exception? error)
        => ClusterReadonlyRecoveryBeforeRefreshFailedMessage(logger, error);

    private static readonly Action<ILogger, int, Exception?> ClusterTopologyCandidatesFailedMessage =
        LoggerMessage.Define<int>(LogLevel.Debug, default, "Redis Cluster topology refresh failed for all {CandidateCount} candidates");

    internal static void ClusterTopologyCandidatesFailed(this ILogger logger, int candidateCount)
        => ClusterTopologyCandidatesFailedMessage(logger, candidateCount, null);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorEstablishedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Information, default,
            "Sentinel monitor established at {Sentinel}; revalidating the primary after subscription");

    internal static void SentinelMonitorEstablished(this ILogger logger, RespireEndpoint sentinel)
        => SentinelMonitorEstablishedMessage(logger, sentinel, null);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelDeliveryGapMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Information, default,
            "Sentinel event delivery from {Sentinel} had a gap; rediscovering the primary");

    internal static void SentinelDeliveryGap(this ILogger logger, RespireEndpoint sentinel)
        => SentinelDeliveryGapMessage(logger, sentinel, null);

    private static readonly Action<ILogger, int, Exception?> SentinelNotificationFirstDiscoveryFailureMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default,
            "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})");

    internal static void SentinelNotificationFirstDiscoveryFailure(this ILogger logger, int attempt, Exception? error)
        => SentinelNotificationFirstDiscoveryFailureMessage(logger, attempt, error);

    private static readonly Action<ILogger, int, Exception?> SentinelNotificationRepeatedDiscoveryFailureMessage =
        LoggerMessage.Define<int>(LogLevel.Debug, default,
            "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})");

    internal static void SentinelNotificationRepeatedDiscoveryFailure(this ILogger logger, int attempt, Exception? error)
        => SentinelNotificationRepeatedDiscoveryFailureMessage(logger, attempt, error);

    private static readonly Action<ILogger, string, string?, RespireEndpoint, string, Exception?>[] SentinelEventMessages =
    [
        DefineSentinelEvent(LogLevel.Trace),
        DefineSentinelEvent(LogLevel.Debug),
        DefineSentinelEvent(LogLevel.Information),
        DefineSentinelEvent(LogLevel.Warning),
        DefineSentinelEvent(LogLevel.Error),
        DefineSentinelEvent(LogLevel.Critical),
        DefineSentinelEvent(LogLevel.None),
    ];

    private static Action<ILogger, string, string?, RespireEndpoint, string, Exception?> DefineSentinelEvent(LogLevel level)
        => LoggerMessage.Define<string, string?, RespireEndpoint, string>(level, default,
            "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}");

    internal static void SentinelEvent(this ILogger logger, LogLevel level, string channel, string? service, RespireEndpoint sentinel, string text)
        => SentinelEventMessages[(int)level](logger, channel, service, sentinel, text, null);
}
