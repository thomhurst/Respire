using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Tests;

public partial class LifecycleLoggingTests
{
    [Test]
    public async Task TypedMessagesPreserveLegacyStructuredOutput()
    {
        var error = new InvalidOperationException("provider-independent failure");
        var endpoint = new RespireEndpoint("redis.example", 6379);
        RespireChannel channel = "notifications";
        var delay = TimeSpan.FromMilliseconds(250);
        using var reply = RespValue.Error("ERR peers unavailable");
        Task[] tasks = [Task.CompletedTask, new TaskCompletionSource().Task];
        (Action<ILogger> Legacy, Action<ILogger> Typed)[] cases =
        [
            (logger => logger.LogDebug("Connected to {Host}:{Port}", endpoint.Host, endpoint.Port),
                logger => logger.ConnectionConnected(endpoint.Host, endpoint.Port)),
            (logger => logger.LogWarning(error, "Reconnect to {Host}:{Port} exhausted its {Attempts} attempts", endpoint.Host, endpoint.Port, 3),
                logger => logger.ReconnectExhausted(endpoint.Host, endpoint.Port, 3, error)),
            (logger => logger.LogWarning(new EventId(4001, "CredentialRefreshFailed"), "Credential renewal failed at {Stage} for {Host}:{Port}", "refresh", endpoint.Host, endpoint.Port),
                logger => logger.CredentialRefreshFailed("refresh", endpoint.Host, endpoint.Port)),
            (logger => logger.LogWarning(new EventId(4101, "CoordinationCleanupAbandoned"), "Background {Primitive} cleanup stopped at its {Stage} step ({Reason}); the owner may stay on Redis until it expires or is removed manually", "lock", "release", "timeout"),
                logger => logger.CoordinationCleanupAbandoned("lock", "release", "timeout")),
            (logger => logger.LogDebug("Redis Cluster slot {Slot} returned {Code} on the preferred role; {ReadFrom} read retries on the other role", 42, "READONLY", RespireReadFrom.PrimaryPreferred),
                logger => logger.ClusterReadRoleRetry(42, "READONLY", RespireReadFrom.PrimaryPreferred)),
            (logger => logger.LogWarning(error, "Cluster notification reconnect failed for {Host}:{Port}; retrying in {Delay}", endpoint.Host, endpoint.Port, delay),
                logger => logger.NotificationReconnectRetry(endpoint.Host, endpoint.Port, delay, error)),
            (logger => logger.LogInformation("Redis maintenance {Kind} ({SequenceId}) on {Host}:{Port}", "MOVING", 123L, endpoint.Host, endpoint.Port),
                logger => logger.MaintenanceNotificationReceived("MOVING", 123L, endpoint.Host, endpoint.Port)),
            (logger => logger.LogWarning(error, "Cluster notification route {Route} was rejected by {Host}:{Port}", channel.ToString(), endpoint.Host, endpoint.Port),
                logger => logger.NotificationRouteRejected(channel, endpoint.Host, endpoint.Port, error)),
            (logger => logger.LogDebug("Failed {Count} in-flight commands on {Host}:{Port}: {Reason}", 2, endpoint.Host, endpoint.Port, error.Message),
                logger => logger.ConnectionInflightFailed(2, endpoint.Host, endpoint.Port, error)),
            (logger => logger.LogWarning("Failover candidate {Endpoint} was marked unhealthy because it duplicates another candidate's deployment: {Reason}", endpoint.ToString(), "duplicate"),
                logger => logger.FailoverDuplicateDeployment(endpoint, "duplicate")),
            (logger => logger.LogInformation("Failover group switched from {PreviousEndpoint} to {CurrentEndpoint} ({Reason})", "none", endpoint.ToString(), "healthy"),
                logger => logger.FailoverEndpointSwitched(null, endpoint, "healthy")),
            (logger => logger.LogDebug("Sentinel peer discovery was unavailable: {Error}", reply.GetErrorMessage()),
                logger => logger.SentinelPeerDiscoveryUnavailable(in reply)),
            (logger => logger.LogWarning("Sentinel event monitoring did not stop within {Timeout}; {Count} task(s) still running", delay, 1),
                logger => logger.SentinelMonitorShutdownTimedOut(delay, tasks)),
        ];
        foreach (var (legacy, typed) in cases)
        {
            var expected = Capture(legacy);
            var actual = Capture(typed);
            await Assert.That(actual.Level).IsEqualTo(expected.Level);
            await Assert.That(actual.EventId.Id).IsEqualTo(expected.EventId.Id);
            await Assert.That(actual.EventId.Name).IsEqualTo(expected.EventId.Name);
            await Assert.That(ReferenceEquals(actual.Error, expected.Error)).IsTrue();
            await Assert.That(actual.Message).IsEqualTo(expected.Message);
            await Assert.That(actual.State).IsEquivalentTo(expected.State);
        }
    }

