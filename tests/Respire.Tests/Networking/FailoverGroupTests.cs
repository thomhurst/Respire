using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class FailoverGroupTests
{
    [Test]
    public async Task ConnectAsync_SelectsHighestPriorityHealthyCandidate()
    {
        await using var lower = new FakeRespServer(FakeRespServer.PongReply);
        await using var higher = new FakeRespServer(FakeRespServer.PongReply);
        await using var group = await RespireFailoverGroup.ConnectAsync(
        [
            Candidate(higher, priority: 10),
            Candidate(lower, priority: 1),
        ], FastOptions());

        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(lower));
        await Assert.That(group.IsConnected).IsTrue();
        await Assert.That(group.GetEndpointStatuses().Count).IsEqualTo(2);
    }

    [Test]
    public async Task ConsecutiveProbeFailuresOpenCircuitAndSwitchNewOperations()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)],
            FastOptions(failureThreshold: 2) with { ProbeInterval = TimeSpan.FromMilliseconds(150) });
        var originalClient = group.ActiveClient;
        var initialProbeCount = primary.CommandsSeen;
        Volatile.Write(ref primaryFailed, 1);

        await WaitUntilAsync(() => primary.CommandsSeen >= initialProbeCount + 1);
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(primary));

        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));
        var primaryStatus = group.GetEndpointStatuses().Single(status => status.Endpoint == Endpoint(primary));
        await Assert.That(primaryStatus.IsHealthy).IsFalse();
        await Assert.That(primaryStatus.ConsecutiveFailures).IsGreaterThanOrEqualTo(2);
        await Assert.That(primaryStatus.CircuitOpenUntil).IsNotNull();
        await Assert.That(originalClient.Endpoint).IsEqualTo(Endpoint(primary));

        await group.ActiveClient.PingAsync();
        await WaitUntilAsync(() => secondary.ReceivedCommands.Count(command => command == "PING") >= 2);

        await originalClient.ExecuteFireAndForgetAsync("SET", "still-primary", "value");
        await WaitUntilAsync(() => primary.ReceivedCommands.Contains("SET still-primary value"));
        await group.ActiveClient.ExecuteFireAndForgetAsync("SET", "selected-secondary", "value");
        await WaitUntilAsync(() => secondary.ReceivedCommands.Contains("SET selected-secondary value"));
        await Assert.That(secondary.ReceivedCommands.Contains("SET still-primary value")).IsFalse();
    }

    [Test]
    public async Task RecoveredHigherPriorityCandidateFailsBackAfterGracePeriod()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)],
            FastOptions(failureThreshold: 1) with
            {
                CircuitOpenDuration = TimeSpan.FromMilliseconds(40),
                FailbackGracePeriod = TimeSpan.FromMilliseconds(30),
            });
        Volatile.Write(ref primaryFailed, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));

        Volatile.Write(ref primaryFailed, 0);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(primary));
    }

    [Test]
    public async Task FailedProbeBelowThresholdRestartsFailbackGracePeriod()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        // 0 = healthy, 1 = always fail, 2 = alternate failures and successes.
        var primaryMode = 0;
        var primaryPings = 0;
        primary.ReplyOverride = (_, command) =>
        {
            if (command != "PING") return null;
            var mode = Volatile.Read(ref primaryMode);
            var fail = mode == 1 || (mode == 2 && Interlocked.Increment(ref primaryPings) % 2 == 0);
            return fail ? "-ERR primary unavailable\r\n"u8.ToArray() : null;
        };

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)],
            FastOptions(failureThreshold: 2) with
            {
                CircuitOpenDuration = TimeSpan.FromMilliseconds(40),
                FailbackGracePeriod = TimeSpan.FromMilliseconds(400),
            });
        Volatile.Write(ref primaryMode, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));

        // Alternating results never open the circuit, but each failure must restart the grace period.
        Volatile.Write(ref primaryMode, 2);
        await WaitUntilAsync(() => group.GetEndpointStatuses()
            .Single(status => status.Endpoint == Endpoint(primary)).IsHealthy);
        await Task.Delay(800);
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(secondary));

        Volatile.Write(ref primaryMode, 0);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(primary));
    }

    [Test]
    public async Task UnstableTopCandidateDoesNotBlockFailbackToStableIntermediateCandidate()
    {
        await using var top = new FakeRespServer(FakeRespServer.PongReply);
        await using var middle = new FakeRespServer(FakeRespServer.PongReply);
        await using var bottom = new FakeRespServer(FakeRespServer.PongReply);
        // 0 = healthy, 1 = always fail, 2 = alternate failures and successes.
        var topMode = 0;
        var topPings = 0;
        top.ReplyOverride = (_, command) =>
        {
            if (command != "PING") return null;
            var mode = Volatile.Read(ref topMode);
            var fail = mode == 1 || (mode == 2 && Interlocked.Increment(ref topPings) % 2 == 0);
            return fail ? "-ERR top unavailable\r\n"u8.ToArray() : null;
        };
        var middleFailed = 0;
        middle.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref middleFailed) != 0
                ? "-ERR middle unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(top, priority: 0), Candidate(middle, priority: 1), Candidate(bottom, priority: 2)],
            FastOptions(failureThreshold: 2) with
            {
                CircuitOpenDuration = TimeSpan.FromMilliseconds(40),
                FailbackGracePeriod = TimeSpan.FromMilliseconds(300),
            });
        Volatile.Write(ref topMode, 1);
        Volatile.Write(ref middleFailed, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(bottom));

        // The top candidate keeps reporting healthy but never completes its grace period.
        Volatile.Write(ref topMode, 2);
        Volatile.Write(ref middleFailed, 0);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(middle));

        Volatile.Write(ref topMode, 0);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(top));
    }

    [Test]
    public async Task AllCandidatesDownThenRecoveryReselectsEndpoint()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var failed = 0;
        server.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref failed) != 0
                ? "-ERR unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(server, priority: 0)],
            FastOptions() with { CircuitOpenDuration = TimeSpan.FromMilliseconds(40) });
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
        group.EndpointSwitched += change => reasons.Enqueue(change.Reason);

        Volatile.Write(ref failed, 1);
        await WaitUntilAsync(() => !group.IsConnected);
        await Assert.That(() => group.ActiveClient).ThrowsExactly<RespireConnectionException>();

        Volatile.Write(ref failed, 0);
        await WaitUntilAsync(() => reasons.Count >= 2);
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(server));
        await Assert.That(reasons.ToArray()).IsEquivalentTo(new[]
        {
            RespireFailoverSwitchReasons.NoHealthyEndpoint,
            RespireFailoverSwitchReasons.FirstHealthy,
        });
    }

    [Test]
    public async Task MaximumCircuitOpenDurationSaturatesInsteadOfBlockingFailover()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)],
            FastOptions() with { CircuitOpenDuration = TimeSpan.MaxValue });
        Volatile.Write(ref primaryFailed, 1);

        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));
        var primaryStatus = group.GetEndpointStatuses().Single(status => status.Endpoint == Endpoint(primary));
        await Assert.That(primaryStatus.CircuitOpenUntil).IsEqualTo(DateTimeOffset.MaxValue);
    }

    [Test]
    public async Task FailbackGraceUsesMonotonicTimeAcrossWallClockCorrections()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryUnavailable = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryUnavailable) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var options = FastOptions() with { FailbackGracePeriod = TimeSpan.FromSeconds(5) };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)], options, clock);
        Volatile.Write(ref primaryUnavailable, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));

        Volatile.Write(ref primaryUnavailable, 0);
        var commandsBeforeWallClockJump = primary.CommandsSeen;
        clock.AdvanceUtc(TimeSpan.FromDays(365));
        await Task.Delay(50);
        await Assert.That(primary.CommandsSeen).IsEqualTo(commandsBeforeWallClockJump);

        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitUntilAsync(() => group.GetEndpointStatuses().Single(status => status.Endpoint == Endpoint(primary)).IsHealthy);
        clock.AdvanceUtc(TimeSpan.FromDays(365));
        await Task.Delay(50);
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(secondary));

        clock.Advance(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(primary));
    }

    [Test]
    public async Task EndpointSwitchedReportsDocumentedReasons()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [Candidate(primary, priority: 0), Candidate(secondary, priority: 1)], FastOptions());
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
        group.EndpointSwitched += change => reasons.Enqueue(change.Reason);

        Volatile.Write(ref primaryFailed, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));
        Volatile.Write(ref primaryFailed, 0);
        // Handlers run after the selection is published, so wait for the event rather than the endpoint.
        await WaitUntilAsync(() => reasons.Count >= 2);

        await Assert.That(reasons.ToArray()).IsEquivalentTo(new[]
        {
            RespireFailoverSwitchReasons.ActiveEndpointUnhealthy,
            RespireFailoverSwitchReasons.HigherPriorityEndpointRecovered,
        });
    }

    [Test]
    public async Task ConnectAsync_RejectsProbeTimeoutBeyondTimerLimit()
    {
        var options = FastOptions() with { ProbeTimeout = TimeSpan.FromDays(50) };

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
                [new RespireFailoverCandidate(new RespireOptions { Endpoints = ["127.0.0.1:1"] })], options))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConcurrentDisposeAsyncCallsWaitForCandidateDisposal()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var group = await RespireFailoverGroup.ConnectAsync([Candidate(server, priority: 0)], FastOptions());
        var client = group.ActiveClient;

        var first = group.DisposeAsync().AsTask();
        await group.DisposeAsync();

        await Assert.That(client.IsConnected).IsFalse();
        await first;
    }

    [Test]
    public async Task ConnectAsync_RejectsClusterAndClientSideCacheCandidates()
    {
        var cluster = new RespireFailoverCandidate(new RespireOptions
        {
            UseCluster = true,
            Endpoints = ["localhost:6379"],
        });
        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([cluster]))
            .ThrowsExactly<RespireConfigurationException>();

        var cached = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = ["localhost:6379"],
            ClientSideCache = new RespireClientSideCacheOptions(),
        });
        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([cached]))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task ConnectAsync_RejectsFiniteReconnectBudget()
    {
        var candidate = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = ["127.0.0.1:1"],
            ReconnectPolicy = new RespireReconnectPolicy { MaxAttempts = 3 },
        });

        var exception = await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([candidate]))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(exception!.Message).Contains("MaxAttempts = null");
    }

    [Test]
    public async Task ConnectAsync_RejectsProbeIntervalBeyondTimerLimit()
    {
        var options = FastOptions() with { ProbeInterval = TimeSpan.FromDays(50) };

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
                [new RespireFailoverCandidate(new RespireOptions { Endpoints = ["127.0.0.1:1"] })], options))
            .ThrowsExactly<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task DisposeAsync_StopsProbesAndRejectsActiveClientAccess()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var group = await RespireFailoverGroup.ConnectAsync([Candidate(server, priority: 0)], FastOptions());
        await group.DisposeAsync();
        var commandCount = server.CommandsSeen;

        await Task.Delay(60);

        await Assert.That(group.IsConnected).IsFalse();
        await Assert.That(server.CommandsSeen).IsEqualTo(commandCount);
        await Assert.That(() => group.ActiveClient).ThrowsExactly<ObjectDisposedException>();
        await group.DisposeAsync();
    }

    [Test]
    public async Task ConnectAsync_CancellationStopsInitialHealthProbe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
                [new RespireFailoverCandidate(new RespireOptions { Endpoints = ["127.0.0.1:1"] })],
                cancellationToken: cancellation.Token))
            .Throws<OperationCanceledException>();
    }

    private static RespireFailoverCandidate Candidate(FakeRespServer server, int priority)
        => new(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            Endpoints = [new RespireEndpoint("127.0.0.1", server.Port)],
        }, priority);

    private static RespireFailoverGroupOptions FastOptions(int failureThreshold = 1)
        => new()
        {
            ProbeInterval = TimeSpan.FromMilliseconds(15),
            ProbeTimeout = TimeSpan.FromMilliseconds(200),
            FailureThreshold = failureThreshold,
            CircuitOpenDuration = TimeSpan.FromMilliseconds(100),
            FailbackGracePeriod = TimeSpan.FromMilliseconds(20),
        };

    private static RespireEndpoint Endpoint(FakeRespServer server) => new("127.0.0.1", server.Port);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class ManualClock(DateTimeOffset utcNow) : TimeProvider
    {
        private long _timestamp;
        private long _utcTicks = utcNow.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
        public void AdvanceUtc(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }
}
