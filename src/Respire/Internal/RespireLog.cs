using Microsoft.Extensions.Logging;
using Respire.Protocol;

namespace Respire;

// Event IDs and names are stable within this package; see website/docs/integrations/log-events.md.
// Keep expensive formatting behind IsEnabled in the helper methods below.
internal static partial class RespireLog
{
    [LoggerMessage(143, LogLevel.Information, "Multipath TCP unavailable; using TCP. {Reason}", EventName = "MultipathTcpFallback")]
    private static partial void MultipathTcpFallbackMessage(ILogger logger, string reason, Exception? exception);

    internal static void MultipathTcpFallback(this ILogger logger, string reason)
        => MultipathTcpFallbackMessage(logger, reason, null);

    [LoggerMessage(1, LogLevel.Warning, "Stream read failed; retrying after {DelayMilliseconds} ms.", EventName = "StreamReadRetry")]
    private static partial void StreamReadRetryMessage(ILogger logger, int delayMilliseconds, Exception? exception);

    internal static void StreamReadRetry(this ILogger logger, int delayMilliseconds, Exception? error)
        => StreamReadRetryMessage(logger, delayMilliseconds, error);

    [LoggerMessage(2, LogLevel.Information, "Replaced dead connection {Slot} to {Host}:{Port}", EventName = "DeadConnectionReplaced")]
    private static partial void DeadConnectionReplacedMessage(ILogger logger, int slot, string host, int port, Exception? exception);

    internal static void DeadConnectionReplaced(this ILogger logger, int slot, string host, int port)
        => DeadConnectionReplacedMessage(logger, slot, host, port, null);

    [LoggerMessage(3, LogLevel.Warning, "Failed to dispose rejected replacement connection to {Host}:{Port}", EventName = "RejectedReplacementDisposalFailed")]
    private static partial void RejectedReplacementDisposalFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void RejectedReplacementDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => RejectedReplacementDisposalFailedMessage(logger, host, port, error);

    [LoggerMessage(4, LogLevel.Debug, "Reconnect of slot {Slot} failed after a healthy connection was published; keeping it", EventName = "ReconnectKeptHealthyConnection")]
    private static partial void ReconnectKeptHealthyConnectionMessage(ILogger logger, int slot, Exception? exception);

    internal static void ReconnectKeptHealthyConnection(this ILogger logger, int slot, Exception? error)
        => ReconnectKeptHealthyConnectionMessage(logger, slot, error);

    [LoggerMessage(5, LogLevel.Warning, "Reconnect to {Host}:{Port} exhausted its {Attempts} attempts", EventName = "ReconnectExhausted")]
    private static partial void ReconnectExhaustedMessage(ILogger logger, string host, int port, int attempts, Exception? exception);

    internal static void ReconnectExhausted(this ILogger logger, string host, int port, int attempts, Exception? error)
        => ReconnectExhaustedMessage(logger, host, port, attempts, error);

    [LoggerMessage(6, LogLevel.Warning, "Reconnect to {Host}:{Port} failed; next use will schedule another attempt with configured backoff", EventName = "ReconnectBackoffDeferred")]
    private static partial void ReconnectBackoffDeferredMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ReconnectBackoffDeferred(this ILogger logger, string host, int port, Exception? error)
        => ReconnectBackoffDeferredMessage(logger, host, port, error);

    [LoggerMessage(7, LogLevel.Warning, "Reconnect to {Host}:{Port} failed; will retry on next use", EventName = "ReconnectDeferred")]
    private static partial void ReconnectDeferredMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ReconnectDeferred(this ILogger logger, string host, int port, Exception? error)
        => ReconnectDeferredMessage(logger, host, port, error);

    [LoggerMessage(8, LogLevel.Warning, "Connection state-change handler threw", EventName = "ConnectionStateObserverFailed")]
    private static partial void ConnectionStateObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ConnectionStateObserverFailed(this ILogger logger, Exception? error)
        => ConnectionStateObserverFailedMessage(logger, error);

    [LoggerMessage(9, LogLevel.Warning, "Connection slot state-change handler threw", EventName = "ConnectionSlotObserverFailed")]
    private static partial void ConnectionSlotObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ConnectionSlotObserverFailed(this ILogger logger, Exception? error)
        => ConnectionSlotObserverFailedMessage(logger, error);

    [LoggerMessage(10, LogLevel.Warning, "Reconnect metrics listener threw", EventName = "ReconnectMetricObserverFailed")]
    private static partial void ReconnectMetricObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ReconnectMetricObserverFailed(this ILogger logger, Exception? error)
        => ReconnectMetricObserverFailedMessage(logger, error);

    [LoggerMessage(11, LogLevel.Debug, "Connection abort after maintenance barrier failure also failed at {Host}:{Port}", EventName = "MaintenanceBarrierAbortFailed")]
    private static partial void MaintenanceBarrierAbortFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MaintenanceBarrierAbortFailed(this ILogger logger, string host, int port, Exception? error)
        => MaintenanceBarrierAbortFailedMessage(logger, host, port, error);

    [LoggerMessage(12, LogLevel.Debug, "Maintenance drain barrier failed at {Host}:{Port}; retiring connection", EventName = "MaintenanceBarrierFailed")]
    private static partial void MaintenanceBarrierFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MaintenanceBarrierFailed(this ILogger logger, string host, int port, Exception? error)
        => MaintenanceBarrierFailedMessage(logger, host, port, error);

    [LoggerMessage(13, LogLevel.Debug, "Retirement did not finish cleanly before disposal of {Host}:{Port}", EventName = "RetirementDisposalFailed")]
    private static partial void RetirementDisposalFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void RetirementDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => RetirementDisposalFailedMessage(logger, host, port, error);

    [LoggerMessage(14, LogLevel.Debug, "MOVING to {Host}:{Port} supersedes the handoff to {PreviousHost}:{PreviousPort}", EventName = "MovingSuperseded")]
    private static partial void MovingSupersededMessage(ILogger logger, string host, int port, string previousHost, int previousPort, Exception? exception);

    internal static void MovingSuperseded(this ILogger logger, string host, int port, string previousHost, int previousPort)
        => MovingSupersededMessage(logger, host, port, previousHost, previousPort, null);

    [LoggerMessage(15, LogLevel.Warning, "MOVING handoff to {Host}:{Port} failed", EventName = "MovingFailed")]
    private static partial void MovingFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MovingFailed(this ILogger logger, string host, int port, Exception? error)
        => MovingFailedMessage(logger, host, port, error);

    [LoggerMessage(16, LogLevel.Debug, "MOVING handoff stopped by multiplexer retirement", EventName = "MovingRetirementStopped")]
    private static partial void MovingRetirementStoppedMessage(ILogger logger, Exception? exception);

