using Microsoft.Extensions.Logging;

namespace Respire;

internal static partial class RespireLog
{
    [LoggerMessage(125, LogLevel.Debug, "Nearest read candidate unavailable at {Endpoint}", EventName = "NearestReadCandidateUnavailable")]
    private static partial void NearestReadCandidateUnavailableMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void NearestReadCandidateUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => NearestReadCandidateUnavailableMessage(logger, endpoint, error);

    [LoggerMessage(126, LogLevel.Debug, "Read replica unavailable at {Endpoint}", EventName = "ReadReplicaUnavailable")]
    private static partial void ReadReplicaUnavailableMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void ReadReplicaUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => ReadReplicaUnavailableMessage(logger, endpoint, error);

    [LoggerMessage(127, LogLevel.Debug, "Rejected {Count} Cluster SMIGRATED entries from {Host}:{Port} (sequence {Sequence}): slot ranges exceed {Limit} slots in total.", EventName = "ClusterMigrationBudgetRejected")]
    private static partial void ClusterMigrationBudgetRejectedMessage(ILogger logger, int count, string host, int port, long sequence, int limit, Exception? exception);

    internal static void ClusterMigrationBudgetRejected(this ILogger logger, int count, string host, int port, long sequence, int limit)
        => ClusterMigrationBudgetRejectedMessage(logger, count, host, port, sequence, limit, null);

    [LoggerMessage(128, LogLevel.Debug, "Rejected a Cluster SMIGRATED entry from {Host}:{Port} (sequence {Sequence}): invalid slot list {Slots}.", EventName = "ClusterMigrationSlotsRejected")]
    private static partial void ClusterMigrationSlotsRejectedMessage(ILogger logger, string host, int port, long sequence, string slots, Exception? exception);

    internal static void ClusterMigrationSlotsRejected(this ILogger logger, string host, int port, long sequence, string slots)
        => ClusterMigrationSlotsRejectedMessage(logger, host, port, sequence, slots, null);

    [LoggerMessage(129, LogLevel.Debug, "Redis Cluster topology refresh failed", EventName = "ClusterTopologyRefreshFailed")]
    private static partial void ClusterTopologyRefreshFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterTopologyRefreshFailed(this ILogger logger, Exception? error)
        => ClusterTopologyRefreshFailedMessage(logger, error);

    [LoggerMessage(130, LogLevel.Warning, "Redis Cluster topology refresh failed {ConsecutiveFailures} consecutive time(s); retrying in {RetryDelay}", EventName = "ClusterTopologyRefreshRetry")]
    private static partial void ClusterTopologyRefreshRetryMessage(ILogger logger, int consecutiveFailures, TimeSpan retryDelay, Exception? exception);

    internal static void ClusterTopologyRefreshRetry(this ILogger logger, int consecutiveFailures, TimeSpan retryDelay)
        => ClusterTopologyRefreshRetryMessage(logger, consecutiveFailures, retryDelay, null);

    [LoggerMessage(131, LogLevel.Warning, "Redis Cluster topology refresh worker failed; continuing", EventName = "ClusterTopologyWorkerFailed")]
    private static partial void ClusterTopologyWorkerFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterTopologyWorkerFailed(this ILogger logger, Exception? error)
        => ClusterTopologyWorkerFailedMessage(logger, error);

    [LoggerMessage(132, LogLevel.Debug, "Redis Cluster READONLY recovery failed", EventName = "ClusterReadonlyRecoveryFailed")]
    private static partial void ClusterReadonlyRecoveryFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterReadonlyRecoveryFailed(this ILogger logger, Exception? error)
        => ClusterReadonlyRecoveryFailedMessage(logger, error);

    [LoggerMessage(133, LogLevel.Debug, "Redis Cluster READONLY recovery failed before topology refresh", EventName = "ClusterReadonlyRecoveryBeforeRefreshFailed")]
    private static partial void ClusterReadonlyRecoveryBeforeRefreshFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterReadonlyRecoveryBeforeRefreshFailed(this ILogger logger, Exception? error)
        => ClusterReadonlyRecoveryBeforeRefreshFailedMessage(logger, error);

    [LoggerMessage(134, LogLevel.Debug, "Redis Cluster topology refresh failed for all {CandidateCount} candidates", EventName = "ClusterTopologyCandidatesFailed")]
    private static partial void ClusterTopologyCandidatesFailedMessage(ILogger logger, int candidateCount, Exception? exception);

    internal static void ClusterTopologyCandidatesFailed(this ILogger logger, int candidateCount)
        => ClusterTopologyCandidatesFailedMessage(logger, candidateCount, null);

    [LoggerMessage(135, LogLevel.Information, "Sentinel monitor established at {Sentinel}; revalidating the primary after subscription", EventName = "SentinelMonitorEstablished")]
    private static partial void SentinelMonitorEstablishedMessage(ILogger logger, RespireEndpoint sentinel, Exception? exception);

    internal static void SentinelMonitorEstablished(this ILogger logger, RespireEndpoint sentinel)
        => SentinelMonitorEstablishedMessage(logger, sentinel, null);

    [LoggerMessage(136, LogLevel.Information, "Sentinel event delivery from {Sentinel} had a gap; rediscovering the primary", EventName = "SentinelDeliveryGap")]
    private static partial void SentinelDeliveryGapMessage(ILogger logger, RespireEndpoint sentinel, Exception? exception);

    internal static void SentinelDeliveryGap(this ILogger logger, RespireEndpoint sentinel)
        => SentinelDeliveryGapMessage(logger, sentinel, null);

    [LoggerMessage(137, LogLevel.Warning, "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})", EventName = "SentinelNotificationFirstDiscoveryFailure")]
    private static partial void SentinelNotificationFirstDiscoveryFailureMessage(ILogger logger, int attempt, Exception? exception);

    internal static void SentinelNotificationFirstDiscoveryFailure(this ILogger logger, int attempt, Exception? error)
        => SentinelNotificationFirstDiscoveryFailureMessage(logger, attempt, error);

    [LoggerMessage(138, LogLevel.Debug, "Sentinel notification-triggered primary discovery failed (consecutive failure {Attempt})", EventName = "SentinelNotificationRepeatedDiscoveryFailure")]
    private static partial void SentinelNotificationRepeatedDiscoveryFailureMessage(ILogger logger, int attempt, Exception? exception);

    internal static void SentinelNotificationRepeatedDiscoveryFailure(this ILogger logger, int attempt, Exception? error)
        => SentinelNotificationRepeatedDiscoveryFailureMessage(logger, attempt, error);

    [LoggerMessage(EventId = 139, Message = "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}", EventName = "SentinelEvent")]
    internal static partial void SentinelEvent(this ILogger logger, LogLevel level, string channel, string? service, RespireEndpoint sentinel, string Event);
}