    [Test]
    [Arguments(LogLevel.Trace)]
    [Arguments(LogLevel.Debug)]
    [Arguments(LogLevel.Information)]
    [Arguments(LogLevel.Warning)]
    [Arguments(LogLevel.Error)]
    [Arguments(LogLevel.Critical)]
    [Arguments(LogLevel.None)]
    public async Task SentinelEventsPreserveDynamicLevelAndUnnamedEvent(LogLevel level)
    {
        var endpoint = new RespireEndpoint("sentinel.example", 26379);
        var expected = Capture(logger => logger.Log(level,
            "Sentinel {Channel} event for service {Service} from {Sentinel}: {Event}", "switch-master", "service", endpoint, "details"));
        var actual = Capture(logger => logger.SentinelEvent(level, "switch-master", "service", endpoint, "details"));
        await Assert.That(actual.Level).IsEqualTo(expected.Level);
        await Assert.That(actual.EventId.Id).IsEqualTo(0);
        await Assert.That(actual.EventId.Name).IsNull();
        await Assert.That(actual.Message).IsEqualTo(expected.Message);
        await Assert.That(actual.State).IsEquivalentTo(expected.State);
    }

    [Test]
    [NotInParallel] // AllocationMeasurement uses a process-wide no-GC boundary.
    public async Task DisabledLoggingAllocatesNothingAndSkipsFormatting()
    {
        using var factory = new ProbeFactory(enabled: false);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("localhost", 1)], LoggerFactory = factory,
        });
        var logger = factory.CreateLogger("Respire.Logging.Allocations");
        var error = new MessageProbeException();
        var endpoint = new RespireEndpoint("redis.example", 6379);
        RespireChannel channel = "notifications";
        using var reply = RespValue.Error("ERR peers unavailable");
        Task[] tasks = [new TaskCompletionSource().Task];
        var hub = client.Core.Hub;
        _ = MeasureDisabledLogging(logger, hub, error, endpoint, channel, tasks, in reply, control: false, iterations: 100);
        _ = MeasureDisabledLogging(logger, hub, error, endpoint, channel, tasks, in reply, control: true, iterations: 100);
        factory.LogCalls = 0;
        var (allocated, control) = AllocationMeasurement.WithoutConcurrentGc(() => (
            MeasureDisabledLogging(logger, hub, error, endpoint, channel, tasks, in reply, control: false, iterations: 1000),
            MeasureDisabledLogging(logger, hub, error, endpoint, channel, tasks, in reply, control: true, iterations: 1000)));
        await Assert.That(allocated).IsEqualTo(0);
        await Assert.That(control).IsGreaterThanOrEqualTo(1000 * 24);
        await Assert.That(factory.LogCalls).IsEqualTo(1000); // Only the allocating legacy positive control calls Log.
        await Assert.That(error.MessageReads).IsEqualTo(0);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDisabledLogging(ILogger logger, SubscriptionHub hub, MessageProbeException error,
        RespireEndpoint endpoint, RespireChannel channel, Task[] tasks, in RespValue reply, bool control, int iterations)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            if (control)
            {
                logger.LogWarning(error, "Reconnect to {Host}:{Port} exhausted its {Attempts} attempts", endpoint.Host, endpoint.Port, i);
                continue;
            }
            logger.ReconnectExhausted(endpoint.Host, endpoint.Port, i, error);
            logger.ClusterReadRoleRetry(i, "READONLY", RespireReadFrom.PrimaryPreferred);
            logger.MovingPublishedLate(endpoint.Host, endpoint.Port, i);
            logger.SentinelEvent(LogLevel.Information, "switch-master", "service", endpoint, "details");
            logger.NotificationReconnectRetry(endpoint.Host, endpoint.Port, TimeSpan.Zero, error);
            logger.NotificationRouteRejected(channel, endpoint.Host, endpoint.Port, error);
            logger.FailoverDuplicateDeployment(endpoint, "duplicate");
            logger.FailoverEndpointSwitched(null, endpoint, "healthy");
            logger.SentinelMonitorShutdownTimedOut(TimeSpan.Zero, tasks);
            logger.SentinelPeerDiscoveryUnavailable(in reply);
            logger.ConnectionInflightFailed(i, endpoint.Host, endpoint.Port, error);
            hub.LogGapObserverFailure(error);
            logger.TryLog((error, i), static (logger, state)
                => logger.SubscriptionRecoveryAttemptFailed(state.i, state.error));
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PubSubKeepsOrdinaryProviderFailuresInsideRecoveryBoundary(bool fromIsEnabled)
    {
        using var factory = new ProbeFactory(providerError: new InvalidOperationException("provider failure"), fromIsEnabled: fromIsEnabled);
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("localhost", 1)], LoggerFactory = factory });
        client.Core.Hub.LogGapObserverFailure(new InvalidOperationException("observer failure"));
        client.Core.Hub.LogGapObserverFailure(new InvalidOperationException("later observer failure"));
        await Assert.That(factory.ProviderFailures).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PubSubStillPropagatesFatalProviderFailures(bool fromIsEnabled)
    {
        using var factory = new ProbeFactory(providerError: new OutOfMemoryException("simulated fatal provider failure"), fromIsEnabled: fromIsEnabled);
        await using var client = RespireClient.Create(new RespireOptions { Endpoints = [new("localhost", 1)], LoggerFactory = factory });
        await Assert.That(() => client.Core.Hub.LogGapObserverFailure(new Exception("observer failure")))
            .ThrowsExactly<OutOfMemoryException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TopologyLoggerKeepsItsCatchAllProviderBoundary(bool fromIsEnabled)
    {
        using var factory = new ProbeFactory(providerError: new OutOfMemoryException("simulated provider failure"), fromIsEnabled: fromIsEnabled);
        var logger = factory.CreateLogger("Respire.Cluster");
        logger.TryLog(new Exception("refresh failure"), static (logger, error) => logger.ClusterTopologyRefreshFailed(error));
        logger.TryLog(new Exception("later refresh failure"), static (logger, error) => logger.ClusterTopologyWorkerFailed(error));
        await Assert.That(factory.ProviderFailures).IsEqualTo(2);
        ILogger? missing = null;
        missing.TryLog(0, static (_, _) => throw new InvalidOperationException("Null logger must not invoke the callback."));
    }

    private static Entry Capture(Action<ILogger> log)
    {
        using var factory = new ProbeFactory(capture: true);
        log(factory.CreateLogger("Respire.Lifecycle"));
        return factory.Entry ?? throw new InvalidOperationException("The enabled logger did not receive an event.");
    }

    private sealed record Entry(LogLevel Level, EventId EventId, Exception? Error, string Message,
        KeyValuePair<string, object?>[] State);

    private sealed class MessageProbeException : Exception
    {
        internal int MessageReads;
        public override string Message { get { MessageReads++; return "computed diagnostic message"; } }
    }

    private sealed class ProbeFactory(bool enabled = true, bool capture = false, Exception? providerError = null,
        bool fromIsEnabled = false) : ILoggerFactory
    {
        private readonly bool _enabled = enabled;
        private readonly bool _capture = capture;
        private readonly Exception? _providerError = providerError;
        private readonly bool _fromIsEnabled = fromIsEnabled;
        internal int LogCalls;
        internal int ProviderFailures;
        internal Entry? Entry;
        public ILogger CreateLogger(string categoryName) => new ProbeLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class ProbeLogger(ProbeFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level)
            {
                if (owner._fromIsEnabled && owner._providerError is { } error)
                {
                    owner.ProviderFailures++;
                    throw error;
                }
                return owner._enabled;
            }
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                owner.LogCalls++;
                if (owner._providerError is { } error)
                {
                    owner.ProviderFailures++;
                    throw error;
                }
                if (owner._capture)
                    owner.Entry = new(level, eventId, exception, formatter(state, exception),
                        ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray());
            }
        }
    }
}