    internal static void MovingRetirementStopped(this ILogger logger, Exception? error)
        => MovingRetirementStoppedMessage(logger, error);

    [LoggerMessage(17, LogLevel.Warning, "MOVING handoff to {Host}:{Port} failed within its grace period; keeping current connections", EventName = "MovingGraceAttemptFailed")]
    private static partial void MovingGraceAttemptFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MovingGraceAttemptFailed(this ILogger logger, string host, int port, Exception? error)
        => MovingGraceAttemptFailedMessage(logger, host, port, error);

    [LoggerMessage(18, LogLevel.Debug, "MOVING handoff to {Host}:{Port} failed; retrying", EventName = "MovingRetry")]
    private static partial void MovingRetryMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MovingRetry(this ILogger logger, string host, int port, Exception? error)
        => MovingRetryMessage(logger, host, port, error);

    [LoggerMessage(19, LogLevel.Debug, "MOVING handoff to {Host}:{Port} superseded before publication", EventName = "MovingPublicationSuperseded")]
    private static partial void MovingPublicationSupersededMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void MovingPublicationSuperseded(this ILogger logger, string host, int port)
        => MovingPublicationSupersededMessage(logger, host, port, null);

    [LoggerMessage(20, LogLevel.Warning, "MOVING handoff to {Host}:{Port} published {LateMilliseconds} ms after its grace period; aborting old sockets, so commands they had accepted may fail", EventName = "MovingPublishedLate")]
    private static partial void MovingPublishedLateMessage(ILogger logger, string host, int port, long lateMilliseconds, Exception? exception);

    internal static void MovingPublishedLate(this ILogger logger, string host, int port, long lateMilliseconds)
        => MovingPublishedLateMessage(logger, host, port, lateMilliseconds, null);

    [LoggerMessage(21, LogLevel.Debug, "MOVING retirement cache fence observer failed", EventName = "MovingCacheFenceObserverFailed")]
    private static partial void MovingCacheFenceObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void MovingCacheFenceObserverFailed(this ILogger logger, Exception? error)
        => MovingCacheFenceObserverFailedMessage(logger, error);

    [LoggerMessage(22, LogLevel.Debug, "Continuity flush metrics observer failed", EventName = "ContinuityFlushObserverFailed")]
    private static partial void ContinuityFlushObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ContinuityFlushObserverFailed(this ILogger logger, Exception? error)
        => ContinuityFlushObserverFailedMessage(logger, error);

    [LoggerMessage(23, LogLevel.Warning, "Fencing old MOVING sockets failed", EventName = "MovingSocketFenceFailed")]
    private static partial void MovingSocketFenceFailedMessage(ILogger logger, Exception? exception);

    internal static void MovingSocketFenceFailed(this ILogger logger, Exception? error)
        => MovingSocketFenceFailedMessage(logger, error);

    [LoggerMessage(24, LogLevel.Debug, "Old MOVING socket drain stopped by multiplexer retirement", EventName = "MovingSocketDrainStopped")]
    private static partial void MovingSocketDrainStoppedMessage(ILogger logger, Exception? exception);

    internal static void MovingSocketDrainStopped(this ILogger logger, Exception? error)
        => MovingSocketDrainStoppedMessage(logger, error);

    [LoggerMessage(25, LogLevel.Warning, "MOVING handoff drain exceeded its advertised grace period; aborting remaining old sockets", EventName = "MovingDrainGraceExceeded")]
    private static partial void MovingDrainGraceExceededMessage(ILogger logger, Exception? exception);

    internal static void MovingDrainGraceExceeded(this ILogger logger)
        => MovingDrainGraceExceededMessage(logger, null);

    [LoggerMessage(26, LogLevel.Debug, "Disposal ended the MOVING handoff drain; aborting remaining old sockets", EventName = "MovingDrainDisposalStopped")]
    private static partial void MovingDrainDisposalStoppedMessage(ILogger logger, Exception? exception);

    internal static void MovingDrainDisposalStopped(this ILogger logger)
        => MovingDrainDisposalStoppedMessage(logger, null);

    [LoggerMessage(27, LogLevel.Warning, "MOVING handoff drain of old sockets failed; aborting remaining old sockets", EventName = "MovingDrainFailed")]
    private static partial void MovingDrainFailedMessage(ILogger logger, Exception? exception);

    internal static void MovingDrainFailed(this ILogger logger, Exception? error)
        => MovingDrainFailedMessage(logger, error);

    [LoggerMessage(28, LogLevel.Debug, "Aborting an old MOVING socket failed", EventName = "MovingSocketAbortFailed")]
    private static partial void MovingSocketAbortFailedMessage(ILogger logger, Exception? exception);

    internal static void MovingSocketAbortFailed(this ILogger logger, Exception? error)
        => MovingSocketAbortFailedMessage(logger, error);

    [LoggerMessage(29, LogLevel.Debug, "Old MOVING sockets completed after drain cleanup", EventName = "MovingSocketDrainCleanupCompleted")]
    private static partial void MovingSocketDrainCleanupCompletedMessage(ILogger logger, Exception? exception);

    internal static void MovingSocketDrainCleanupCompleted(this ILogger logger, Exception? error)
        => MovingSocketDrainCleanupCompletedMessage(logger, error);

    [LoggerMessage(30, LogLevel.Warning, "Dedicated connection cleanup after MOVING failed", EventName = "DedicatedMovingCleanupFailed")]
    private static partial void DedicatedMovingCleanupFailedMessage(ILogger logger, Exception? exception);

    internal static void DedicatedMovingCleanupFailed(this ILogger logger, Exception? error)
        => DedicatedMovingCleanupFailedMessage(logger, error);

    [LoggerMessage(31, LogLevel.Debug, "Skipping Redis Cluster slots {Start}-{End}: the preferred endpoint is unknown ('?').", EventName = "ClusterUnknownEndpointSkipped")]
    private static partial void ClusterUnknownEndpointSkippedMessage(ILogger logger, long start, long end, Exception? exception);

    internal static void ClusterUnknownEndpointSkipped(this ILogger logger, long start, long end)
        => ClusterUnknownEndpointSkippedMessage(logger, start, end, null);

    [LoggerMessage(32, LogLevel.Warning, "Cluster discovery observer threw", EventName = "ClusterDiscoveryObserverFailed")]
    private static partial void ClusterDiscoveryObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterDiscoveryObserverFailed(this ILogger logger, Exception? error)
        => ClusterDiscoveryObserverFailedMessage(logger, error);

