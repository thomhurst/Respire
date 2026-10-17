---
title: Logging event IDs
description: Stable diagnostic event IDs for Respire packages.
---

# Logging event IDs

All diagnostic logging in shipping packages uses source-generated `LoggerMessage` methods.
Event IDs and names below are stable within each package. Identify an event by its package
and ID; IDs can repeat across packages. Logger categories still identify the component or
closed generic type emitting the event. Packages absent from this table emit no events of their own.

Existing explicit IDs are preserved. Previously unnamed event-zero diagnostics now use the
named IDs below; update filters that depended on event zero. The stream-handler and missing
Sentinel-epoch warnings also now have explicit IDs instead of generated IDs. Message templates,
structured properties, levels, and attached exceptions are unchanged.

Sentinel event 139 uses the severity selected for the received Sentinel notification.
Disabled levels skip state creation and expensive diagnostic formatting.

When adding an event, choose an unused positive ID in its package and add it here. Never
renumber or reuse an existing ID or rename its event. Keep retired IDs reserved. The
`LoggingCatalogTests` guard rejects duplicate IDs and checks source against this catalog;
core logging contract tests also pin templates, levels, structured values, and identities.

See [observability](observability.md#logging) for logger configuration.

| Package | ID | Event name | Level |
| --- | ---: | --- | --- |
| `Respire.Idempotency` | 1 | `ResponseLimitExceeded` | Warning |
| `Respire.Idempotency` | 2 | `AbandonFailed` | Warning |
| `Respire.Idempotency` | 3 | `CompletionFailed` | Warning |
| `Respire` | 1 | `StreamReadRetry` | Warning |
| `Respire` | 2 | `DeadConnectionReplaced` | Information |
| `Respire` | 3 | `RejectedReplacementDisposalFailed` | Warning |
| `Respire` | 4 | `ReconnectKeptHealthyConnection` | Debug |
| `Respire` | 5 | `ReconnectExhausted` | Warning |
| `Respire` | 6 | `ReconnectBackoffDeferred` | Warning |
| `Respire` | 7 | `ReconnectDeferred` | Warning |
| `Respire` | 8 | `ConnectionStateObserverFailed` | Warning |
| `Respire` | 9 | `ConnectionSlotObserverFailed` | Warning |
| `Respire` | 10 | `ReconnectMetricObserverFailed` | Warning |
| `Respire` | 11 | `MaintenanceBarrierAbortFailed` | Debug |
| `Respire` | 12 | `MaintenanceBarrierFailed` | Debug |
| `Respire` | 13 | `RetirementDisposalFailed` | Debug |
| `Respire` | 14 | `MovingSuperseded` | Debug |
| `Respire` | 15 | `MovingFailed` | Warning |
| `Respire` | 16 | `MovingRetirementStopped` | Debug |
| `Respire` | 17 | `MovingGraceAttemptFailed` | Warning |
| `Respire` | 18 | `MovingRetry` | Debug |
| `Respire` | 19 | `MovingPublicationSuperseded` | Debug |
| `Respire` | 20 | `MovingPublishedLate` | Warning |
| `Respire` | 21 | `MovingCacheFenceObserverFailed` | Debug |
| `Respire` | 22 | `ContinuityFlushObserverFailed` | Debug |
| `Respire` | 23 | `MovingSocketFenceFailed` | Warning |
| `Respire` | 24 | `MovingSocketDrainStopped` | Debug |
| `Respire` | 25 | `MovingDrainGraceExceeded` | Warning |
| `Respire` | 26 | `MovingDrainDisposalStopped` | Debug |
| `Respire` | 27 | `MovingDrainFailed` | Warning |
| `Respire` | 28 | `MovingSocketAbortFailed` | Debug |
| `Respire` | 29 | `MovingSocketDrainCleanupCompleted` | Debug |
| `Respire` | 30 | `DedicatedMovingCleanupFailed` | Warning |
| `Respire` | 31 | `ClusterUnknownEndpointSkipped` | Debug |
| `Respire` | 32 | `ClusterDiscoveryObserverFailed` | Warning |
| `Respire` | 33 | `ClusterMigrationDropMetricFailed` | Error |
| `Respire` | 34 | `ClusterMigrationQueueFull` | Warning |
| `Respire` | 35 | `ClusterMigrationMetricObserverFailed` | Warning |
| `Respire` | 36 | `ClusterMigrationApplyFailed` | Error |
| `Respire` | 37 | `ClusterMigrationSequenceIgnored` | Debug |
| `Respire` | 38 | `ClusterReplicaRouteRefreshFailed` | Warning |
| `Respire` | 39 | `ClusterReadRoleRetry` | Debug |
| `Respire` | 40 | `ClusterReplicaRedirectRefreshFailed` | Warning |
| `Respire` | 41 | `ClusterRetirementCorrectionNeeded` | Debug |
| `Respire` | 42 | `ClusterFenceUnavailable` | Warning |
| `Respire` | 43 | `ClusterFenceRetry` | Debug |
| `Respire` | 44 | `ClusterRetirementFailed` | Warning |
| `Respire` | 45 | `ClusterGenerationCleanupFailed` | Warning |
| `Respire` | 46 | `ClusterRetiredPoolCleanupFailed` | Warning |
| `Respire` | 47 | `DedicatedPoolDisposalFailed` | Warning |
| `Respire` | 48 | `DedicatedConnectionCloseFailed` | Warning |
| `Respire` | 49 | `DedicatedRecoveryObserverFailed` | Warning |
| `Respire` | 50 | `MaintenanceNotificationReceived` | Information |
| `Respire` | 51 | `MaintenanceDiagnosticObserverFailed` | Warning |
| `Respire` | 52 | `ReadReplicaDrainFailed` | Debug |
| `Respire` | 53 | `ReadReplicaCloseFailed` | Debug |
| `Respire` | 54 | `SentinelReplicaBackgroundRefreshFailed` | Debug |
| `Respire` | 55 | `SentinelReplicaDiscoveryFailed` | Warning |
| `Respire` | 56 | `SentinelReplicaRefreshFailed` | Debug |
| `Respire` | 57 | `SentinelReplicaDiscoveryRecovered` | Information |
| `Respire` | 58 | `ReconnectTelemetryObserverFailed` | Warning |
| `Respire` | 59 | `SentinelMonitorRestarting` | Warning |
| `Respire` | 60 | `SentinelMonitorFailed` | Warning |
| `Respire` | 61 | `SentinelMonitorCleanupFailed` | Debug |
| `Respire` | 62 | `SentinelMonitorExhausted` | Warning |
| `Respire` | 63 | `SentinelMonitorResumed` | Information |
| `Respire` | 64 | `SentinelSwitchSourceResolutionFailed` | Debug |
| `Respire` | 65 | `SentinelMalformedReplicas` | Debug |
| `Respire` | 66 | `SentinelReplicaDiscoveryUnavailable` | Debug |
| `Respire` | 67 | `SentinelPrimaryDiscoveryFailed` | Warning |
| `Respire` | 68 | `SentinelOptionalDiscoveryFailed` | Debug |
| `Respire` | 69 | `SentinelPeerDiscoveryUnavailable` | Debug |
| `Respire` | 70 | `SentinelRetiredPrimaryProbeFailed` | Debug |
| `Respire` | 71 | `SentinelStateObserverFailed` | Warning |
| `Respire` | 72 | `SentinelRetirementDrainFailed` | Debug |
| `Respire` | 73 | `SentinelCorrectionFenceUnacknowledged` | Warning |
| `Respire` | 74 | `SentinelGenerationCleanupFailed` | Warning |
| `Respire` | 75 | `SentinelMonitorShutdownTimedOut` | Warning |
| `Respire` | 76 | `SentinelMovingUploadCleanupFailed` | Warning |
| `Respire` | 77 | `SentinelSwitchPrimaryRetirementFailed` | Debug |
| `Respire` | 78 | `SentinelNotificationDiscoveryRecovered` | Information |
| `Respire` | 79 | `ThreadPoolSchedulingDelayed` | Warning |
| `Respire` | 80 | `ConnectionConnected` | Debug |
| `Respire` | 81 | `HelloResp2Fallback` | Information |
| `Respire` | 82 | `HelloResp3Negotiated` | Debug |
| `Respire` | 83 | `ConnectionSendFailed` | Debug |
| `Respire` | 84 | `ConnectionCloseObserverFailed` | Warning |
| `Respire` | 85 | `ConnectionFailureObserverFailed` | Warning |
| `Respire` | 86 | `ConnectionPushHandlerFailed` | Warning |
| `Respire` | 87 | `ConnectionQueuedDeliveryDelayed` | Warning |
| `Respire` | 88 | `ConnectionContinuationDeliveryBlocked` | Warning |
| `Respire` | 89 | `ConnectionInflightFailed` | Debug |
| `Respire` | 90 | `ConnectionDisconnected` | Debug |
| `Respire` | 91 | `ShardedSubscriptionReceiveFailed` | Warning |
| `Respire` | 92 | `ShardedSubscriptionCloseFailed` | Debug |
| `Respire` | 93 | `ShardedSubscriptionRecoveryFailed` | Warning |
| `Respire` | 94 | `NotificationRollbackCloseFailed` | Debug |
| `Respire` | 95 | `NotificationRouteRejected` | Warning |
| `Respire` | 96 | `NotificationReplacementCloseFailed` | Debug |
| `Respire` | 97 | `NotificationRetiredReplacementCloseFailed` | Debug |
| `Respire` | 98 | `NotificationSupersededCloseFailed` | Debug |
| `Respire` | 99 | `NotificationRecoveryStopped` | Warning |
| `Respire` | 100 | `NotificationReconnectTelemetryFailed` | Warning |
| `Respire` | 101 | `NotificationReconnectRetry` | Warning |
| `Respire` | 102 | `NotificationReconnectExhaustionTelemetryFailed` | Warning |
| `Respire` | 103 | `NotificationExhaustionUnsubscribeFailed` | Debug |
| `Respire` | 104 | `NotificationExhaustionCloseFailed` | Debug |
| `Respire` | 105 | `NotificationUnsubscribeFailed` | Debug |
| `Respire` | 106 | `NotificationUncertainCloseFailed` | Debug |
| `Respire` | 107 | `NotificationRetiredCloseFailed` | Debug |
| `Respire` | 108 | `NotificationTopologyReconciliationFailed` | Warning |
| `Respire` | 109 | `NotificationSubscribeFailureCloseFailed` | Debug |
| `Respire` | 110 | `NotificationExhaustedSubscriptionReleaseFailed` | Debug |
| `Respire` | 111 | `SubscriptionGapObserverFailed` | Warning |
| `Respire` | 112 | `SubscriptionCloseFailed` | Debug |
| `Respire` | 113 | `SubscriptionReconnectRetry` | Warning |
| `Respire` | 114 | `SubscriptionRecoveryMetricFailed` | Warning |
| `Respire` | 115 | `NotificationConnectionCloseFailed` | Debug |
| `Respire` | 116 | `SubscriptionRecoveryEpisodeObserverFailed` | Warning |
| `Respire` | 117 | `SubscriptionRecoveryAttemptFailed` | Warning |
| `Respire` | 118 | `SubscriptionReplacementCleanupFailed` | Warning |
| `Respire` | 119 | `NativeLockFencingUnavailable` | Warning |
| `Respire` | 120 | `NativeLockFenceFailed` | Warning |
| `Respire` | 121 | `FailoverDuplicateDeployment` | Warning |
| `Respire` | 122 | `FailoverEndpointSwitched` | Information |
| `Respire` | 123 | `FailoverSwitchObserverFailed` | Warning |
| `Respire` | 124 | `FailoverMonitorFailed` | Warning |
| `Respire` | 125 | `NearestReadCandidateUnavailable` | Debug |
| `Respire` | 126 | `ReadReplicaUnavailable` | Debug |
| `Respire` | 127 | `ClusterMigrationBudgetRejected` | Debug |
| `Respire` | 128 | `ClusterMigrationSlotsRejected` | Debug |
| `Respire` | 129 | `ClusterTopologyRefreshFailed` | Debug |
| `Respire` | 130 | `ClusterTopologyRefreshRetry` | Warning |
| `Respire` | 131 | `ClusterTopologyWorkerFailed` | Warning |
| `Respire` | 132 | `ClusterReadonlyRecoveryFailed` | Debug |
| `Respire` | 133 | `ClusterReadonlyRecoveryBeforeRefreshFailed` | Debug |
| `Respire` | 134 | `ClusterTopologyCandidatesFailed` | Debug |
| `Respire` | 135 | `SentinelMonitorEstablished` | Information |
| `Respire` | 136 | `SentinelDeliveryGap` | Information |
| `Respire` | 137 | `SentinelNotificationFirstDiscoveryFailure` | Warning |
| `Respire` | 138 | `SentinelNotificationRepeatedDiscoveryFailure` | Debug |
| `Respire` | 139 | `SentinelEvent` | Dynamic |
| `Respire` | 140 | `LogMissingEpoch` | Warning |
| `Respire` | 141 | `IgnoredConnectionStringOption` | Warning |
| `Respire` | 142 | `ConnectionRecycled` | Information |
| `Respire` | 4001 | `CredentialRefreshFailed` | Warning |
| `Respire` | 4101 | `CoordinationCleanupAbandoned` | Warning |
| `Respire.FusionCache` | 1 | `LogGap` | Warning |
| `Respire.FusionCache` | 2 | `LogMessageFailure` | Warning |
| `Respire.FusionCache` | 3 | `LogSubscriptionEnded` | Warning |
| `Respire.FusionCache` | 4 | `LogConsumerFailure` | Error |
| `Respire.FusionCache` | 5 | `LogConnectionFailure` | Warning |
| `Respire.FusionCache` | 6 | `LogOwnershipLost` | Warning |
| `Respire.FusionCache` | 7 | `LogCleanupFailed` | Warning |
| `Respire.OutputCaching` | 1 | `OutputCacheTagCleanupFailed` | Warning |
| `Respire.OutputCaching` | 2 | `OutputCacheCleanupLockLost` | Debug |
| `Respire.SignalR` | 3 | `Subscribing` | Trace |
| `Respire.SignalR` | 4 | `ReceivedFromChannel` | Trace |
| `Respire.SignalR` | 5 | `PublishToChannel` | Trace |
| `Respire.SignalR` | 6 | `Unsubscribe` | Trace |
| `Respire.SignalR` | 10 | `FailedWritingMessage` | Debug |
| `Respire.SignalR` | 11 | `InternalMessageFailed` | Warning |
| `Respire.SignalR` | 12 | `MismatchedServers` | Error |
| `Respire.SignalR` | 13 | `ErrorForwardingResult` | Error |
| `Respire.SignalR` | 15 | `ErrorParsingResult` | Warning |
| `Respire.SignalR` | 16 | `DeliveryGap` | Warning |
| `Respire.SignalR` | 17 | `SubscriptionEnded` | Error |
| `Respire.SignalR` | 18 | `RemoteResultExpired` | Debug |
| `Respire.Streaming` | 1 | `HandlerFailed` | Warning |
| `Respire.Streaming` | 2 | `LogPassFailure` | Error |
