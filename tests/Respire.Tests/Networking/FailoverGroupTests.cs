using System.Text;
using Microsoft.Extensions.Logging;
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
    public async Task SentinelCandidateRediscoversWhenCurrentPrimaryIsDemoted()
    {
        var primaryPort = 0;
        var demoted = 0;
        await using var oldPrimary = new FakeRespServer(RoleReply("master"), FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? RoleReply(Volatile.Read(ref demoted) == 0 ? "master" : "slave") : null,
        };
        await using var newPrimary = new FakeRespServer(RoleReply("master"), FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        Volatile.Write(ref primaryPort, oldPrimary.Port);
        await using var staleSentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(Volatile.Read(ref primaryPort)),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var sentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(Volatile.Read(ref primaryPort)),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [SentinelCandidateWithSeeds([staleSentinel, sentinel])], FastOptions() with
            {
                ProbeInterval = TimeSpan.FromMilliseconds(30),
                ProbeTimeout = TimeSpan.FromSeconds(2),
                // A probe failure would keep the only candidate unavailable for the rest of the test.
                CircuitOpenDuration = TimeSpan.FromMinutes(1),
            });
        Volatile.Write(ref primaryPort, newPrimary.Port);
        Volatile.Write(ref demoted, 1);

        await WaitUntilAsync(() => group.IsConnected && group.ActiveClient.Endpoint == Endpoint(newPrimary)
            && newPrimary.ReceivedCommands.Contains("PING"));
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(newPrimary));
        // Rediscovering a validated replacement within the probe is a healthy handoff, not a failure.
        var status = group.GetEndpointStatuses().Single();
        await Assert.That(status.IsHealthy).IsTrue();
        await Assert.That(status.ConsecutiveFailures).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelCandidatesRejectDuplicateNormalizedSeedSets()
    {
        await using var unused = new FakeRespServer(FakeRespServer.PongReply);
        var duplicateSeeds = new RespireFailoverCandidate(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", unused.Port), new("127.0.0.1", unused.Port)],
            SentinelPrimaryName = "mymaster",
        });
        var singleSeed = new RespireFailoverCandidate(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Endpoints = [new("127.0.0.1", unused.Port)],
            SentinelPrimaryName = "mymaster",
        });

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([duplicateSeeds, singleSeed]))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(unused.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelCandidatesRejectOverlappingSeedSetsForSameService()
    {
        await using var sharedSeed = new FakeRespServer(FakeRespServer.PongReply);
        await using var additionalSeed = new FakeRespServer(FakeRespServer.PongReply);
        var first = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sharedSeed.Port)],
            SentinelPrimaryName = "mymaster",
        });
        var second = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sharedSeed.Port), new("127.0.0.1", additionalSeed.Port)],
            SentinelPrimaryName = "mymaster",
        });

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([first, second]))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(sharedSeed.CommandsSeen).IsEqualTo(0);
        await Assert.That(additionalSeed.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task SentinelCandidatesRejectDisjointSeedsThatDiscoverSameDeployment()
    {
        await using var firstPrimary = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        await using var secondPrimary = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        FakeRespServer? first = null;
        FakeRespServer? second = null;
        await using var firstSentinel = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(firstPrimary.Port),
                "SENTINEL SENTINELS mymaster" => PeerReply(second!.Port),
                _ => null,
            },
        };
        first = firstSentinel;
        await using var secondSentinel = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(secondPrimary.Port),
                "SENTINEL SENTINELS mymaster" => PeerReply(first!.Port),
                _ => null,
            },
        };
        second = secondSentinel;

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
            [SentinelCandidate(firstSentinel), SentinelCandidate(secondSentinel, priority: 1)],
            FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(2) }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task LearnedSentinelPeerCannotBeSelectedAsDataEndpoint()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        await using var dataEndpoint = new FakeRespServer(FakeRespServer.PongReply);
        await using var sentinel = new FakeRespServer(FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(primary.Port),
                "SENTINEL SENTINELS mymaster" => PeerReply(dataEndpoint.Port),
                _ => null,
            },
        };

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
            [SentinelCandidate(sentinel), Candidate(dataEndpoint, priority: 1)],
            FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(2) }))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task UndiscoveredSentinelStatusHasNoDataEndpoint()
    {
        await using var invalidSentinel = new FakeRespServer("-ERR unavailable\r\n"u8.ToArray());
        await using var healthy = new FakeRespServer(FakeRespServer.PongReply);
        await using var group = await RespireFailoverGroup.ConnectAsync(
        [SentinelCandidate(invalidSentinel, priority: 0), Candidate(healthy, priority: 1)], FastOptions());

        await Assert.That(group.GetEndpointStatuses().Single(status => status.Priority == 0).Endpoint).IsNull();
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(healthy));
    }

    [Test]
    [Arguments(false, false, true, true)]
    [Arguments(false, false, false, false)]
    [Arguments(false, true, true, true)]
    [Arguments(false, true, false, true)]
    [Arguments(true, false, true, false)]
    [Arguments(true, false, false, false)]
    [Arguments(true, true, true, true)]
    [Arguments(true, true, false, false)]
    public async Task DeploymentConflictFailsUnhealthyOrLowerPrecedenceCandidate(
        bool candidateIsHealthy, bool otherIsHealthy, bool otherPrecedes, bool expected)
    {
        await Assert.That(RespireFailoverGroup.ShouldFailCandidateForDeploymentConflict(
            candidateIsHealthy, otherIsHealthy, otherPrecedes)).IsEqualTo(expected);
    }

    [Test]
    public async Task RecoveredSentinelCandidateDuplicatingHealthyDeploymentStaysUnhealthy()
    {
        var recovered = 0;
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        await using var firstSentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(primary.Port),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var secondSentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Volatile.Read(ref recovered) == 0
                    ? "-ERR unavailable\r\n"u8.ToArray() : PrimaryReply(primary.Port),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [SentinelCandidate(firstSentinel), SentinelCandidate(secondSentinel, priority: 1)],
            FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(2) });
        await Assert.That(group.GetEndpointStatuses().Single(status => status.Priority == 1).IsHealthy).IsFalse();

        // The second deployment recovers and discovers the primary the first candidate already serves.
        Volatile.Write(ref recovered, 1);
        await WaitUntilAsync(() => group.GetEndpointStatuses().Single(status => status.Priority == 1).LastErrorType
            == nameof(RespireConfigurationException));

        var statuses = group.GetEndpointStatuses();
        await Assert.That(statuses.Single(status => status.Priority == 0).IsHealthy).IsTrue();
        await Assert.That(statuses.Single(status => status.Priority == 1).IsHealthy).IsFalse();
    }

    [Test]
    public async Task ConnectAsync_RejectsStandaloneEndpointThatIsSentinelPrimaryInEitherOrder()
    {
        await using var primary = new FakeRespServer(16, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? RoleReply("master") : null,
        };
        await using var sentinel = new FakeRespServer(16, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => PrimaryReply(primary.Port),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };

        foreach (var (sentinelPriority, standalonePriority) in new[] { (0, 1), (1, 0) })
        {
            await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
                    [SentinelCandidate(sentinel, sentinelPriority), Candidate(primary, standalonePriority)],
                    FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(2) }))
                .ThrowsExactly<RespireConfigurationException>();
        }
    }

    [Test]
    public async Task SwitchFromSentinelCandidateReportsPrimaryObservedAtSelection()
    {
        var demoted = 0;
        await using var primary = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command == "ROLE"
                ? RoleReply(Volatile.Read(ref demoted) == 0 ? "master" : "slave") : null,
        };
        await using var sentinel = new FakeRespServer(8, FakeRespServer.PongReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Volatile.Read(ref demoted) == 0
                    ? PrimaryReply(primary.Port) : "-ERR unavailable\r\n"u8.ToArray(),
                "SENTINEL SENTINELS mymaster" => "*0\r\n"u8.ToArray(),
                _ => null,
            },
        };
        await using var standby = new FakeRespServer(FakeRespServer.PongReply);
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [SentinelCandidate(sentinel), Candidate(standby, priority: 1)],
            FastOptions() with { ProbeTimeout = TimeSpan.FromSeconds(2) });
        var switched = new TaskCompletionSource<RespireFailoverSwitch>(TaskCreationOptions.RunContinuationsAsynchronously);
        group.EndpointSwitched += change => switched.TrySetResult(change);

        // The demoted primary retires the Sentinel generation, so the candidate's live endpoint becomes null.
        Volatile.Write(ref demoted, 1);
        var change = await switched.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(change.PreviousEndpoint).IsEqualTo(Endpoint(primary));
        await Assert.That(change.CurrentEndpoint).IsEqualTo(Endpoint(standby));
        await Assert.That(change.Reason).IsEqualTo(RespireFailoverSwitchReasons.ActiveEndpointUnhealthy);
    }

    [Test]
    public async Task ConsecutiveProbeFailuresOpenCircuitAndSwitchNewOperations()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        var failedProbes = 0;
        var secondProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.SuppressReply = command =>
        {
            if (command != "PING" || Volatile.Read(ref primaryFailed) == 0) return false;
            if (Interlocked.Increment(ref failedProbes) != 2) return false;
            secondProbe.TrySetResult();
            return true;
        };
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        var primaryCandidate = Candidate(primary, priority: 0);
        primaryCandidate = primaryCandidate with { Options = primaryCandidate.Options with { CommandTimeout = TimeSpan.FromSeconds(10) } };
        await using var group = await RespireFailoverGroup.ConnectAsync(
            [primaryCandidate, Candidate(secondary, priority: 1)],
            FastOptions(failureThreshold: 2) with { ProbeTimeout = TimeSpan.FromSeconds(10) });
        var originalClient = group.ActiveClient;
        Volatile.Write(ref primaryFailed, 1);

        // Hold the second failure response until the one-failure state has been inspected.
        await secondProbe.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(primary));
        await Assert.That(group.GetEndpointStatuses().Single(status => status.Endpoint == Endpoint(primary)).ConsecutiveFailures)
            .IsEqualTo(1);
        await primary.SendRawAsync("-ERR primary unavailable\r\n"u8.ToArray(), primary.ReceivedConnectionIds[^1]);

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
    public async Task RecoveredEarlierCandidateWithEqualPriorityFailsBack()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;

        await using var group = await RespireFailoverGroup.ConnectAsync(
            [Candidate(primary, priority: 0), Candidate(secondary, priority: 0)], FastOptions());
        var reasons = new System.Collections.Concurrent.ConcurrentQueue<string>();
        group.EndpointSwitched += change => reasons.Enqueue(change.Reason);
        Volatile.Write(ref primaryFailed, 1);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(secondary));

        Volatile.Write(ref primaryFailed, 0);
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(primary));
        await WaitUntilAsync(() => reasons.Count >= 2);
        await Assert.That(reasons.ToArray()).IsEquivalentTo(new[]
        {
            RespireFailoverSwitchReasons.ActiveEndpointUnhealthy,
            RespireFailoverSwitchReasons.EarlierEqualPriorityEndpointRecovered,
        });
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
            RespireFailoverSwitchReasons.RecoveredFromNoHealthyEndpoint,
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
    public async Task ThrowingSwitchHandlerDoesNotStopOtherHandlersOrMonitoring()
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
        group.EndpointSwitched += _ => throw new InvalidOperationException("handler failure");
        group.EndpointSwitched += change => reasons.Enqueue(change.Reason);

        Volatile.Write(ref primaryFailed, 1);
        await WaitUntilAsync(() => reasons.Count >= 1);
        Volatile.Write(ref primaryFailed, 0);
        await WaitUntilAsync(() => reasons.Count >= 2);

        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(primary));
        await Assert.That(reasons.ToArray()).IsEquivalentTo(new[]
        {
            RespireFailoverSwitchReasons.ActiveEndpointUnhealthy,
            RespireFailoverSwitchReasons.HigherPriorityEndpointRecovered,
        });
    }

    [Test]
    public async Task ThrowingSwitchHandlerIsLoggedThroughCandidateLoggerFactory()
    {
        await using var primary = new FakeRespServer(FakeRespServer.PongReply);
        await using var secondary = new FakeRespServer(FakeRespServer.PongReply);
        var primaryFailed = 0;
        primary.ReplyOverride = (_, command) =>
            command == "PING" && Volatile.Read(ref primaryFailed) != 0
                ? "-ERR primary unavailable\r\n"u8.ToArray()
                : null;
        var logger = new CapturingLogger();
        var first = Candidate(primary, priority: 0);

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [first with { Options = first.Options with { LoggerFactory = logger } }, Candidate(secondary, priority: 1)],
            FastOptions());
        group.EndpointSwitched += _ => throw new InvalidOperationException("handler failure");

        Volatile.Write(ref primaryFailed, 1);
        await WaitUntilAsync(() => logger.Warnings.Any(entry => entry.Error is InvalidOperationException));

        var warning = logger.Warnings.First(entry => entry.Error is InvalidOperationException);
        await Assert.That(warning.Category).IsEqualTo("Respire.FailoverGroup");
        await Assert.That(warning.Message).Contains("EndpointSwitched handler threw");
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
    public async Task ConnectAsync_ProbesClusterCandidatesAndRejectsClientSideCache()
    {
        var cluster = new RespireFailoverCandidate(new RespireOptions
        {
            UseCluster = true,
            Endpoints = ["127.0.0.1:1"],
        });
        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([cluster]))
            .ThrowsExactly<RespireConnectionException>();

        var cached = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = ["localhost:6379"],
            ClientSideCache = new RespireClientSideCacheOptions(),
        });
        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([cached]))
            .ThrowsExactly<RespireConfigurationException>();
    }

    [Test]
    public async Task ClusterCandidateUsesClusterStateAndReportsFirstSeed()
    {
        await using var cluster = new FakeRespServer(FakeRespServer.PongReply);
        await using var standby = new FakeRespServer(FakeRespServer.PongReply);
        var clusterState = "ok";
        cluster.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => ClusterSlots(cluster.Port),
            "CLUSTER INFO" => Bulk($"cluster_state:{Volatile.Read(ref clusterState)}\r\ncluster_slots_assigned:16384\r\n"),
            _ => null,
        };
        var unusedSeed = new RespireEndpoint("127.0.0.1", 1);

        await using var group = await RespireFailoverGroup.ConnectAsync(
        [
            new RespireFailoverCandidate(new RespireOptions
            {
                UseCluster = true,
                Protocol = RespProtocol.Resp2,
                Connections = 1,
                ConnectTimeout = TimeSpan.FromMilliseconds(200),
                CommandTimeout = TimeSpan.FromMilliseconds(300),
                Endpoints = [Endpoint(cluster), unusedSeed],
            }, Priority: 0),
            Candidate(standby, priority: 1),
        ], FastOptions());

        await Assert.That(group.ActiveClient.Endpoint).IsEqualTo(Endpoint(cluster));
        await Assert.That(group.GetEndpointStatuses()[0].Endpoint).IsEqualTo(Endpoint(cluster));
        await Assert.That(cluster.ReceivedCommands.Contains("CLUSTER INFO")).IsTrue();
        await Assert.That(cluster.ReceivedCommands.Contains("PING")).IsFalse();

        // The node still answers, but Redis reports unserved slots.
        Volatile.Write(ref clusterState, "fail");
        await WaitUntilAsync(() => group.ActiveClient.Endpoint == Endpoint(standby));
        var status = group.GetEndpointStatuses().Single(candidate => candidate.Endpoint == Endpoint(cluster));
        await Assert.That(status.IsHealthy).IsFalse();
        await Assert.That(status.LastErrorType).IsEqualTo(nameof(RespireConnectionException));
    }

    [Test]
    public async Task ConnectAsync_RejectsInvalidCandidateModesBeforeConnecting()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var endpoint = Endpoint(server);

        var sentinel = await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
            [new RespireFailoverCandidate(new RespireOptions { UseCluster = true, Endpoints = [endpoint], SentinelPrimaryName = "mymaster" })]))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(sentinel!.Message).Contains("Sentinel");

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
            [new RespireFailoverCandidate(new RespireOptions { Endpoints = [endpoint, new RespireEndpoint("127.0.0.1", 1)] })]))
            .ThrowsExactly<RespireConfigurationException>();

        var overlapping = await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(
        [
            new RespireFailoverCandidate(new RespireOptions { UseCluster = true, Endpoints = [new RespireEndpoint("127.0.0.1", 1), endpoint] }),
            new RespireFailoverCandidate(new RespireOptions { UseCluster = true, Endpoints = [endpoint] }),
        ])).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(overlapping!.Message).Contains(endpoint.ToString());
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ConnectAsync_RejectsSentinelSeedsReusedAsDataEndpointsInEitherOrder()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var endpoint = Endpoint(server);
        var standalone = new RespireFailoverCandidate(new RespireOptions { Endpoints = [endpoint] });
        var sentinel = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = [endpoint],
            SentinelPrimaryName = "mymaster",
        });

        foreach (var candidates in new[] { new[] { standalone, sentinel }, new[] { sentinel, standalone } })
        {
            await Assert.That(async () => await RespireFailoverGroup.ConnectAsync(candidates))
                .ThrowsExactly<RespireConfigurationException>();
        }

        await Assert.That(server.CommandsSeen).IsEqualTo(0);
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
    public async Task ConnectAsync_RejectsDuplicateEndpointsIgnoringHostCase()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        var primary = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = [$"LOCALHOST:{server.Port}"],
        });
        var duplicate = new RespireFailoverCandidate(new RespireOptions
        {
            Endpoints = [$"localhost:{server.Port}"],
        });

        await Assert.That(async () => await RespireFailoverGroup.ConnectAsync([primary, duplicate]))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(server.CommandsSeen).IsEqualTo(0);
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
        server.DelayReply(replyIndex: 1, milliseconds: 200);
        var group = await RespireFailoverGroup.ConnectAsync([Candidate(server, priority: 0)], FastOptions());
        await WaitUntilAsync(() => server.CommandsSeen >= 2);
        await group.DisposeAsync();
        var commandCount = server.CommandsSeen;

        // The server reports the peer closed only after it finishes the delayed reply and reads every byte
        // the client sent, so a queued post-disposal probe would already be counted.
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));

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
            ConnectTimeout = TimeSpan.FromSeconds(2),
            CommandTimeout = TimeSpan.FromSeconds(2),
            Endpoints = [new RespireEndpoint("127.0.0.1", server.Port)],
        }, priority);

    private static RespireFailoverCandidate SentinelCandidate(FakeRespServer sentinel, int priority = 0)
        => SentinelCandidateWithSeeds([sentinel], priority);

    private static RespireFailoverCandidate SentinelCandidateWithSeeds(FakeRespServer[] sentinels, int priority = 0)
        => new(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ConnectTimeout = TimeSpan.FromMilliseconds(200),
            CommandTimeout = TimeSpan.FromMilliseconds(300),
            Endpoints = sentinels.Select(sentinel => new RespireEndpoint("127.0.0.1", sentinel.Port)).ToArray(),
            SentinelPrimaryName = "mymaster",
        }, priority);

    private static byte[] RoleReply(string role)
        => Encoding.ASCII.GetBytes($"*3\r\n${role.Length}\r\n{role}\r\n:0\r\n*0\r\n");

    private static byte[] PrimaryReply(int port)
        => Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static byte[] PeerReply(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*4\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${port.ToString().Length}\r\n{port}\r\n");

    private static RespireFailoverGroupOptions FastOptions(int failureThreshold = 1)
        => new()
        {
            ProbeInterval = TimeSpan.FromMilliseconds(15),
            // Tests inject failure replies explicitly. Keep monitoring fast without treating
            // a busy CI scheduler as another failed endpoint or an extra switch event.
            ProbeTimeout = TimeSpan.FromSeconds(2),
            FailureThreshold = failureThreshold,
            CircuitOpenDuration = TimeSpan.FromMilliseconds(100),
            FailbackGracePeriod = TimeSpan.FromMilliseconds(20),
        };

    private static RespireEndpoint Endpoint(FakeRespServer server) => new("127.0.0.1", server.Port);

    private static byte[] Bulk(string value)
    {
        var payload = Encoding.UTF8.GetBytes(value);
        return [.. Encoding.ASCII.GetBytes($"${payload.Length}\r\n"), .. payload, 13, 10];
    }

    private static byte[] ClusterSlots(int port)
        => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class CapturingLogger : ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<(string Category, string Message, Exception? Error)> Warnings { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CategoryLogger(this, categoryName);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

        private sealed class CategoryLogger(CapturingLogger owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == LogLevel.Warning) owner.Warnings.Enqueue((category, formatter(state, exception), exception));
            }
        }
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