    [LoggerMessage(33, LogLevel.Error, "Error recording clustered migration-drop metric.", EventName = "ClusterMigrationDropMetricFailed")]
    private static partial void ClusterMigrationDropMetricFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterMigrationDropMetricFailed(this ILogger logger, Exception? error)
        => ClusterMigrationDropMetricFailedMessage(logger, error);

    [LoggerMessage(34, LogLevel.Warning, "Cluster SMIGRATED queue is full; {Dropped} notifications dropped so far. MOVED handling and topology discovery will correct the affected slots.", EventName = "ClusterMigrationQueueFull")]
    private static partial void ClusterMigrationQueueFullMessage(ILogger logger, long dropped, Exception? exception);

    internal static void ClusterMigrationQueueFull(this ILogger logger, long dropped)
        => ClusterMigrationQueueFullMessage(logger, dropped, null);

    [LoggerMessage(35, LogLevel.Warning, "Cluster slot migration metric listener threw.", EventName = "ClusterMigrationMetricObserverFailed")]
    private static partial void ClusterMigrationMetricObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterMigrationMetricObserverFailed(this ILogger logger, Exception? error)
        => ClusterMigrationMetricObserverFailedMessage(logger, error);

    [LoggerMessage(36, LogLevel.Error, "Failed to apply Cluster SMIGRATED notification from {Host}:{Port}.", EventName = "ClusterMigrationApplyFailed")]
    private static partial void ClusterMigrationApplyFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ClusterMigrationApplyFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterMigrationApplyFailedMessage(logger, host, port, error);

    [LoggerMessage(37, LogLevel.Debug, "Ignored duplicate Cluster SMIGRATED sequence {Sequence} from {Host}:{Port}.", EventName = "ClusterMigrationSequenceIgnored")]
    private static partial void ClusterMigrationSequenceIgnoredMessage(ILogger logger, long sequence, string host, int port, Exception? exception);

    internal static void ClusterMigrationSequenceIgnored(this ILogger logger, long sequence, string host, int port)
        => ClusterMigrationSequenceIgnoredMessage(logger, sequence, host, port, null);

    [LoggerMessage(38, LogLevel.Warning, "Replica route refresh for Redis Cluster slot {Slot} failed", EventName = "ClusterReplicaRouteRefreshFailed")]
    private static partial void ClusterReplicaRouteRefreshFailedMessage(ILogger logger, int slot, Exception? exception);

    internal static void ClusterReplicaRouteRefreshFailed(this ILogger logger, int slot, Exception? error)
        => ClusterReplicaRouteRefreshFailedMessage(logger, slot, error);

    [LoggerMessage(39, LogLevel.Debug, "Redis Cluster slot {Slot} returned {Code} on the preferred role; {ReadFrom} read retries on the other role", EventName = "ClusterReadRoleRetry")]
    private static partial void ClusterReadRoleRetryMessage(ILogger logger, int slot, string code, RespireReadFrom readFrom, Exception? exception);

    internal static void ClusterReadRoleRetry(this ILogger logger, int slot, string code, RespireReadFrom readFrom)
        => ClusterReadRoleRetryMessage(logger, slot, code, readFrom, null);

    [LoggerMessage(40, LogLevel.Warning, "Unable to refresh replica routes for Redis Cluster slot {Slot} from {Host}:{Port} after a redirect; {ReadFrom} read uses {Fallback}", EventName = "ClusterReplicaRedirectRefreshFailed")]
    private static partial void ClusterReplicaRedirectRefreshFailedMessage(ILogger logger, int slot, string host, int port, RespireReadFrom readFrom, string fallback, Exception? exception);

    internal static void ClusterReplicaRedirectRefreshFailed(this ILogger logger, int slot, string host, int port, RespireReadFrom readFrom, string fallback)
        => ClusterReplicaRedirectRefreshFailedMessage(logger, slot, host, port, readFrom, fallback, null);

    [LoggerMessage(41, LogLevel.Debug, "Cluster generation retirement needs correction cleanup at {Host}:{Port}", EventName = "ClusterRetirementCorrectionNeeded")]
    private static partial void ClusterRetirementCorrectionNeededMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ClusterRetirementCorrectionNeeded(this ILogger logger, string host, int port, Exception? error)
        => ClusterRetirementCorrectionNeededMessage(logger, host, port, error);

    [LoggerMessage(42, LogLevel.Warning, "Cluster fence still unavailable at {Host}:{Port}; retrying in {DelaySeconds}s; {RetiringGenerationCount} generations remain in retirement", EventName = "ClusterFenceUnavailable")]
    private static partial void ClusterFenceUnavailableMessage(ILogger logger, string host, int port, int delaySeconds, int retiringGenerationCount, Exception? exception);

    internal static void ClusterFenceUnavailable(this ILogger logger, string host, int port, int delaySeconds, int retiringGenerationCount, Exception? error)
        => ClusterFenceUnavailableMessage(logger, host, port, delaySeconds, retiringGenerationCount, error);

    [LoggerMessage(43, LogLevel.Debug, "Cluster fence retry failed at {Host}:{Port}; retrying in {DelaySeconds}s", EventName = "ClusterFenceRetry")]
    private static partial void ClusterFenceRetryMessage(ILogger logger, string host, int port, int delaySeconds, Exception? exception);

    internal static void ClusterFenceRetry(this ILogger logger, string host, int port, int delaySeconds, Exception? error)
        => ClusterFenceRetryMessage(logger, host, port, delaySeconds, error);

    [LoggerMessage(44, LogLevel.Warning, "Cluster generation retirement failed at {Host}:{Port}", EventName = "ClusterRetirementFailed")]
    private static partial void ClusterRetirementFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ClusterRetirementFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterRetirementFailedMessage(logger, host, port, error);

    [LoggerMessage(45, LogLevel.Warning, "Cluster generation cleanup failed at {Host}:{Port}", EventName = "ClusterGenerationCleanupFailed")]
    private static partial void ClusterGenerationCleanupFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ClusterGenerationCleanupFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterGenerationCleanupFailedMessage(logger, host, port, error);

    [LoggerMessage(46, LogLevel.Warning, "A retired Cluster pool reported a cleanup failure", EventName = "ClusterRetiredPoolCleanupFailed")]
    private static partial void ClusterRetiredPoolCleanupFailedMessage(ILogger logger, Exception? exception);

    internal static void ClusterRetiredPoolCleanupFailed(this ILogger logger, Exception? error)
        => ClusterRetiredPoolCleanupFailedMessage(logger, error);

    [LoggerMessage(47, LogLevel.Warning, "Failed to dispose a dedicated pool for {Host}:{Port}", EventName = "DedicatedPoolDisposalFailed")]
    private static partial void DedicatedPoolDisposalFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void DedicatedPoolDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => DedicatedPoolDisposalFailedMessage(logger, host, port, error);

