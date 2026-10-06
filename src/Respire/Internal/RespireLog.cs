using Microsoft.Extensions.Logging;
using Respire.Protocol;

namespace Respire;

// Keep legacy EventId values and names, including unnamed event zero.
// Cached typed delegates check IsEnabled before boxing or creating log state.
internal static partial class RespireLog
{
    private static readonly Action<ILogger, int, Exception?> StreamReadRetryMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default, "Stream read failed; retrying after {DelayMilliseconds} ms.");

    internal static void StreamReadRetry(this ILogger logger, int delayMilliseconds, Exception? error)
        => StreamReadRetryMessage(logger, delayMilliseconds, error);

    private static readonly Action<ILogger, int, string, int, Exception?> DeadConnectionReplacedMessage =
        LoggerMessage.Define<int, string, int>(LogLevel.Information, default, "Replaced dead connection {Slot} to {Host}:{Port}");

    internal static void DeadConnectionReplaced(this ILogger logger, int slot, string host, int port)
        => DeadConnectionReplacedMessage(logger, slot, host, port, null);

    private static readonly Action<ILogger, string, int, Exception?> RejectedReplacementDisposalFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Failed to dispose rejected replacement connection to {Host}:{Port}");

    internal static void RejectedReplacementDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => RejectedReplacementDisposalFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, int, Exception?> ReconnectKeptHealthyConnectionMessage =
        LoggerMessage.Define<int>(LogLevel.Debug, default, "Reconnect of slot {Slot} failed after a healthy connection was published; keeping it");

    internal static void ReconnectKeptHealthyConnection(this ILogger logger, int slot, Exception? error)
        => ReconnectKeptHealthyConnectionMessage(logger, slot, error);

    private static readonly Action<ILogger, string, int, int, Exception?> ReconnectExhaustedMessage =
        LoggerMessage.Define<string, int, int>(LogLevel.Warning, default, "Reconnect to {Host}:{Port} exhausted its {Attempts} attempts");

    internal static void ReconnectExhausted(this ILogger logger, string host, int port, int attempts, Exception? error)
        => ReconnectExhaustedMessage(logger, host, port, attempts, error);

    private static readonly Action<ILogger, string, int, Exception?> ReconnectBackoffDeferredMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Reconnect to {Host}:{Port} failed; next use will schedule another attempt with configured backoff");

    internal static void ReconnectBackoffDeferred(this ILogger logger, string host, int port, Exception? error)
        => ReconnectBackoffDeferredMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> ReconnectDeferredMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Reconnect to {Host}:{Port} failed; will retry on next use");

    internal static void ReconnectDeferred(this ILogger logger, string host, int port, Exception? error)
        => ReconnectDeferredMessage(logger, host, port, error);

    private static readonly Action<ILogger, Exception?> ConnectionStateObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Connection state-change handler threw");

    internal static void ConnectionStateObserverFailed(this ILogger logger, Exception? error)
        => ConnectionStateObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ConnectionSlotObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Connection slot state-change handler threw");

    internal static void ConnectionSlotObserverFailed(this ILogger logger, Exception? error)
        => ConnectionSlotObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ReconnectMetricObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Reconnect metrics listener threw");

    internal static void ReconnectMetricObserverFailed(this ILogger logger, Exception? error)
        => ReconnectMetricObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> MaintenanceBarrierAbortFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Connection abort after maintenance barrier failure also failed at {Host}:{Port}");

    internal static void MaintenanceBarrierAbortFailed(this ILogger logger, string host, int port, Exception? error)
        => MaintenanceBarrierAbortFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> MaintenanceBarrierFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Maintenance drain barrier failed at {Host}:{Port}; retiring connection");

    internal static void MaintenanceBarrierFailed(this ILogger logger, string host, int port, Exception? error)
        => MaintenanceBarrierFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> RetirementDisposalFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Retirement did not finish cleanly before disposal of {Host}:{Port}");

    internal static void RetirementDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => RetirementDisposalFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, string, int, Exception?> MovingSupersededMessage =
        LoggerMessage.Define<string, int, string, int>(LogLevel.Debug, default, "MOVING to {Host}:{Port} supersedes the handoff to {PreviousHost}:{PreviousPort}");

    internal static void MovingSuperseded(this ILogger logger, string host, int port, string previousHost, int previousPort)
        => MovingSupersededMessage(logger, host, port, previousHost, previousPort, null);

    private static readonly Action<ILogger, string, int, Exception?> MovingFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "MOVING handoff to {Host}:{Port} failed");

    internal static void MovingFailed(this ILogger logger, string host, int port, Exception? error)
        => MovingFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, Exception?> MovingRetirementStoppedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "MOVING handoff stopped by multiplexer retirement");

    internal static void MovingRetirementStopped(this ILogger logger, Exception? error)
        => MovingRetirementStoppedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> MovingGraceAttemptFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "MOVING handoff to {Host}:{Port} failed within its grace period; keeping current connections");

    internal static void MovingGraceAttemptFailed(this ILogger logger, string host, int port, Exception? error)
        => MovingGraceAttemptFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> MovingRetryMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "MOVING handoff to {Host}:{Port} failed; retrying");

    internal static void MovingRetry(this ILogger logger, string host, int port, Exception? error)
        => MovingRetryMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> MovingPublicationSupersededMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "MOVING handoff to {Host}:{Port} superseded before publication");

    internal static void MovingPublicationSuperseded(this ILogger logger, string host, int port)
        => MovingPublicationSupersededMessage(logger, host, port, null);

    private static readonly Action<ILogger, string, int, long, Exception?> MovingPublishedLateMessage =
        LoggerMessage.Define<string, int, long>(LogLevel.Warning, default, "MOVING handoff to {Host}:{Port} published {LateMilliseconds} ms after its grace period; aborting old sockets, so commands they had accepted may fail");

    internal static void MovingPublishedLate(this ILogger logger, string host, int port, long lateMilliseconds)
        => MovingPublishedLateMessage(logger, host, port, lateMilliseconds, null);

    private static readonly Action<ILogger, Exception?> MovingCacheFenceObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "MOVING retirement cache fence observer failed");

    internal static void MovingCacheFenceObserverFailed(this ILogger logger, Exception? error)
        => MovingCacheFenceObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ContinuityFlushObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Continuity flush metrics observer failed");

    internal static void ContinuityFlushObserverFailed(this ILogger logger, Exception? error)
        => ContinuityFlushObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> MovingSocketFenceFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Fencing old MOVING sockets failed");

    internal static void MovingSocketFenceFailed(this ILogger logger, Exception? error)
        => MovingSocketFenceFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> MovingSocketDrainStoppedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Old MOVING socket drain stopped by multiplexer retirement");

    internal static void MovingSocketDrainStopped(this ILogger logger, Exception? error)
        => MovingSocketDrainStoppedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> MovingDrainGraceExceededMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "MOVING handoff drain exceeded its advertised grace period; aborting remaining old sockets");

    internal static void MovingDrainGraceExceeded(this ILogger logger)
        => MovingDrainGraceExceededMessage(logger, null);

    private static readonly Action<ILogger, Exception?> MovingDrainDisposalStoppedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Disposal ended the MOVING handoff drain; aborting remaining old sockets");

    internal static void MovingDrainDisposalStopped(this ILogger logger)
        => MovingDrainDisposalStoppedMessage(logger, null);

    private static readonly Action<ILogger, Exception?> MovingDrainFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "MOVING handoff drain of old sockets failed; aborting remaining old sockets");

    internal static void MovingDrainFailed(this ILogger logger, Exception? error)
        => MovingDrainFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> MovingSocketAbortFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Aborting an old MOVING socket failed");

    internal static void MovingSocketAbortFailed(this ILogger logger, Exception? error)
        => MovingSocketAbortFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> MovingSocketDrainCleanupCompletedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Old MOVING sockets completed after drain cleanup");

    internal static void MovingSocketDrainCleanupCompleted(this ILogger logger, Exception? error)
        => MovingSocketDrainCleanupCompletedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> DedicatedMovingCleanupFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Dedicated connection cleanup after MOVING failed");

    internal static void DedicatedMovingCleanupFailed(this ILogger logger, Exception? error)
        => DedicatedMovingCleanupFailedMessage(logger, error);

    private static readonly Action<ILogger, long, long, Exception?> ClusterUnknownEndpointSkippedMessage =
        LoggerMessage.Define<long, long>(LogLevel.Debug, default, "Skipping Redis Cluster slots {Start}-{End}: the preferred endpoint is unknown ('?').");

    internal static void ClusterUnknownEndpointSkipped(this ILogger logger, long start, long end)
        => ClusterUnknownEndpointSkippedMessage(logger, start, end, null);

    private static readonly Action<ILogger, Exception?> ClusterDiscoveryObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Cluster discovery observer threw");

    internal static void ClusterDiscoveryObserverFailed(this ILogger logger, Exception? error)
        => ClusterDiscoveryObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> ClusterMigrationDropMetricFailedMessage =
        LoggerMessage.Define(LogLevel.Error, default, "Error recording clustered migration-drop metric.");

    internal static void ClusterMigrationDropMetricFailed(this ILogger logger, Exception? error)
        => ClusterMigrationDropMetricFailedMessage(logger, error);

    private static readonly Action<ILogger, long, Exception?> ClusterMigrationQueueFullMessage =
        LoggerMessage.Define<long>(LogLevel.Warning, default, "Cluster SMIGRATED queue is full; {Dropped} notifications dropped so far. MOVED handling and topology discovery will correct the affected slots.");

    internal static void ClusterMigrationQueueFull(this ILogger logger, long dropped)
        => ClusterMigrationQueueFullMessage(logger, dropped, null);

    private static readonly Action<ILogger, Exception?> ClusterMigrationMetricObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Cluster slot migration metric listener threw.");

    internal static void ClusterMigrationMetricObserverFailed(this ILogger logger, Exception? error)
        => ClusterMigrationMetricObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> ClusterMigrationApplyFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Error, default, "Failed to apply Cluster SMIGRATED notification from {Host}:{Port}.");

    internal static void ClusterMigrationApplyFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterMigrationApplyFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, long, string, int, Exception?> ClusterMigrationSequenceIgnoredMessage =
        LoggerMessage.Define<long, string, int>(LogLevel.Debug, default, "Ignored duplicate Cluster SMIGRATED sequence {Sequence} from {Host}:{Port}.");

    internal static void ClusterMigrationSequenceIgnored(this ILogger logger, long sequence, string host, int port)
        => ClusterMigrationSequenceIgnoredMessage(logger, sequence, host, port, null);

    private static readonly Action<ILogger, int, Exception?> ClusterReplicaRouteRefreshFailedMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default, "Replica route refresh for Redis Cluster slot {Slot} failed");

    internal static void ClusterReplicaRouteRefreshFailed(this ILogger logger, int slot, Exception? error)
        => ClusterReplicaRouteRefreshFailedMessage(logger, slot, error);

    private static readonly Action<ILogger, int, string, RespireReadFrom, Exception?> ClusterReadRoleRetryMessage =
        LoggerMessage.Define<int, string, RespireReadFrom>(LogLevel.Debug, default, "Redis Cluster slot {Slot} returned {Code} on the preferred role; {ReadFrom} read retries on the other role");

    internal static void ClusterReadRoleRetry(this ILogger logger, int slot, string code, RespireReadFrom readFrom)
        => ClusterReadRoleRetryMessage(logger, slot, code, readFrom, null);

    private static readonly Action<ILogger, int, string, int, RespireReadFrom, string, Exception?> ClusterReplicaRedirectRefreshFailedMessage =
        LoggerMessage.Define<int, string, int, RespireReadFrom, string>(LogLevel.Warning, default, "Unable to refresh replica routes for Redis Cluster slot {Slot} from {Host}:{Port} after a redirect; {ReadFrom} read uses {Fallback}");

    internal static void ClusterReplicaRedirectRefreshFailed(this ILogger logger, int slot, string host, int port, RespireReadFrom readFrom, string fallback)
        => ClusterReplicaRedirectRefreshFailedMessage(logger, slot, host, port, readFrom, fallback, null);

    private static readonly Action<ILogger, string, int, Exception?> ClusterRetirementCorrectionNeededMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Cluster generation retirement needs correction cleanup at {Host}:{Port}");

    internal static void ClusterRetirementCorrectionNeeded(this ILogger logger, string host, int port, Exception? error)
        => ClusterRetirementCorrectionNeededMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, int, int, Exception?> ClusterFenceUnavailableMessage =
        LoggerMessage.Define<string, int, int, int>(LogLevel.Warning, default, "Cluster fence still unavailable at {Host}:{Port}; retrying in {DelaySeconds}s; {RetiringGenerationCount} generations remain in retirement");

    internal static void ClusterFenceUnavailable(this ILogger logger, string host, int port, int delaySeconds, int retiringGenerationCount, Exception? error)
        => ClusterFenceUnavailableMessage(logger, host, port, delaySeconds, retiringGenerationCount, error);

    private static readonly Action<ILogger, string, int, int, Exception?> ClusterFenceRetryMessage =
        LoggerMessage.Define<string, int, int>(LogLevel.Debug, default, "Cluster fence retry failed at {Host}:{Port}; retrying in {DelaySeconds}s");

    internal static void ClusterFenceRetry(this ILogger logger, string host, int port, int delaySeconds, Exception? error)
        => ClusterFenceRetryMessage(logger, host, port, delaySeconds, error);

    private static readonly Action<ILogger, string, int, Exception?> ClusterRetirementFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Cluster generation retirement failed at {Host}:{Port}");

    internal static void ClusterRetirementFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterRetirementFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> ClusterGenerationCleanupFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Cluster generation cleanup failed at {Host}:{Port}");

    internal static void ClusterGenerationCleanupFailed(this ILogger logger, string host, int port, Exception? error)
        => ClusterGenerationCleanupFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, Exception?> ClusterRetiredPoolCleanupFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "A retired Cluster pool reported a cleanup failure");

    internal static void ClusterRetiredPoolCleanupFailed(this ILogger logger, Exception? error)
        => ClusterRetiredPoolCleanupFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> DedicatedPoolDisposalFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Failed to dispose a dedicated pool for {Host}:{Port}");

    internal static void DedicatedPoolDisposalFailed(this ILogger logger, string host, int port, Exception? error)
        => DedicatedPoolDisposalFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> DedicatedConnectionCloseFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Failed to close a dedicated connection to {Host}:{Port}");

    internal static void DedicatedConnectionCloseFailed(this ILogger logger, string host, int port, Exception? error)
        => DedicatedConnectionCloseFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, Exception?> DedicatedRecoveryObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Dedicated connection recovery observer threw");

    internal static void DedicatedRecoveryObserverFailed(this ILogger logger, Exception? error)
        => DedicatedRecoveryObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, string, long, string, int, Exception?> MaintenanceNotificationReceivedMessage =
        LoggerMessage.Define<string, long, string, int>(LogLevel.Information, default, "Redis maintenance {Kind} ({SequenceId}) on {Host}:{Port}");

    internal static void MaintenanceNotificationReceived(this ILogger logger, string kind, long sequenceId, string host, int port)
        => MaintenanceNotificationReceivedMessage(logger, kind, sequenceId, host, port, null);

    private static readonly Action<ILogger, Exception?> MaintenanceDiagnosticObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Maintenance diagnostic listener threw");

    internal static void MaintenanceDiagnosticObserverFailed(this ILogger logger, Exception? error)
        => MaintenanceDiagnosticObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> ReadReplicaDrainFailedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Draining a removed read replica at {Endpoint} failed");

    internal static void ReadReplicaDrainFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => ReadReplicaDrainFailedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, Exception?> ReadReplicaCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a removed read replica failed");

    internal static void ReadReplicaCloseFailed(this ILogger logger, Exception? error)
        => ReadReplicaCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SentinelReplicaBackgroundRefreshFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Background Sentinel replica refresh failed");

    internal static void SentinelReplicaBackgroundRefreshFailed(this ILogger logger, Exception? error)
        => SentinelReplicaBackgroundRefreshFailedMessage(logger, error);

    private static readonly Action<ILogger, int, Exception?> SentinelReplicaDiscoveryFailedMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default, "Sentinel replica discovery failed; serving the last known {Count} replica endpoint(s) until Sentinel answers");

    internal static void SentinelReplicaDiscoveryFailed(this ILogger logger, int count, Exception? error)
        => SentinelReplicaDiscoveryFailedMessage(logger, count, error);

    private static readonly Action<ILogger, Exception?> SentinelReplicaRefreshFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Sentinel replica refresh failed; retaining current endpoints");

    internal static void SentinelReplicaRefreshFailed(this ILogger logger, Exception? error)
        => SentinelReplicaRefreshFailedMessage(logger, error);

    private static readonly Action<ILogger, int, Exception?> SentinelReplicaDiscoveryRecoveredMessage =
        LoggerMessage.Define<int>(LogLevel.Information, default, "Sentinel replica discovery recovered with {Count} replica endpoint(s)");

    internal static void SentinelReplicaDiscoveryRecovered(this ILogger logger, int count)
        => SentinelReplicaDiscoveryRecoveredMessage(logger, count, null);

    private static readonly Action<ILogger, string, string, int, Exception?> CredentialRefreshFailedMessage =
        LoggerMessage.Define<string, string, int>(LogLevel.Warning, new EventId(4001, "CredentialRefreshFailed"), "Credential renewal failed at {Stage} for {Host}:{Port}");

    internal static void CredentialRefreshFailed(this ILogger logger, string stage, string host, int port)
        => CredentialRefreshFailedMessage(logger, stage, host, port, null);

    private static readonly Action<ILogger, string, string, string, Exception?> CoordinationCleanupAbandonedMessage =
        LoggerMessage.Define<string, string, string>(LogLevel.Warning, new EventId(4101, "CoordinationCleanupAbandoned"), "Background {Primitive} cleanup stopped at its {Stage} step ({Reason}); the owner may stay on Redis until it expires or is removed manually");

    internal static void CoordinationCleanupAbandoned(this ILogger logger, string primitive, string stage, string reason)
        => CoordinationCleanupAbandonedMessage(logger, primitive, stage, reason, null);

    private static readonly Action<ILogger, string, Exception?> ReconnectTelemetryObserverFailedMessage =
        LoggerMessage.Define<string>(LogLevel.Warning, default, "Reconnect telemetry listener threw for {Scope}");

    internal static void ReconnectTelemetryObserverFailed(this ILogger logger, string scope, Exception? error)
        => ReconnectTelemetryObserverFailedMessage(logger, scope, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorRestartingMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Warning, default, "Restarting an unexpectedly completed Sentinel event monitor at {Endpoint}");

    internal static void SentinelMonitorRestarting(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorRestartingMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorFailedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Warning, default, "Sentinel event monitor failed at {Endpoint}");

    internal static void SentinelMonitorFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorFailedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorCleanupFailedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Sentinel event monitor cleanup failed at {Endpoint}");

    internal static void SentinelMonitorCleanupFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelMonitorCleanupFailedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorExhaustedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Warning, default, "Sentinel event monitor exhausted reconnect attempts at {Endpoint}; failover events from this "
                    + "Sentinel are not observed until a new primary is published, and discovery runs on demand");

    internal static void SentinelMonitorExhausted(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMonitorExhaustedMessage(logger, endpoint, null);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMonitorResumedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Information, default, "Sentinel event monitor at {Endpoint} resumes after a new primary was published");

    internal static void SentinelMonitorResumed(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMonitorResumedMessage(logger, endpoint, null);

    private static readonly Action<ILogger, string, Exception?> SentinelSwitchSourceResolutionFailedMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, default, "Could not resolve Sentinel switch source {Host}");

    internal static void SentinelSwitchSourceResolutionFailed(this ILogger logger, string host, Exception? error)
        => SentinelSwitchSourceResolutionFailedMessage(logger, host, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelMalformedReplicasMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Sentinel {Endpoint} returned a malformed SENTINEL REPLICAS reply");

    internal static void SentinelMalformedReplicas(this ILogger logger, RespireEndpoint endpoint)
        => SentinelMalformedReplicasMessage(logger, endpoint, null);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelReplicaDiscoveryUnavailableMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Optional Sentinel replica discovery failed at {Endpoint}");

    internal static void SentinelReplicaDiscoveryUnavailable(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelReplicaDiscoveryUnavailableMessage(logger, endpoint, error);

    private static readonly Action<ILogger, string, int, Exception?> SentinelPrimaryDiscoveryFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Redis Sentinel discovery or primary connection failed through {Host}:{Port}");

    internal static void SentinelPrimaryDiscoveryFailed(this ILogger logger, string host, int port, Exception? error)
        => SentinelPrimaryDiscoveryFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, RespireEndpoint, Exception?> SentinelOptionalDiscoveryFailedMessage =
        LoggerMessage.Define<string, RespireEndpoint>(LogLevel.Debug, default, "Optional Sentinel {Stage} discovery failed at {Sentinel}");

    internal static void SentinelOptionalDiscoveryFailed(this ILogger logger, string stage, RespireEndpoint sentinel, Exception? error)
        => SentinelOptionalDiscoveryFailedMessage(logger, stage, sentinel, error);

    private static readonly Action<ILogger, string, Exception?> SentinelPeerDiscoveryUnavailableMessage =
        LoggerMessage.Define<string>(LogLevel.Debug, default, "Sentinel peer discovery was unavailable: {Error}");

    internal static void SentinelPeerDiscoveryUnavailable(this ILogger logger, in RespValue reply)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            SentinelPeerDiscoveryUnavailableMessage(logger, reply.GetErrorMessage(), null);
    }

    private static readonly Action<ILogger, Exception?> SentinelRetiredPrimaryProbeFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Sentinel primary probe failed on a retired generation; rediscovering");

    internal static void SentinelRetiredPrimaryProbeFailed(this ILogger logger, Exception? error)
        => SentinelRetiredPrimaryProbeFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SentinelStateObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Sentinel state observer failed");

    internal static void SentinelStateObserverFailed(this ILogger logger, Exception? error)
        => SentinelStateObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelRetirementDrainFailedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Debug, default, "Sentinel transport retirement reported an error after draining at {Endpoint}");

    internal static void SentinelRetirementDrainFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelRetirementDrainFailedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelCorrectionFenceUnacknowledgedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Warning, default, "Sentinel generation at {Endpoint} retains an unacknowledged correction fence");

    internal static void SentinelCorrectionFenceUnacknowledged(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelCorrectionFenceUnacknowledgedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, RespireEndpoint, Exception?> SentinelGenerationCleanupFailedMessage =
        LoggerMessage.Define<RespireEndpoint>(LogLevel.Warning, default, "Sentinel generation cleanup failed at {Endpoint}; retained until client disposal");

    internal static void SentinelGenerationCleanupFailed(this ILogger logger, RespireEndpoint endpoint, Exception? error)
        => SentinelGenerationCleanupFailedMessage(logger, endpoint, error);

    private static readonly Action<ILogger, TimeSpan, int, Exception?> SentinelMonitorShutdownTimedOutMessage =
        LoggerMessage.Define<TimeSpan, int>(LogLevel.Warning, default, "Sentinel event monitoring did not stop within {Timeout}; {Count} task(s) still running");

    internal static void SentinelMonitorShutdownTimedOut(this ILogger logger, TimeSpan timeout, Task[] tasks)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            SentinelMonitorShutdownTimedOutMessage(logger, timeout, tasks.Count(static task => !task.IsCompleted), null);
    }

    private static readonly Action<ILogger, Exception?> SentinelMovingUploadCleanupFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Sentinel upload pool cleanup after MOVING failed");

    internal static void SentinelMovingUploadCleanupFailed(this ILogger logger, Exception? error)
        => SentinelMovingUploadCleanupFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SentinelSwitchPrimaryRetirementFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Could not retire the primary named by a Sentinel switch event");

    internal static void SentinelSwitchPrimaryRetirementFailed(this ILogger logger, Exception? error)
        => SentinelSwitchPrimaryRetirementFailedMessage(logger, error);

    private static readonly Action<ILogger, int, Exception?> SentinelNotificationDiscoveryRecoveredMessage =
        LoggerMessage.Define<int>(LogLevel.Information, default, "Sentinel notification-triggered primary discovery succeeded after {Failures} failed attempt(s)");

    internal static void SentinelNotificationDiscoveryRecovered(this ILogger logger, int failures)
        => SentinelNotificationDiscoveryRecoveredMessage(logger, failures, null);

    private static readonly Action<ILogger, double, bool, int, int, long, Exception?> ThreadPoolSchedulingDelayedMessage =
        LoggerMessage.Define<double, bool, int, int, long>(LogLevel.Warning, default, "Thread-pool scheduling delayed by {SchedulingDelayMs} ms (probe pending: {ProbePending}); workers busy/min: {BusyWorkers}/{MinWorkers}; queued work: {PendingWorkItems}. " +
                    "Inspect synchronous blocking and long-running work; use asynchronous I/O and diagnose runtime counters before changing minimum worker threads.");

    internal static void ThreadPoolSchedulingDelayed(this ILogger logger, double schedulingDelayMs, bool probePending, int busyWorkers, int minWorkers, long pendingWorkItems)
        => ThreadPoolSchedulingDelayedMessage(logger, schedulingDelayMs, probePending, busyWorkers, minWorkers, pendingWorkItems, null);

    private static readonly Action<ILogger, string, int, Exception?> ConnectionConnectedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Connected to {Host}:{Port}");

    internal static void ConnectionConnected(this ILogger logger, string host, int port)
        => ConnectionConnectedMessage(logger, host, port, null);

    private static readonly Action<ILogger, string, int, Exception?> HelloResp2FallbackMessage =
        LoggerMessage.Define<string, int>(LogLevel.Information, default, "HELLO 3 is unsupported by {Host}:{Port}; using RESP2 on this connection");

    internal static void HelloResp2Fallback(this ILogger logger, string host, int port)
        => HelloResp2FallbackMessage(logger, host, port, null);

    private static readonly Action<ILogger, string, int, Exception?> HelloResp3NegotiatedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Negotiated RESP3 with {Host}:{Port}");

    internal static void HelloResp3Negotiated(this ILogger logger, string host, int port)
        => HelloResp3NegotiatedMessage(logger, host, port, null);

    private static readonly Action<ILogger, string, int, Exception?> ConnectionSendFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Send failed for {Host}:{Port}; aborting connection");

    internal static void ConnectionSendFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionSendFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> ConnectionCloseObserverFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Connection close observer threw for {Host}:{Port}");

    internal static void ConnectionCloseObserverFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionCloseObserverFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> ConnectionFailureObserverFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Connection failure observer threw for {Host}:{Port}");

    internal static void ConnectionFailureObserverFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionFailureObserverFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, Exception?> ConnectionPushHandlerFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Push handler threw for {Host}:{Port}; message dropped");

    internal static void ConnectionPushHandlerFailed(this ILogger logger, string host, int port, Exception? error)
        => ConnectionPushHandlerFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, string, int, long, Exception?> ConnectionQueuedDeliveryDelayedMessage =
        LoggerMessage.Define<string, int, long>(LogLevel.Warning, default, "Replies for {Host}:{Port} were still queued for delivery {Milliseconds} ms after the connection closed; "
                        + "finishing disposal while their delivery runner waits for a thread-pool thread.");

    internal static void ConnectionQueuedDeliveryDelayed(this ILogger logger, string host, int port, long milliseconds)
        => ConnectionQueuedDeliveryDelayedMessage(logger, host, port, milliseconds, null);

    private static readonly Action<ILogger, string, int, long, Exception?> ConnectionContinuationDeliveryBlockedMessage =
        LoggerMessage.Define<string, int, long>(LogLevel.Warning, default, "Reply delivery for {Host}:{Port} was blocked by a continuation for over {Milliseconds} ms; "
                + "delivering the remaining replies on another thread. Avoid blocking on Respire results "
                + "inside continuations.");

    internal static void ConnectionContinuationDeliveryBlocked(this ILogger logger, string host, int port, long milliseconds)
        => ConnectionContinuationDeliveryBlockedMessage(logger, host, port, milliseconds, null);

    private static readonly Action<ILogger, int, string, int, string, Exception?> ConnectionInflightFailedMessage =
        LoggerMessage.Define<int, string, int, string>(LogLevel.Debug, default, "Failed {Count} in-flight commands on {Host}:{Port}: {Reason}");

    internal static void ConnectionInflightFailed(this ILogger logger, int count, string host, int port, Exception failure)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            ConnectionInflightFailedMessage(logger, count, host, port, failure.Message, null);
    }

    private static readonly Action<ILogger, string, int, Exception?> ConnectionDisconnectedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Disconnected from {Host}:{Port}");

    internal static void ConnectionDisconnected(this ILogger logger, string host, int port)
        => ConnectionDisconnectedMessage(logger, host, port, null);

    private static readonly Action<ILogger, Exception?> ShardedSubscriptionReceiveFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Sharded subscription connection closed with a receive failure");

    internal static void ShardedSubscriptionReceiveFailed(this ILogger logger, Exception? error)
        => ShardedSubscriptionReceiveFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> ShardedSubscriptionCloseFailedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Debug, default, "Closing sharded subscription connection {Host}:{Port} after SUNSUBSCRIBE failed");

    internal static void ShardedSubscriptionCloseFailed(this ILogger logger, string host, int port, Exception? error)
        => ShardedSubscriptionCloseFailedMessage(logger, host, port, error);

    private static readonly Action<ILogger, int, Exception?> ShardedSubscriptionRecoveryFailedMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default, "Sharded pub/sub recovery attempt {Attempt} failed");

    internal static void ShardedSubscriptionRecoveryFailed(this ILogger logger, int attempt, Exception? error)
        => ShardedSubscriptionRecoveryFailedMessage(logger, attempt, error);

    private static readonly Action<ILogger, Exception?> NotificationRollbackCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a rolled back cluster notification connection failed");

    internal static void NotificationRollbackCloseFailed(this ILogger logger, Exception? error)
        => NotificationRollbackCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, string, string, int, Exception?> NotificationRouteRejectedMessage =
        LoggerMessage.Define<string, string, int>(LogLevel.Warning, default, "Cluster notification route {Route} was rejected by {Host}:{Port}");

    internal static void NotificationRouteRejected(this ILogger logger, RespireChannel route, string host, int port, Exception? error)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            NotificationRouteRejectedMessage(logger, route.ToString(), host, port, error);
    }

    private static readonly Action<ILogger, Exception?> NotificationReplacementCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a failed cluster notification replacement failed");

    internal static void NotificationReplacementCloseFailed(this ILogger logger, Exception? error)
        => NotificationReplacementCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationRetiredReplacementCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a replacement for a retired cluster notification node failed");

    internal static void NotificationRetiredReplacementCloseFailed(this ILogger logger, Exception? error)
        => NotificationRetiredReplacementCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationSupersededCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a superseded cluster notification connection failed");

    internal static void NotificationSupersededCloseFailed(this ILogger logger, Exception? error)
        => NotificationSupersededCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, Exception?> NotificationRecoveryStoppedMessage =
        LoggerMessage.Define<string, int>(LogLevel.Warning, default, "Cluster notification recovery for {Host}:{Port} stopped unexpectedly");

    internal static void NotificationRecoveryStopped(this ILogger logger, string host, int port, Exception? error)
        => NotificationRecoveryStoppedMessage(logger, host, port, error);

    private static readonly Action<ILogger, Exception?> NotificationReconnectTelemetryFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Cluster notification reconnect telemetry listener threw");

    internal static void NotificationReconnectTelemetryFailed(this ILogger logger, Exception? error)
        => NotificationReconnectTelemetryFailedMessage(logger, error);

    private static readonly Action<ILogger, string, int, TimeSpan, Exception?> NotificationReconnectRetryMessage =
        LoggerMessage.Define<string, int, TimeSpan>(LogLevel.Warning, default, "Cluster notification reconnect failed for {Host}:{Port}; retrying in {Delay}");

    internal static void NotificationReconnectRetry(this ILogger logger, string host, int port, TimeSpan delay, Exception? error)
        => NotificationReconnectRetryMessage(logger, host, port, delay, error);

    private static readonly Action<ILogger, Exception?> NotificationReconnectExhaustionTelemetryFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Cluster notification reconnect exhaustion telemetry listener threw");

    internal static void NotificationReconnectExhaustionTelemetryFailed(this ILogger logger, Exception? error)
        => NotificationReconnectExhaustionTelemetryFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationExhaustionUnsubscribeFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Cluster notification unsubscribe failed during exhaustion cleanup");

    internal static void NotificationExhaustionUnsubscribeFailed(this ILogger logger, Exception? error)
        => NotificationExhaustionUnsubscribeFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationExhaustionCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing an exhausted cluster notification connection failed");

    internal static void NotificationExhaustionCloseFailed(this ILogger logger, Exception? error)
        => NotificationExhaustionCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationUnsubscribeFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Cluster notification unsubscribe failed");

    internal static void NotificationUnsubscribeFailed(this ILogger logger, Exception? error)
        => NotificationUnsubscribeFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationUncertainCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing an uncertain cluster notification connection failed");

    internal static void NotificationUncertainCloseFailed(this ILogger logger, Exception? error)
        => NotificationUncertainCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationRetiredCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a retired cluster notification connection failed");

    internal static void NotificationRetiredCloseFailed(this ILogger logger, Exception? error)
        => NotificationRetiredCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationTopologyReconciliationFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Cluster notification topology reconciliation failed");

    internal static void NotificationTopologyReconciliationFailed(this ILogger logger, Exception? error)
        => NotificationTopologyReconciliationFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationSubscribeFailureCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a cluster notification connection after a failed subscribe failed");

    internal static void NotificationSubscribeFailureCloseFailed(this ILogger logger, Exception? error)
        => NotificationSubscribeFailureCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationExhaustedSubscriptionReleaseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Releasing an exhausted cluster notification subscription failed");

    internal static void NotificationExhaustedSubscriptionReleaseFailed(this ILogger logger, Exception? error)
        => NotificationExhaustedSubscriptionReleaseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SubscriptionGapObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Subscription delivery-gap observer threw");

    internal static void SubscriptionGapObserverFailed(this ILogger logger, Exception? error)
        => SubscriptionGapObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SubscriptionCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a failed subscription connection failed");

    internal static void SubscriptionCloseFailed(this ILogger logger, Exception? error)
        => SubscriptionCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, TimeSpan, Exception?> SubscriptionReconnectRetryMessage =
        LoggerMessage.Define<TimeSpan>(LogLevel.Warning, default, "Pub/sub reconnect failed; retrying in {Delay}");

    internal static void SubscriptionReconnectRetry(this ILogger logger, TimeSpan delay, Exception? error)
        => SubscriptionReconnectRetryMessage(logger, delay, error);

    private static readonly Action<ILogger, Exception?> SubscriptionRecoveryMetricFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Pub/sub recovery metric observer threw");

    internal static void SubscriptionRecoveryMetricFailed(this ILogger logger, Exception? error)
        => SubscriptionRecoveryMetricFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NotificationConnectionCloseFailedMessage =
        LoggerMessage.Define(LogLevel.Debug, default, "Closing a cluster notification connection failed");

    internal static void NotificationConnectionCloseFailed(this ILogger logger, Exception? error)
        => NotificationConnectionCloseFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> SubscriptionRecoveryEpisodeObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Pub/sub recovery episode observer threw");

    internal static void SubscriptionRecoveryEpisodeObserverFailed(this ILogger logger, Exception? error)
        => SubscriptionRecoveryEpisodeObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, int, Exception?> SubscriptionRecoveryAttemptFailedMessage =
        LoggerMessage.Define<int>(LogLevel.Warning, default, "Pub/sub recovery attempt {Attempt} failed");

    internal static void SubscriptionRecoveryAttemptFailed(this ILogger logger, int attempt, Exception? error)
        => SubscriptionRecoveryAttemptFailedMessage(logger, attempt, error);

    private static readonly Action<ILogger, Exception?> SubscriptionReplacementCleanupFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Failed to clean up a pub/sub replacement");

    internal static void SubscriptionReplacementCleanupFailed(this ILogger logger, Exception? error)
        => SubscriptionReplacementCleanupFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> NativeLockFencingUnavailableMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Lock releases run without connection fencing because CLIENT ID or CLIENT KILL was denied. " +
                "An uncertain release still treats ownership as lost, but its delete may run later. " +
                "Grant the client and client|id/client|kill permissions to restore fencing.");

    internal static void NativeLockFencingUnavailable(this ILogger logger, Exception? error)
        => NativeLockFencingUnavailableMessage(logger, error);

    private static readonly Action<ILogger, long, RespireEndpoint, string, Exception?> NativeLockFenceFailedMessage =
        LoggerMessage.Define<long, RespireEndpoint, string>(LogLevel.Warning, default, "Could not fence Redis client {ServerClientId} at {Endpoint} after an uncertain {Operation}; " +
                "the command may still execute. Ownership was already treated as lost.");

    internal static void NativeLockFenceFailed(this ILogger logger, long serverClientId, RespireEndpoint endpoint, string operation, Exception? error)
        => NativeLockFenceFailedMessage(logger, serverClientId, endpoint, operation, error);

    private static readonly Action<ILogger, string, string, Exception?> FailoverDuplicateDeploymentMessage =
        LoggerMessage.Define<string, string>(LogLevel.Warning, default, "Failover candidate {Endpoint} was marked unhealthy because it duplicates another candidate's deployment: {Reason}");

    internal static void FailoverDuplicateDeployment(this ILogger logger, RespireEndpoint endpoint, string reason)
    {
        if (logger.IsEnabled(LogLevel.Warning))
            FailoverDuplicateDeploymentMessage(logger, endpoint.ToString(), reason, null);
    }

    private static readonly Action<ILogger, string, string, string, Exception?> FailoverEndpointSwitchedMessage =
        LoggerMessage.Define<string, string, string>(LogLevel.Information, default, "Failover group switched from {PreviousEndpoint} to {CurrentEndpoint} ({Reason})");

    internal static void FailoverEndpointSwitched(this ILogger logger, RespireEndpoint? previousEndpoint, RespireEndpoint? currentEndpoint, string reason)
    {
        if (logger.IsEnabled(LogLevel.Information))
            FailoverEndpointSwitchedMessage(logger, previousEndpoint?.ToString() ?? "none", currentEndpoint?.ToString() ?? "none", reason, null);
    }

    private static readonly Action<ILogger, Exception?> FailoverSwitchObserverFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Failover group EndpointSwitched handler threw");

    internal static void FailoverSwitchObserverFailed(this ILogger logger, Exception? error)
        => FailoverSwitchObserverFailedMessage(logger, error);

    private static readonly Action<ILogger, Exception?> FailoverMonitorFailedMessage =
        LoggerMessage.Define(LogLevel.Warning, default, "Failover group health monitor round failed");

    internal static void FailoverMonitorFailed(this ILogger logger, Exception? error)
        => FailoverMonitorFailedMessage(logger, error);

}