    [LoggerMessage(48, LogLevel.Warning, "Failed to close a dedicated connection to {Host}:{Port}", EventName = "DedicatedConnectionCloseFailed")]
    private static partial void DedicatedConnectionCloseFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void DedicatedConnectionCloseFailed(this ILogger logger, string host, int port, Exception? error)
        => DedicatedConnectionCloseFailedMessage(logger, host, port, error);

    [LoggerMessage(49, LogLevel.Warning, "Dedicated connection recovery observer threw", EventName = "DedicatedRecoveryObserverFailed")]
    private static partial void DedicatedRecoveryObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void DedicatedRecoveryObserverFailed(this ILogger logger, Exception? error)
        => DedicatedRecoveryObserverFailedMessage(logger, error);

    [LoggerMessage(50, LogLevel.Information, "Redis maintenance {Kind} ({SequenceId}) on {Host}:{Port}", EventName = "MaintenanceNotificationReceived")]
    private static partial void MaintenanceNotificationReceivedMessage(ILogger logger, string kind, long sequenceId, string host, int port, Exception? exception);

    internal static void MaintenanceNotificationReceived(this ILogger logger, string kind, long sequenceId, string host, int port)
        => MaintenanceNotificationReceivedMessage(logger, kind, sequenceId, host, port, null);

    [LoggerMessage(51, LogLevel.Warning, "Maintenance diagnostic listener threw", EventName = "MaintenanceDiagnosticObserverFailed")]
    private static partial void MaintenanceDiagnosticObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void MaintenanceDiagnosticObserverFailed(this ILogger logger, Exception? error)
        => MaintenanceDiagnosticObserverFailedMessage(logger, error);

    [LoggerMessage(52, LogLevel.Debug, "Draining a removed read replica at {Endpoint} failed", EventName = "ReadReplicaDrainFailed")]
    private static partial void ReadReplicaDrainFailedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void ReadReplicaDrainFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => ReadReplicaDrainFailedMessage(logger, endpoint, error);

    [LoggerMessage(53, LogLevel.Debug, "Closing a removed read replica failed", EventName = "ReadReplicaCloseFailed")]
    private static partial void ReadReplicaCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void ReadReplicaCloseFailed(this ILogger logger, Exception? error)
        => ReadReplicaCloseFailedMessage(logger, error);

    [LoggerMessage(54, LogLevel.Debug, "Background Sentinel replica refresh failed", EventName = "SentinelReplicaBackgroundRefreshFailed")]
    private static partial void SentinelReplicaBackgroundRefreshFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelReplicaBackgroundRefreshFailed(this ILogger logger, Exception? error)
        => SentinelReplicaBackgroundRefreshFailedMessage(logger, error);

    [LoggerMessage(55, LogLevel.Warning, "Sentinel replica discovery failed; serving the last known {Count} replica endpoint(s) until Sentinel answers", EventName = "SentinelReplicaDiscoveryFailed")]
    private static partial void SentinelReplicaDiscoveryFailedMessage(ILogger logger, int count, Exception? exception);

    internal static void SentinelReplicaDiscoveryFailed(this ILogger logger, int count, Exception? error)
        => SentinelReplicaDiscoveryFailedMessage(logger, count, error);

    [LoggerMessage(56, LogLevel.Debug, "Sentinel replica refresh failed; retaining current endpoints", EventName = "SentinelReplicaRefreshFailed")]
    private static partial void SentinelReplicaRefreshFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelReplicaRefreshFailed(this ILogger logger, Exception? error)
        => SentinelReplicaRefreshFailedMessage(logger, error);

    [LoggerMessage(57, LogLevel.Information, "Sentinel replica discovery recovered with {Count} replica endpoint(s)", EventName = "SentinelReplicaDiscoveryRecovered")]
    private static partial void SentinelReplicaDiscoveryRecoveredMessage(ILogger logger, int count, Exception? exception);

    internal static void SentinelReplicaDiscoveryRecovered(this ILogger logger, int count)
        => SentinelReplicaDiscoveryRecoveredMessage(logger, count, null);

    [LoggerMessage(4001, LogLevel.Warning, "Credential renewal failed at {Stage} for {Host}:{Port}", EventName = "CredentialRefreshFailed")]
    private static partial void CredentialRefreshFailedMessage(ILogger logger, string stage, string host, int port, Exception? exception);

    internal static void CredentialRefreshFailed(this ILogger logger, string stage, string host, int port)
        => CredentialRefreshFailedMessage(logger, stage, host, port, null);

    [LoggerMessage(4101, LogLevel.Warning, "Background {Primitive} cleanup stopped at its {Stage} step ({Reason}); the owner may stay on Redis until it expires or is removed manually", EventName = "CoordinationCleanupAbandoned")]
    private static partial void CoordinationCleanupAbandonedMessage(ILogger logger, string primitive, string stage, string reason, Exception? exception);

    internal static void CoordinationCleanupAbandoned(this ILogger logger, string primitive, string stage, string reason)
        => CoordinationCleanupAbandonedMessage(logger, primitive, stage, reason, null);

    [LoggerMessage(58, LogLevel.Warning, "Reconnect telemetry listener threw for {Scope}", EventName = "ReconnectTelemetryObserverFailed")]
    private static partial void ReconnectTelemetryObserverFailedMessage(ILogger logger, string scope, Exception? exception);

    internal static void ReconnectTelemetryObserverFailed(this ILogger logger, string scope, Exception? error)
        => ReconnectTelemetryObserverFailedMessage(logger, scope, error);

    [LoggerMessage(59, LogLevel.Warning, "Restarting an unexpectedly completed Sentinel event monitor at {Endpoint}", EventName = "SentinelMonitorRestarting")]
    private static partial void SentinelMonitorRestartingMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMonitorRestarting(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorRestartingMessage(logger, endpoint, error);

    [LoggerMessage(60, LogLevel.Warning, "Sentinel event monitor failed at {Endpoint}", EventName = "SentinelMonitorFailed")]
    private static partial void SentinelMonitorFailedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMonitorFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorFailedMessage(logger, endpoint, error);

    [LoggerMessage(61, LogLevel.Debug, "Sentinel event monitor cleanup failed at {Endpoint}", EventName = "SentinelMonitorCleanupFailed")]
    private static partial void SentinelMonitorCleanupFailedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMonitorCleanupFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorCleanupFailedMessage(logger, endpoint, error);

    [LoggerMessage(62, LogLevel.Warning, "Sentinel event monitor exhausted reconnect attempts at {Endpoint}; failover events from this "
                    + "Sentinel are not observed until a new primary is published, and discovery runs on demand", EventName = "SentinelMonitorExhausted")]
    private static partial void SentinelMonitorExhaustedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMonitorExhausted(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMonitorExhaustedMessage(logger, endpoint, null);

    [LoggerMessage(63, LogLevel.Information, "Sentinel event monitor at {Endpoint} resumes after a new primary was published", EventName = "SentinelMonitorResumed")]
    private static partial void SentinelMonitorResumedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMonitorResumed(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMonitorResumedMessage(logger, endpoint, null);

    [LoggerMessage(64, LogLevel.Debug, "Could not resolve Sentinel switch source {Host}", EventName = "SentinelSwitchSourceResolutionFailed")]
    private static partial void SentinelSwitchSourceResolutionFailedMessage(ILogger logger, string host, Exception? exception);

    internal static void SentinelSwitchSourceResolutionFailed(this ILogger logger, string host, Exception? error)
        => SentinelSwitchSourceResolutionFailedMessage(logger, host, error);

    [LoggerMessage(65, LogLevel.Debug, "Sentinel {Endpoint} returned a malformed SENTINEL REPLICAS reply", EventName = "SentinelMalformedReplicas")]
    private static partial void SentinelMalformedReplicasMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelMalformedReplicas(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMalformedReplicasMessage(logger, endpoint, null);

    [LoggerMessage(66, LogLevel.Debug, "Optional Sentinel replica discovery failed at {Endpoint}", EventName = "SentinelReplicaDiscoveryUnavailable")]
    private static partial void SentinelReplicaDiscoveryUnavailableMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelReplicaDiscoveryUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelReplicaDiscoveryUnavailableMessage(logger, endpoint, error);

    [LoggerMessage(67, LogLevel.Warning, "Redis Sentinel discovery or primary connection failed through {Host}:{Port}", EventName = "SentinelPrimaryDiscoveryFailed")]
    private static partial void SentinelPrimaryDiscoveryFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void SentinelPrimaryDiscoveryFailed(this ILogger logger, string host, int port, Exception? error)
        => SentinelPrimaryDiscoveryFailedMessage(logger, host, port, error);

    [LoggerMessage(68, LogLevel.Debug, "Optional Sentinel {Stage} discovery failed at {Sentinel}", EventName = "SentinelOptionalDiscoveryFailed")]
    private static partial void SentinelOptionalDiscoveryFailedMessage(ILogger logger, string stage, RespireEndpoint sentinel, Exception? exception);

    internal static void SentinelOptionalDiscoveryFailed(this ILogger logger, string stage, RespireEndpoint sentinel, Exception? error)
        => SentinelOptionalDiscoveryFailedMessage(logger, stage, sentinel, error);

    [LoggerMessage(69, LogLevel.Debug, "Sentinel peer discovery was unavailable: {Error}", EventName = "SentinelPeerDiscoveryUnavailable")]
    private static partial void SentinelPeerDiscoveryUnavailableMessage(ILogger logger, string error, Exception? exception);

    internal static void SentinelPeerDiscoveryUnavailable(this ILogger logger, in RespValue reply)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            SentinelPeerDiscoveryUnavailableMessage(logger, reply.GetErrorMessage(), null);
    }

    [LoggerMessage(70, LogLevel.Debug, "Sentinel primary probe failed on a retired generation; rediscovering", EventName = "SentinelRetiredPrimaryProbeFailed")]
    private static partial void SentinelRetiredPrimaryProbeFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelRetiredPrimaryProbeFailed(this ILogger logger, Exception? error)
        => SentinelRetiredPrimaryProbeFailedMessage(logger, error);

    [LoggerMessage(71, LogLevel.Warning, "Sentinel state observer failed", EventName = "SentinelStateObserverFailed")]
    private static partial void SentinelStateObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelStateObserverFailed(this ILogger logger, Exception? error)
        => SentinelStateObserverFailedMessage(logger, error);

    [LoggerMessage(72, LogLevel.Debug, "Sentinel transport retirement reported an error after draining at {Endpoint}", EventName = "SentinelRetirementDrainFailed")]
    private static partial void SentinelRetirementDrainFailedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelRetirementDrainFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelRetirementDrainFailedMessage(logger, endpoint, error);

    [LoggerMessage(73, LogLevel.Warning, "Sentinel generation at {Endpoint} retains an unacknowledged correction fence", EventName = "SentinelCorrectionFenceUnacknowledged")]
    private static partial void SentinelCorrectionFenceUnacknowledgedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelCorrectionFenceUnacknowledged(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelCorrectionFenceUnacknowledgedMessage(logger, endpoint, error);

    [LoggerMessage(74, LogLevel.Warning, "Sentinel generation cleanup failed at {Endpoint}; retained until client disposal", EventName = "SentinelGenerationCleanupFailed")]
    private static partial void SentinelGenerationCleanupFailedMessage(ILogger logger, RespireEndpoint endpoint, Exception? exception);

    internal static void SentinelGenerationCleanupFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelGenerationCleanupFailedMessage(logger, endpoint, error);

    [LoggerMessage(75, LogLevel.Warning, "Sentinel event monitoring did not stop within {Timeout}; {Count} task(s) still running", EventName = "SentinelMonitorShutdownTimedOut")]
    private static partial void SentinelMonitorShutdownTimedOutMessage(ILogger logger, TimeSpan timeout, int count, Exception? exception);

    internal static void SentinelMonitorShutdownTimedOut(this ILogger logger, TimeSpan timeout, Task[] tasks)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            SentinelMonitorShutdownTimedOutMessage(logger, timeout, tasks.Count(static task => !task.IsCompleted), null);
    }

    [LoggerMessage(76, LogLevel.Warning, "Sentinel upload pool cleanup after MOVING failed", EventName = "SentinelMovingUploadCleanupFailed")]
    private static partial void SentinelMovingUploadCleanupFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelMovingUploadCleanupFailed(this ILogger logger, Exception? error)
        => SentinelMovingUploadCleanupFailedMessage(logger, error);

    [LoggerMessage(77, LogLevel.Debug, "Could not retire the primary named by a Sentinel switch event", EventName = "SentinelSwitchPrimaryRetirementFailed")]
    private static partial void SentinelSwitchPrimaryRetirementFailedMessage(ILogger logger, Exception? exception);

    internal static void SentinelSwitchPrimaryRetirementFailed(this ILogger logger, Exception? error)
        => SentinelSwitchPrimaryRetirementFailedMessage(logger, error);

    [LoggerMessage(78, LogLevel.Information, "Sentinel notification-triggered primary discovery succeeded after {Failures} failed attempt(s)", EventName = "SentinelNotificationDiscoveryRecovered")]
    private static partial void SentinelNotificationDiscoveryRecoveredMessage(ILogger logger, int failures, Exception? exception);

    internal static void SentinelNotificationDiscoveryRecovered(this ILogger logger, int failures)
        => SentinelNotificationDiscoveryRecoveredMessage(logger, failures, null);

    [LoggerMessage(79, LogLevel.Warning, "Thread-pool scheduling delayed by {SchedulingDelayMs} ms (probe pending: {ProbePending}); workers busy/min: {BusyWorkers}/{MinWorkers}; queued work: {PendingWorkItems}. " +
                    "Inspect synchronous blocking and long-running work; use asynchronous I/O and diagnose runtime counters before changing minimum worker threads.", EventName = "ThreadPoolSchedulingDelayed")]
    private static partial void ThreadPoolSchedulingDelayedMessage(ILogger logger, double schedulingDelayMs, bool probePending, int busyWorkers, int minWorkers, long pendingWorkItems, Exception? exception);

    internal static void ThreadPoolSchedulingDelayed(this ILogger logger, double schedulingDelayMs, bool probePending, int busyWorkers, int minWorkers, long pendingWorkItems)
        => ThreadPoolSchedulingDelayedMessage(logger, schedulingDelayMs, probePending, busyWorkers, minWorkers, pendingWorkItems, null);

    [LoggerMessage(80, LogLevel.Debug, "Connected to {Host}:{Port}", EventName = "ConnectionConnected")]
    private static partial void ConnectionConnectedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionConnected(this ILogger logger, string host, int port)
        => ConnectionConnectedMessage(logger, host, port, null);

    [LoggerMessage(81, LogLevel.Information, "HELLO 3 is unsupported by {Host}:{Port}; using RESP2 on this connection", EventName = "HelloResp2Fallback")]
    private static partial void HelloResp2FallbackMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void HelloResp2Fallback(this ILogger logger, string host, int port)
        => HelloResp2FallbackMessage(logger, host, port, null);

    [LoggerMessage(82, LogLevel.Debug, "Negotiated RESP3 with {Host}:{Port}", EventName = "HelloResp3Negotiated")]
    private static partial void HelloResp3NegotiatedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void HelloResp3Negotiated(this ILogger logger, string host, int port)
        => HelloResp3NegotiatedMessage(logger, host, port, null);

    [LoggerMessage(83, LogLevel.Debug, "Send failed for {Host}:{Port}; aborting connection", EventName = "ConnectionSendFailed")]
    private static partial void ConnectionSendFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionSendFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionSendFailedMessage(logger, host, port, error);

    [LoggerMessage(84, LogLevel.Warning, "Connection close observer threw for {Host}:{Port}", EventName = "ConnectionCloseObserverFailed")]
    private static partial void ConnectionCloseObserverFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionCloseObserverFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionCloseObserverFailedMessage(logger, host, port, error);

    [LoggerMessage(85, LogLevel.Warning, "Connection failure observer threw for {Host}:{Port}", EventName = "ConnectionFailureObserverFailed")]
    private static partial void ConnectionFailureObserverFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionFailureObserverFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionFailureObserverFailedMessage(logger, host, port, error);

    [LoggerMessage(86, LogLevel.Warning, "Push handler threw for {Host}:{Port}; message dropped", EventName = "ConnectionPushHandlerFailed")]
    private static partial void ConnectionPushHandlerFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionPushHandlerFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionPushHandlerFailedMessage(logger, host, port, error);

    [LoggerMessage(87, LogLevel.Warning, "Replies for {Host}:{Port} were still queued for delivery {Milliseconds} ms after the connection closed; "
                        + "finishing disposal while their delivery runner waits for a thread-pool thread.", EventName = "ConnectionQueuedDeliveryDelayed")]
    private static partial void ConnectionQueuedDeliveryDelayedMessage(ILogger logger, string host, int port, long milliseconds, Exception? exception);

    internal static void ConnectionQueuedDeliveryDelayed(this ILogger logger, string host, int port, long milliseconds)
        => ConnectionQueuedDeliveryDelayedMessage(logger, host, port, milliseconds, null);

    [LoggerMessage(88, LogLevel.Warning, "Reply delivery for {Host}:{Port} was blocked by a continuation for over {Milliseconds} ms; "
                + "delivering the remaining replies on another thread. Blocking waits or CPU-bound work inside continuations "
                + "can delay other callers. Consider RespireOptions.RunContinuationsAsynchronously.", EventName = "ConnectionContinuationDeliveryBlocked")]
    private static partial void ConnectionContinuationDeliveryBlockedMessage(ILogger logger, string host, int port, long milliseconds, Exception? exception);

    internal static void ConnectionContinuationDeliveryBlocked(this ILogger logger, string host, int port, long milliseconds)
        => ConnectionContinuationDeliveryBlockedMessage(logger, host, port, milliseconds, null);

    [LoggerMessage(89, LogLevel.Debug, "Failed {Count} in-flight commands on {Host}:{Port}: {Reason}", EventName = "ConnectionInflightFailed")]
    private static partial void ConnectionInflightFailedMessage(ILogger logger, int count, string host, int port, string reason, Exception? exception);

    internal static void ConnectionInflightFailed(this ILogger logger, int count, string host, int port, Exception failure)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            ConnectionInflightFailedMessage(logger, count, host, port, failure.Message, null);
    }

    [LoggerMessage(90, LogLevel.Debug, "Disconnected from {Host}:{Port}", EventName = "ConnectionDisconnected")]
    private static partial void ConnectionDisconnectedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ConnectionDisconnected(this ILogger logger, string host, int port)
        => ConnectionDisconnectedMessage(logger, host, port, null);

    [LoggerMessage(91, LogLevel.Warning, "Sharded subscription connection closed with a receive failure", EventName = "ShardedSubscriptionReceiveFailed")]
    private static partial void ShardedSubscriptionReceiveFailedMessage(ILogger logger, Exception? exception);

    internal static void ShardedSubscriptionReceiveFailed(this ILogger logger, Exception? error)
        => ShardedSubscriptionReceiveFailedMessage(logger, error);

    [LoggerMessage(92, LogLevel.Debug, "Closing sharded subscription connection {Host}:{Port} after SUNSUBSCRIBE failed", EventName = "ShardedSubscriptionCloseFailed")]
    private static partial void ShardedSubscriptionCloseFailedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void ShardedSubscriptionCloseFailed(this ILogger logger, string host, int port, Exception? error)
        => ShardedSubscriptionCloseFailedMessage(logger, host, port, error);

    [LoggerMessage(93, LogLevel.Warning, "Sharded pub/sub recovery attempt {Attempt} failed", EventName = "ShardedSubscriptionRecoveryFailed")]
    private static partial void ShardedSubscriptionRecoveryFailedMessage(ILogger logger, int attempt, Exception? exception);

    internal static void ShardedSubscriptionRecoveryFailed(this ILogger logger, int attempt, Exception? error)
        => ShardedSubscriptionRecoveryFailedMessage(logger, attempt, error);

    [LoggerMessage(94, LogLevel.Debug, "Closing a rolled back cluster notification connection failed", EventName = "NotificationRollbackCloseFailed")]
    private static partial void NotificationRollbackCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationRollbackCloseFailed(this ILogger logger, Exception? error)
        => NotificationRollbackCloseFailedMessage(logger, error);

    [LoggerMessage(95, LogLevel.Warning, "Cluster notification route {Route} was rejected by {Host}:{Port}", EventName = "NotificationRouteRejected")]
    private static partial void NotificationRouteRejectedMessage(ILogger logger, string route, string host, int port, Exception? exception);

    internal static void NotificationRouteRejected(this ILogger logger, RespireChannel route, string host, int port, Exception? error)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            NotificationRouteRejectedMessage(logger, route.ToString(), host, port, error);
    }

    [LoggerMessage(96, LogLevel.Debug, "Closing a failed cluster notification replacement failed", EventName = "NotificationReplacementCloseFailed")]
    private static partial void NotificationReplacementCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationReplacementCloseFailed(this ILogger logger, Exception? error)
        => NotificationReplacementCloseFailedMessage(logger, error);

    [LoggerMessage(97, LogLevel.Debug, "Closing a replacement for a retired cluster notification node failed", EventName = "NotificationRetiredReplacementCloseFailed")]
    private static partial void NotificationRetiredReplacementCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationRetiredReplacementCloseFailed(this ILogger logger, Exception? error)
        => NotificationRetiredReplacementCloseFailedMessage(logger, error);

    [LoggerMessage(98, LogLevel.Debug, "Closing a superseded cluster notification connection failed", EventName = "NotificationSupersededCloseFailed")]
    private static partial void NotificationSupersededCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationSupersededCloseFailed(this ILogger logger, Exception? error)
        => NotificationSupersededCloseFailedMessage(logger, error);

    [LoggerMessage(99, LogLevel.Warning, "Cluster notification recovery for {Host}:{Port} stopped unexpectedly", EventName = "NotificationRecoveryStopped")]
    private static partial void NotificationRecoveryStoppedMessage(ILogger logger, string host, int port, Exception? exception);

    internal static void NotificationRecoveryStopped(this ILogger logger, string host, int port, Exception? error)
        => NotificationRecoveryStoppedMessage(logger, host, port, error);

    [LoggerMessage(100, LogLevel.Warning, "Cluster notification reconnect telemetry listener threw", EventName = "NotificationReconnectTelemetryFailed")]
    private static partial void NotificationReconnectTelemetryFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationReconnectTelemetryFailed(this ILogger logger, Exception? error)
        => NotificationReconnectTelemetryFailedMessage(logger, error);

    [LoggerMessage(101, LogLevel.Warning, "Cluster notification reconnect failed for {Host}:{Port}; retrying in {Delay}", EventName = "NotificationReconnectRetry")]
    private static partial void NotificationReconnectRetryMessage(ILogger logger, string host, int port, TimeSpan delay, Exception? exception);

    internal static void NotificationReconnectRetry(this ILogger logger, string host, int port, TimeSpan delay, Exception? error)
        => NotificationReconnectRetryMessage(logger, host, port, delay, error);

    [LoggerMessage(102, LogLevel.Warning, "Cluster notification reconnect exhaustion telemetry listener threw", EventName = "NotificationReconnectExhaustionTelemetryFailed")]
    private static partial void NotificationReconnectExhaustionTelemetryFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationReconnectExhaustionTelemetryFailed(this ILogger logger, Exception? error)
        => NotificationReconnectExhaustionTelemetryFailedMessage(logger, error);

    [LoggerMessage(103, LogLevel.Debug, "Cluster notification unsubscribe failed during exhaustion cleanup", EventName = "NotificationExhaustionUnsubscribeFailed")]
    private static partial void NotificationExhaustionUnsubscribeFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationExhaustionUnsubscribeFailed(this ILogger logger, Exception? error)
        => NotificationExhaustionUnsubscribeFailedMessage(logger, error);

    [LoggerMessage(104, LogLevel.Debug, "Closing an exhausted cluster notification connection failed", EventName = "NotificationExhaustionCloseFailed")]
    private static partial void NotificationExhaustionCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationExhaustionCloseFailed(this ILogger logger, Exception? error)
        => NotificationExhaustionCloseFailedMessage(logger, error);

    [LoggerMessage(105, LogLevel.Debug, "Cluster notification unsubscribe failed", EventName = "NotificationUnsubscribeFailed")]
    private static partial void NotificationUnsubscribeFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationUnsubscribeFailed(this ILogger logger, Exception? error)
        => NotificationUnsubscribeFailedMessage(logger, error);

    [LoggerMessage(106, LogLevel.Debug, "Closing an uncertain cluster notification connection failed", EventName = "NotificationUncertainCloseFailed")]
    private static partial void NotificationUncertainCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationUncertainCloseFailed(this ILogger logger, Exception? error)
        => NotificationUncertainCloseFailedMessage(logger, error);

    [LoggerMessage(107, LogLevel.Debug, "Closing a retired cluster notification connection failed", EventName = "NotificationRetiredCloseFailed")]
    private static partial void NotificationRetiredCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationRetiredCloseFailed(this ILogger logger, Exception? error)
        => NotificationRetiredCloseFailedMessage(logger, error);

    [LoggerMessage(108, LogLevel.Warning, "Cluster notification topology reconciliation failed", EventName = "NotificationTopologyReconciliationFailed")]
    private static partial void NotificationTopologyReconciliationFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationTopologyReconciliationFailed(this ILogger logger, Exception? error)
        => NotificationTopologyReconciliationFailedMessage(logger, error);

    [LoggerMessage(109, LogLevel.Debug, "Closing a cluster notification connection after a failed subscribe failed", EventName = "NotificationSubscribeFailureCloseFailed")]
    private static partial void NotificationSubscribeFailureCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationSubscribeFailureCloseFailed(this ILogger logger, Exception? error)
        => NotificationSubscribeFailureCloseFailedMessage(logger, error);

    [LoggerMessage(110, LogLevel.Debug, "Releasing an exhausted cluster notification subscription failed", EventName = "NotificationExhaustedSubscriptionReleaseFailed")]
    private static partial void NotificationExhaustedSubscriptionReleaseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationExhaustedSubscriptionReleaseFailed(this ILogger logger, Exception? error)
        => NotificationExhaustedSubscriptionReleaseFailedMessage(logger, error);

    [LoggerMessage(111, LogLevel.Warning, "Subscription delivery-gap observer threw", EventName = "SubscriptionGapObserverFailed")]
    private static partial void SubscriptionGapObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void SubscriptionGapObserverFailed(this ILogger logger, Exception? error)
        => SubscriptionGapObserverFailedMessage(logger, error);

    [LoggerMessage(112, LogLevel.Debug, "Closing a failed subscription connection failed", EventName = "SubscriptionCloseFailed")]
    private static partial void SubscriptionCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void SubscriptionCloseFailed(this ILogger logger, Exception? error)
        => SubscriptionCloseFailedMessage(logger, error);

    [LoggerMessage(113, LogLevel.Warning, "Pub/sub reconnect failed; retrying in {Delay}", EventName = "SubscriptionReconnectRetry")]
    private static partial void SubscriptionReconnectRetryMessage(ILogger logger, TimeSpan delay, Exception? exception);

    internal static void SubscriptionReconnectRetry(this ILogger logger, TimeSpan delay, Exception? error)
        => SubscriptionReconnectRetryMessage(logger, delay, error);

    [LoggerMessage(114, LogLevel.Warning, "Pub/sub recovery metric observer threw", EventName = "SubscriptionRecoveryMetricFailed")]
    private static partial void SubscriptionRecoveryMetricFailedMessage(ILogger logger, Exception? exception);

    internal static void SubscriptionRecoveryMetricFailed(this ILogger logger, Exception? error)
        => SubscriptionRecoveryMetricFailedMessage(logger, error);

    [LoggerMessage(115, LogLevel.Debug, "Closing a cluster notification connection failed", EventName = "NotificationConnectionCloseFailed")]
    private static partial void NotificationConnectionCloseFailedMessage(ILogger logger, Exception? exception);

    internal static void NotificationConnectionCloseFailed(this ILogger logger, Exception? error)
        => NotificationConnectionCloseFailedMessage(logger, error);

    [LoggerMessage(116, LogLevel.Warning, "Pub/sub recovery episode observer threw", EventName = "SubscriptionRecoveryEpisodeObserverFailed")]
    private static partial void SubscriptionRecoveryEpisodeObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void SubscriptionRecoveryEpisodeObserverFailed(this ILogger logger, Exception? error)
        => SubscriptionRecoveryEpisodeObserverFailedMessage(logger, error);

    [LoggerMessage(117, LogLevel.Warning, "Pub/sub recovery attempt {Attempt} failed", EventName = "SubscriptionRecoveryAttemptFailed")]
    private static partial void SubscriptionRecoveryAttemptFailedMessage(ILogger logger, int attempt, Exception? exception);

    internal static void SubscriptionRecoveryAttemptFailed(this ILogger logger, int attempt, Exception? error)
        => SubscriptionRecoveryAttemptFailedMessage(logger, attempt, error);

    [LoggerMessage(118, LogLevel.Warning, "Failed to clean up a pub/sub replacement", EventName = "SubscriptionReplacementCleanupFailed")]
    private static partial void SubscriptionReplacementCleanupFailedMessage(ILogger logger, Exception? exception);

    internal static void SubscriptionReplacementCleanupFailed(this ILogger logger, Exception? error)
        => SubscriptionReplacementCleanupFailedMessage(logger, error);

    [LoggerMessage(119, LogLevel.Warning, "Lock releases run without connection fencing because CLIENT ID or CLIENT KILL was denied. " +
                "An uncertain release still treats ownership as lost, but its delete may run later. " +
                "Grant the client and client|id/client|kill permissions to restore fencing.", EventName = "NativeLockFencingUnavailable")]
    private static partial void NativeLockFencingUnavailableMessage(ILogger logger, Exception? exception);

    internal static void NativeLockFencingUnavailable(this ILogger logger, Exception? error)
        => NativeLockFencingUnavailableMessage(logger, error);

    [LoggerMessage(120, LogLevel.Warning, "Could not fence Redis client {ServerClientId} at {Endpoint} after an uncertain {Operation}; " +
                "the command may still execute. Ownership was already treated as lost.", EventName = "NativeLockFenceFailed")]
    private static partial void NativeLockFenceFailedMessage(ILogger logger, long serverClientId, RespireEndpoint endpoint, string operation, Exception? exception);

    internal static void NativeLockFenceFailed(this ILogger logger, long serverClientId, RespireEndpoint endpoint, string operation, Exception? error)
        => NativeLockFenceFailedMessage(logger, serverClientId, endpoint, operation, error);

    [LoggerMessage(121, LogLevel.Warning, "Failover candidate {Endpoint} was marked unhealthy because it duplicates another candidate's deployment: {Reason}", EventName = "FailoverDuplicateDeployment")]
    private static partial void FailoverDuplicateDeploymentMessage(ILogger logger, string endpoint, string reason, Exception? exception);

    internal static void FailoverDuplicateDeployment(this ILogger logger, RespireEndpoint endpoint, string reason)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            FailoverDuplicateDeploymentMessage(logger, endpoint.ToString(), reason, null);
    }

    [LoggerMessage(122, LogLevel.Information, "Failover group switched from {PreviousEndpoint} to {CurrentEndpoint} ({Reason})", EventName = "FailoverEndpointSwitched")]
    private static partial void FailoverEndpointSwitchedMessage(ILogger logger, string previousEndpoint, string currentEndpoint, string reason, Exception? exception);

    internal static void FailoverEndpointSwitched(this ILogger logger, RespireEndpoint? previousEndpoint, RespireEndpoint? currentEndpoint, string reason)
    {
        if (logger.IsEnabled(LogLevel.Information))
            FailoverEndpointSwitchedMessage(logger, previousEndpoint?.ToString() ?? "none", currentEndpoint?.ToString() ?? "none", reason, null);
    }

    [LoggerMessage(123, LogLevel.Warning, "Failover group EndpointSwitched handler threw", EventName = "FailoverSwitchObserverFailed")]
    private static partial void FailoverSwitchObserverFailedMessage(ILogger logger, Exception? exception);

    internal static void FailoverSwitchObserverFailed(this ILogger logger, Exception? error)
        => FailoverSwitchObserverFailedMessage(logger, error);

    [LoggerMessage(124, LogLevel.Warning, "Failover group health monitor round failed", EventName = "FailoverMonitorFailed")]
    private static partial void FailoverMonitorFailedMessage(ILogger logger, Exception? exception);

    internal static void FailoverMonitorFailed(this ILogger logger, Exception? error)
        => FailoverMonitorFailedMessage(logger, error);

}
