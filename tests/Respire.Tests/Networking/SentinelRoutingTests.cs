using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Threading.Channels;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public partial class SentinelRoutingTests
{
    [Test]
    [Arguments(549)]
    [Arguments(678)]
    [Arguments(727)]
    public async Task RandomNotificationSequencesRequireRoleAndMonotonicEpochs(int seed)
    {
        var roleAccepted = true;
        var successfulRoles = 0;
        var rejectedRoleReplies = 0;
        byte[]? RoleReply(int _, string command)
        {
            if (command != "ROLE") return null;
            if (!Volatile.Read(ref roleAccepted))
            {
                Interlocked.Increment(ref rejectedRoleReplies);
                return "*3\r\n+slave\r\n+127.0.0.1\r\n:6379\r\n"u8.ToArray();
            }
            Interlocked.Increment(ref successfulRoles);
            return PrimaryRole;
        }
        await using var first = Primary(RoleReply, maxConnections: 128);
        await using var second = Primary(RoleReply, maxConnections: 128);
        var port = first.Port;
        long epoch = 0;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port), () => Volatile.Read(ref epoch), maxConnections: 256);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var endpoints = new[] { new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port) };
        var reporter = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var coalescer = new SentinelNotificationCoalescer();
        var random = new Random(seed);
        long publishedEpoch = 0;
        var successes = 0;
        var failedRoles = 0;
        var staleReports = 0;
        for (var step = 0; step < 96; step++)
        {
            for (var offered = random.Next(1, 5); offered > 0; offered--)
            {
                var source = random.Next(2);
                var hint = random.Next(3) switch
                {
                    0 => SentinelHint.FromSwitchMaster($"switch-{source}", endpoints[source], endpoints[1 - source], reporter),
                    1 => SentinelHint.FromDown($"down-{source}", reporter),
                    _ => SentinelHint.FromGap(reporter),
                };
                coalescer.Offer(hint, targetIsCurrent: false);
            }
            // Fresh master/replica pairs guarantee progress and failed ROLE coverage;
            // other passes include stale reporters,
            // failed ROLE results, duplicate hints, and pending hints retained after failure.
            epoch = step % 4 < 2 ? step + 1 : random.Next(0, step + 2);
            port = endpoints[random.Next(2)].Port;
            roleAccepted = step % 4 == 0 || step % 4 != 1 && random.Next(2) == 0;
            var before = router.Current!;
            var validationsBefore = Volatile.Read(ref successfulRoles);
            var failed = false;
            try
            {
                var selected = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
                    notificationHint: coalescer.Active).AsTask().WaitAsync(Limit);
                await Assert.That((seed, step, roleAccepted)).IsEqualTo((seed, step, true));
                await Assert.That(Volatile.Read(ref successfulRoles) > validationsBefore).IsTrue();
                await Assert.That(epoch >= publishedEpoch).IsTrue();
                await Assert.That(selected.ValidatedPeer!.Value.Port).IsEqualTo(port);
                await Assert.That(router.Current).IsSameReferenceAs(selected);
                publishedEpoch = epoch;
                successes++;
            }
            catch (RespireConnectionException)
            {
                failed = true;
                await Assert.That(router.Current).IsSameReferenceAs(before);
                if (!roleAccepted) failedRoles++;
                if (epoch < publishedEpoch) staleReports++;
            }
            var validated = failed ? null : router.Current;
            if (coalescer.TakePending(failed, validated?.Endpoint, validated?.ValidatedPeer) is null)
                coalescer.Complete();
        }
        await Assert.That(successes > 10).IsTrue();
        await Assert.That(failedRoles > 0).IsTrue();
        await Assert.That(Volatile.Read(ref rejectedRoleReplies) > 0).IsTrue();
        await Assert.That(staleReports > 0).IsTrue();
    }

    [Test]
    [Arguments("127.0.0.1")]
    [Arguments("::ffff:127.0.0.1")]
    public async Task SameEpochFallbackRecognizesResolvedOwnerAlias(string numericHost)
    {
        const int primaryPort = 7001;
        await using var first = Sentinel(() => primaryPort, () => 6);
        var reply = first.ReplyOverride!;
        first.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? AddressReply("owner.test", primaryPort)
            : command == "SENTINEL MASTER mymaster"
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ConfigurationReply(primaryPort, 6)).Replace("127.0.0.1", "owner.test"))
                : reply(id, command);
        await using var second = Sentinel(() => primaryPort, () => 6);
        var secondReply = second.ReplyOverride!;
        second.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? AddressReply(numericHost, primaryPort)
            : command == "SENTINEL MASTER mymaster"
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ConfigurationReply(primaryPort, 6)).Replace("127.0.0.1", numericHost))
                : secondReply(id, command);
        var endpoints = new[] { new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port) };
        var candidates = new List<string>();
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(Options(first.Port) with { Endpoints = [.. endpoints] },
            (options, _, _) =>
            {
                candidates.Add(options.PrimaryEndpoint.Host);
                if (options.PrimaryEndpoint.Host == "owner.test") throw new RespireConnectionException("Hostname transport unavailable");
                return ValueTask.FromResult(options.PrimaryEndpoint.Host);
            }, CancellationToken.None, new SentinelDiscoveryState(endpoints),
            hostResolver: (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]));
        await Assert.That(result).IsEqualTo(numericHost);
        await Assert.That(candidates).IsEquivalentTo(["owner.test", numericHost]);
    }

    [Test]
    public async Task SameEpochNumericFallbackRetainsTheValidatedPeerOfAMultiAddressHostname()
    {
        await using var primary = Primary(maxConnections: 32);
        await using var first = Sentinel(() => primary.Port, () => 6);
        await using var second = Sentinel(() => primary.Port, () => 6);
        // Isolate transport recovery from initial subscription-gap rediscovery.
        first.SuppressReply = second.SuppressReply = command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal);
        var firstReply = first.ReplyOverride!;
        first.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? AddressReply("localhost", primary.Port)
            : command == "SENTINEL MASTER mymaster"
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ConfigurationReply(primary.Port, 6)).Replace("127.0.0.1", "localhost"))
                : firstReply(id, command);
        await using var client = RespireClient.Create(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        var router = client.Core.Sentinel!;
        router.HostResolver = static (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback, IPAddress.Parse("127.0.0.2")]);
        await client.PingAsync().AsTask().WaitAsync(Limit);
        var original = router.Current!;
        var validatedPeer = original.ValidatedPeer;
        first.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? "-ERR hostname reporter unavailable\r\n"u8.ToArray() : firstReply(id, command);
        router.HostResolver = static (_, _) => throw new System.Net.Sockets.SocketException();
        await original.Multiplexer.GetConnection().DisposeAsync();
        var replacement = await router.GetGenerationAsync(CancellationToken.None).AsTask().WaitAsync(Limit);
        await Assert.That(replacement).IsNotSameReferenceAs(original);
        await Assert.That(replacement.ValidatedPeer).IsEqualTo(validatedPeer);
        await Assert.That(replacement.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    public async Task RejectedValidatedPeerIsCleanedUpBeforeSameEpochFallback(bool missingEpoch, bool newerEpoch)
    {
        const int primaryPort = 7001;
        var hostname = new RespireEndpoint("owner.test", primaryPort);
        var originalPeer = new RespireEndpoint("127.0.0.1", primaryPort);
        var otherPeer = new RespireEndpoint("127.0.0.2", primaryPort);
        await using var first = HostnameSentinel(primaryPort);
        await using var second = Sentinel(() => primaryPort, () => 6);
        var firstReply = first.ReplyOverride!;
        first.ReplyOverride = (id, command) =>
        {
            if (command != "SENTINEL MASTER mymaster") return firstReply(id, command);
            if (missingEpoch) return "-NOPERM metadata unavailable\r\n"u8.ToArray();
            return Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ConfigurationReply(primaryPort, newerEpoch ? 7 : 6)).Replace("127.0.0.1", hostname.Host));
        };
        var endpoints = new[] { new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port) };
        var state = new SentinelDiscoveryState(endpoints);
        state.AcceptConfiguration(hostname, 6, [originalPeer.Host, otherPeer.Host], originalPeer);
        var rejected = new List<RespireEndpoint>();
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(Options(first.Port) with { Endpoints = [.. endpoints] },
            (options, _, _) =>
            {
                if (options.PrimaryEndpoint == hostname) return ValueTask.FromResult(otherPeer);
                // The rejected result must be released before another candidate connects.
                if (!rejected.SequenceEqual([otherPeer])) throw new InvalidOperationException("Rejected candidate still owned.");
                return ValueTask.FromResult(originalPeer);
            }, CancellationToken.None, state,
            hostResolver: (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback, IPAddress.Parse(otherPeer.Host)]),
            getValidatedPeer: static peer => peer,
            rejectPrimaryAsync: peer => { rejected.Add(peer); return ValueTask.CompletedTask; });
        await Assert.That(result).IsEqualTo(newerEpoch ? otherPeer : originalPeer);
        await Assert.That(rejected).IsEquivalentTo(newerEpoch ? [] : new[] { otherPeer });
    }

    [Test]
    public async Task OwnerAliasLookupUsesThePrimaryConnectDeadline()
    {
        const int primaryPort = 7001;
        await using var sentinel = HostnameSentinel(primaryPort);
        var options = Options(sentinel.Port) with { CommandTimeout = TimeSpan.FromSeconds(1) };
        var result = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint.Host), CancellationToken.None,
            hostResolver: async (_, token) =>
            {
                // Alias lookup belongs to the separately bounded primary connection stage.
                await Task.Delay(TimeSpan.FromMilliseconds(1250), token);
                return [IPAddress.Loopback];
            });
        await Assert.That(result).IsEqualTo("owner.test");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OwnerAliasLookupPreservesConnectTimeoutAndCallerCancellation(bool cancelCaller)
    {
        await using var sentinel = HostnameSentinel(7001);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(sentinel.Port) with { ConnectTimeout = cancelCaller ? Limit : TimeSpan.FromMilliseconds(200) };
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options,
            (_, _, _) => throw new InvalidOperationException("No transport should be attempted before resolution."),
            cancellation.Token, hostResolver: async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Array.Empty<IPAddress>();
            }).AsTask();
        await entered.Task.WaitAsync(Limit);
        if (cancelCaller)
        {
            cancellation.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        }
        else
        {
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<RespireConnectionException>();
            await Assert.That(error!.InnerException is RespireTimeoutException).IsTrue();
            var timeout = (RespireTimeoutException)error.InnerException!;
            await Assert.That(timeout.CommandName).IsEqualTo("CONNECT");
            await Assert.That(timeout.Timeout).IsEqualTo(options.ConnectTimeout);
        }
    }

    [Test]
    public async Task ReporterArrivingDuringBackoffUsesTheRemainingRetry()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        await using var first = Sentinel(() => original.Port, () => 1);
        await using var unavailable = Sentinel(() => original.Port, () => 1);
        var secondPort = original.Port;
        await using var second = Sentinel(() => Volatile.Read(ref secondPort), () => secondPort == original.Port ? 1 : 2);
        var retryDelay = TimeSpan.FromMilliseconds(200);
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", unavailable.Port), new("127.0.0.1", second.Port)],
            ReconnectPolicy = new() { InitialDelay = retryDelay, MaxDelay = retryDelay, JitterRatio = 0, MaxAttempts = 1 },
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, unavailable, expectedSubscriptions: 2);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 3);
        var router = client.Core.Sentinel!;
        var clock = new FenceClock();
        router.Clock = clock;
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var before = first.ReceivedCommands.Count(command => command == query);
        var secondBefore = second.ReceivedCommands.Count(command => command == query);
        foreach (var reporter in new[] { first, unavailable })
        {
            var reply = reporter.ReplyOverride!;
            reporter.ReplyOverride = (id, command) => command == query
                ? "-ERR reporter unavailable\r\n"u8.ToArray() : reply(id, command);
        }
        var hint = SentinelHintBuilder.Create("backoff-reporters", new("127.0.0.1", promoted.Port),
            new("127.0.0.1", original.Port), ReportingSentinel: new("127.0.0.1", first.Port));
        router.QueueNotificationRediscovery(hint);
        await ReadFenceTimerAsync(clock, retryDelay);
        var worker = router.NotificationRediscovery!;
        Volatile.Write(ref secondPort, promoted.Port);
        router.QueueNotificationRediscovery(hint with { Reporters = [new("127.0.0.1", second.Port)] });
        await worker.WaitAsync(Limit);

        // A successful epoch-aware discovery brackets metadata with two address reads.
        await Assert.That(second.ReceivedCommands.Count(command => command == query)).IsEqualTo(secondBefore + 2);
        await Assert.That(first.ReceivedCommands.Count(command => command == query)).IsEqualTo(before + 1);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);
    }

    [Test]
    public async Task RepeatedSuccessfulFaultHintsHaveAMinimumDiscoveryInterval()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var watch = Stopwatch.StartNew();
        for (var index = 0; index < 8; index++)
        {
            router.QueueNotificationRediscovery(SentinelHintBuilder.Create($"fault-{index}", MustRediscover: true,
                ReportingSentinel: new("127.0.0.1", sentinel.Port)));
            if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        }
        // Seven gaps are required; allow one interval of timing margin for the first worker.
        await Assert.That(watch.ElapsedMilliseconds)
            .IsGreaterThanOrEqualTo(6L * SentinelRouter.MinimumNotificationDiscoveryIntervalMilliseconds);
        await Assert.That(router.Current!.IsRetired).IsFalse();
    }

    [Test]
    public async Task InProgressFailoverDoesNotRememberNewEpochForOldAddress()
    {
        const int originalPort = 7001, promotedPort = 7002;
        var currentPort = originalPort;
        var promoting = true;
        await using var sentinel = Sentinel(() => currentPort, () => 6);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? ConfigurationReply(currentPort, 6, promoting ? "master,failover_in_progress,disconnected" : "master")
            : reply(id, command);
        var state = new SentinelDiscoveryState([new("127.0.0.1", sentinel.Port)]);
        state.AcceptConfiguration(new("127.0.0.1", originalPort), 5);
        var candidates = new List<int>();
        await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<int>(Options(sentinel.Port),
            (candidate, _, _) =>
            {
                candidates.Add(candidate.PrimaryEndpoint.Port);
                throw new RespireConnectionException("Old primary is unavailable");
            }, CancellationToken.None, state)).Throws<RespireConnectionException>();
        await Assert.That(candidates).IsEmpty();

        promoting = false;
        currentPort = promotedPort;
        var recovered = await SentinelResolver.ResolveAndConnectPrimaryAsync(Options(sentinel.Port),
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint.Port), CancellationToken.None, state);
        await Assert.That(recovered).IsEqualTo(promotedPort);
    }

    [Test]
    public async Task PromotionDuringConfigurationReadDoesNotAssignNewEpochToOldPrimary()
    {
        const int originalPort = 7001, promotedPort = 7002;
        var currentPort = originalPort;
        await using var sentinel = Sentinel(() => currentPort, () => 6);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) =>
        {
            if (command != "SENTINEL MASTER mymaster") return reply(id, command);
            var snapshotPort = currentPort;
            currentPort = promotedPort;
            return ConfigurationReply(snapshotPort, 6);
        };
        var state = new SentinelDiscoveryState([new("127.0.0.1", sentinel.Port)]);
        state.AcceptConfiguration(new("127.0.0.1", originalPort), 5);
        var candidates = new List<int>();
        await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<int>(Options(sentinel.Port),
            (candidate, _, _) =>
            {
                candidates.Add(candidate.PrimaryEndpoint.Port);
                throw new RespireConnectionException("Old primary is unavailable");
            }, CancellationToken.None, state)).Throws<RespireConnectionException>();
        await Assert.That(candidates).IsEmpty();

        var recovered = await SentinelResolver.ResolveAndConnectPrimaryAsync(Options(sentinel.Port),
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint.Port), CancellationToken.None, state);
        await Assert.That(recovered).IsEqualTo(promotedPort);
    }

    [Test]
    public async Task DeliveryGapCanDiscoverPromotionWhenConfigurationCommandIsDenied()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref port, promoted.Port);
        var router = client.Core.Sentinel!;
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("gap-acl", MustRediscover: true,
            ReportingSentinel: new("127.0.0.1", sentinel.Port)));
        await WaitForEndpointAsync(client, promoted.Port);
        await Assert.That(promoted.ReceivedCommands.Contains("ROLE")).IsTrue();
    }

    [Test]
    public async Task FailedNewerConfigurationCannotFallBackToOlderEpoch()
    {
        const int originalPort = 7001, promotedPort = 7002;
        await using var first = Sentinel(() => promotedPort, () => 6);
        await using var second = Sentinel(() => originalPort, () => 5);
        var endpoints = new[] { new RespireEndpoint("127.0.0.1", first.Port), new RespireEndpoint("127.0.0.1", second.Port) };
        var state = new SentinelDiscoveryState(endpoints);
        state.AcceptConfiguration(new("127.0.0.1", originalPort), 5);
        var options = Options(first.Port) with { Endpoints = [.. endpoints] };
        var candidates = new List<int>();
        await Assert.That(async () => await SentinelResolver.ResolveAndConnectPrimaryAsync<int>(options,
            (candidate, _, _) =>
            {
                candidates.Add(candidate.PrimaryEndpoint.Port);
                if (candidate.PrimaryEndpoint.Port == promotedPort) throw new RespireConnectionException("Transient promotion failure");
                return ValueTask.FromResult(candidate.PrimaryEndpoint.Port);
            }, CancellationToken.None, state, notificationHint: SentinelHintBuilder.Create("gap", MustRediscover: true)))
            .Throws<RespireConnectionException>();
        await Assert.That(candidates).IsEquivalentTo([promotedPort]);
        // A later attempt may confirm the observed generation once its transport recovers.
        var recovered = await SentinelResolver.ResolveAndConnectPrimaryAsync(options,
            (candidate, _, _) => ValueTask.FromResult(candidate.PrimaryEndpoint.Port), CancellationToken.None, state);
        await Assert.That(recovered).IsEqualTo(promotedPort);
    }

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] PrimaryRole = "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray();

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task ForcedDiscoveryReusesValidatedNumericAndHostnameAliases(int connections)
    {
        await using var primary = Primary();
        var host = "localhost";
        await using var sentinel = Sentinel(() => primary.Port);
        var previous = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? AddressReply(Volatile.Read(ref host), primary.Port)
            : command == "SENTINEL MASTER mymaster" ? "-NOPERM metadata denied\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { Connections = connections });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        var original = router.Current!;
        var protection = typeof(SentinelRouter).GetMethod("IsAnnouncedTarget",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var confirmation = SentinelHintBuilder.Create("confirmed-alias", new("127.0.0.1", primary.Port), original.Endpoint);
        await Assert.That((bool)protection.Invoke(null, [original, confirmation])!).IsTrue();
        Volatile.Write(ref host, "127.0.0.1");
        var numeric = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true);
        Volatile.Write(ref host, "localhost");
        var named = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true);
        await Assert.That(numeric).IsSameReferenceAs(original);
        await Assert.That(named).IsSameReferenceAs(original);
        await Assert.That(original.IsRetired).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ForcedDiscoveryReplacesGenerationWithMixedSocketPeers(bool secondSocket, bool notification)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var previous = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? AddressReply("localhost", primary.Port)
            : command == "SENTINEL MASTER mymaster" ? "-NOPERM metadata denied\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { Connections = 2 });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        var first = original.Multiplexer.GetConnection();
        var second = original.Multiplexer.GetConnection();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        // Model one recovered slot reaching the new DNS peer while another still accepts
        // commands at the old peer. Both servers can answer ROLE master during failover.
        typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(secondSocket ? second : first, "192.0.2.1");
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);

        if (notification)
        {
            var monitorIndex = sentinel.ReceivedCommands.ToList()
                .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
            var queued = QueuedNotificationCount(router);
            await SendSentinelMessageAsync(sentinel, sentinel.ReceivedConnectionIds[monitorIndex], "+switch-master",
                $"mymaster 192.0.2.1 {primary.Port} 127.0.0.1 {primary.Port}");
            await WaitForQueuedNotificationsAsync(router, queued + 1);
            if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        }
        else await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true);
        var replacement = router.Current!;
        await Assert.That(ReferenceEquals(replacement, original)).IsFalse();
        await Assert.That(original.IsRetired).IsTrue();
        for (var index = 0; index < 2; index++)
            await Assert.That(replacement.Multiplexer.GetConnection().NetworkPeerAddress).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task SwitchToTheSameHostnameRevalidatesItsChangedPeer()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var previous = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? AddressReply("localhost", primary.Port)
            : command == "SENTINEL MASTER mymaster" ? "-NOPERM metadata denied\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        // Model a hostname changing while its established socket remains on the old peer.
        typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(original.Multiplexer.GetConnection(), "192.0.2.1");
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        var monitorIndex = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var queued = QueuedNotificationCount(router);
        await SendSentinelMessageAsync(sentinel, sentinel.ReceivedConnectionIds[monitorIndex], "+switch-master",
            $"mymaster 192.0.2.1 {primary.Port} localhost {primary.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        await Assert.That(ReferenceEquals(router.Current, original)).IsFalse();
        await Assert.That(original.IsRetired).IsTrue();
        await Assert.That(router.Current!.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task StableHostnamePublishesChangedValidatedPeer()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var previous = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? AddressReply("localhost", primary.Port)
            : command == "SENTINEL MASTER mymaster" ? "-NOPERM metadata denied\r\n"u8.ToArray() : previous(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        // Model DNS changing behind an established socket. The old socket keeps answering
        // ROLE master; its captured peer differs from every freshly connected socket.
        typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(original.Multiplexer.GetConnection(), "192.0.2.1");
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        var replacement = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true);
        await Assert.That(ReferenceEquals(replacement, original)).IsFalse();
        await Assert.That(original.IsRetired).IsTrue();
        await Assert.That(replacement.Endpoint).IsEqualTo(original.Endpoint);
        await Assert.That(replacement.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task AnnouncedIpv6TargetUsesNormalizedIdentity()
    {
        await using var client = RespireClient.Create(Options(26379));
        await using var current = new SentinelRouter.Generation(client.Core.Sentinel!, client.Core,
            Options(26379) with { Endpoints = [new("2001:db8::1", 6379)] });
        var hint = SentinelHintBuilder.Create("failback", new("2001:0db8:0:0:0:0:0:1", 6379), current.Endpoint);
        var method = typeof(SentinelRouter).GetMethod("IsAnnouncedTarget",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.That((bool)method.Invoke(null, [current, hint])!).IsTrue();
    }

    [Test]
    public async Task ValidatedPrimaryDoesNotRetainUnconnectedDnsAddresses()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var previous = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
            ? AddressReply("localhost", primary.Port)
            : command == "SENTINEL MASTER mymaster" ? "-NOPERM metadata denied\r\n"u8.ToArray() : previous(id, command);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var router = client.Core.Sentinel!;
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback, IPAddress.Parse("192.0.2.9")]);
        var generation = await router.GetGenerationAsync(CancellationToken.None);
        await Assert.That(generation.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task DelayedSourceAddressesNeverDescribeArrivalGeneration(bool samePort, bool failback)
    {
        await using var primary = Primary();
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var arrived = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("192.0.2.1", samePort ? primary.Port : 1)] });
        await using var current = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("127.0.0.1", primary.Port)] });
        await current.Multiplexer.EnsureConnectedAsync();
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter).GetField("_coalescer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;
        var hint = SentinelHintBuilder.Create("b-to-c", new("192.0.2.2", 6379), new("source.invalid", primary.Port));
        coalescer.Offer(in hint, false);
        var addresses = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.HostResolver = (_, token) => addresses.Task.WaitAsync(token);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task)resolve.Invoke(router, [hint, arrived, CancellationToken.None, null])!;
        if (failback) coalescer.Offer(SentinelHintBuilder.Create("c-to-b", current.Endpoint, hint.Target), true);
        addresses.SetResult([IPAddress.Loopback]);
        await pending.WaitAsync(Limit);
        await Assert.That(current.IsRetired).IsEqualTo(!failback);
    }

    [Test]
    public async Task SameEndpointSentinelPublicationPreservesPubSubHealth()
    {
        await using var client = RespireClient.Create(Options(26379));
        var core = client.Core;
        var node = core.Multiplexer;
        var endpoint = new RespireEndpoint(node.Host, node.Port);
        var changes = new List<RespireConnectionStateChange>();
        client.ConnectionStateChanged += changes.Add;
        var pubSubError = new RespireConnectionException("subscription reconnect pending");
        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(endpoint, RespireConnectionState.Disconnected, pubSubError));
        changes.Clear();

        core.NotifySentinelPrimaryChanged(node, node);
        await Assert.That(changes.Select(change => change.State))
            .IsEquivalentTo([RespireConnectionState.Disconnected]);

        core.NotifySubscriptionStateChanged(new RespireConnectionStateChange(endpoint, RespireConnectionState.Connected, null));
        await Assert.That(changes.Select(change => change.State))
            .IsEquivalentTo([RespireConnectionState.Disconnected, RespireConnectionState.Connected]);
    }

    [Test]
    public async Task SentinelDisconnectNotificationPreservesTransportError()
    {
        await using var client = RespireClient.Create(Options(26379));
        var node = client.Core.Multiplexer;
        var error = new RespireConnectionException("remote EOF");
        RespireConnectionStateChange? observed = null;
        client.ConnectionStateChanged += change => observed = change;

        client.Core.NotifySentinelDisconnected(node, error);

        await Assert.That(observed?.State).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(observed?.Error).IsSameReferenceAs(error);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LazySentinelEndpointIsUnavailableUntilValidatedPrimary(bool failValidation)
    {
        var valid = !failValidation;
        await using var primary = Primary((_, command) => command == "ROLE" && !Volatile.Read(ref valid)
            ? "*0\r\n"u8.ToArray() : null);
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        foreach (IRespireClient view in new[] { client, prefixed })
            await Assert.That(() => _ = view.Endpoint).ThrowsExactly<InvalidOperationException>();
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        if (failValidation)
        {
            await Assert.That(async () => await client.PingAsync().AsTask().WaitAsync(Limit)).Throws<RespireConnectionException>();
            await Assert.That(() => _ = client.Endpoint).ThrowsExactly<InvalidOperationException>();
            Volatile.Write(ref valid, true);
        }
        await prefixed.PingAsync().AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(prefixed.Endpoint).IsEqualTo(client.Endpoint);
    }

    [Test]
    public async Task SwitchNotificationValidatesItsTargetBeforeAcceptingOtherSentinelView()
    {
        await using var stalePrimary = Primary();
        await using var promotedPrimary = Primary();
        await using var firstSentinel = Sentinel(() => stalePrimary.Port);
        var reportingPort = stalePrimary.Port;
        // The epoch advances with the failover even if coalesced startup never queried this reporter.
        await using var reportingSentinel = Sentinel(() => Volatile.Read(ref reportingPort),
            () => Volatile.Read(ref reportingPort) == stalePrimary.Port ? 0 : 1);
        var options = Options(firstSentinel.Port) with
        {
            Endpoints = [new("127.0.0.1", firstSentinel.Port), new("127.0.0.1", reportingSentinel.Port)],
        };
        await using var client = RespireClient.Create(options);
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await WaitForCommandAsync(firstSentinel, "SUBSCRIBE +switch-master");
        await WaitForCommandAsync(reportingSentinel, "SUBSCRIBE +switch-master");
        var router = client.Core.Sentinel!;
        using (var monitorTimeout = new CancellationTokenSource(Limit))
            await SentinelTestSetup.WaitForSubscriptionsAsync(router, 2, monitorTimeout.Token);
        while (router.NotificationRediscovery is { } initialRediscovery)
            await initialRediscovery.WaitAsync(Limit);

        var firstDiscoveryCount = firstSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster");
        var reportingDiscoveryCount = reportingSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster");
        var monitorCommand = reportingSentinel.ReceivedCommands.ToList().FindIndex(command =>
            command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = reportingSentinel.ReceivedConnectionIds[monitorCommand];
        Volatile.Write(ref reportingPort, promotedPrimary.Port);
        await SendSentinelMessageAsync(reportingSentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {stalePrimary.Port} 127.0.0.1 {promotedPrimary.Port}");

        using var timeout = new CancellationTokenSource(Limit);
        while (client.Endpoint.Port != promotedPrimary.Port)
            await Task.Delay(5, timeout.Token);

        await Assert.That(reportingSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster")).IsGreaterThan(reportingDiscoveryCount);
        await Assert.That(firstSentinel.ReceivedCommands.Count(command =>
            command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster")).IsEqualTo(firstDiscoveryCount);
    }

    [Test]
    public async Task MasterDownNotificationQueriesReportingSentinelFirst()
    {
        await using var stalePrimary = Primary();
        await using var promotedPrimary = Primary();
        await using var firstSentinel = Sentinel(() => stalePrimary.Port);
        var reportingPort = stalePrimary.Port;
        // Epochs belong to the simulated failover, not to whether startup happened to
        // query this reporter before its subscription became live.
        await using var reportingSentinel = Sentinel(() => Volatile.Read(ref reportingPort),
            () => Volatile.Read(ref reportingPort) == stalePrimary.Port ? 0 : 1);
        var options = Options(firstSentinel.Port) with
        {
            Endpoints = [new("127.0.0.1", firstSentinel.Port), new("127.0.0.1", reportingSentinel.Port)],
        };
        await using var client = RespireClient.Create(options);
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await WaitForCommandAsync(firstSentinel, "SUBSCRIBE +sdown");
        await WaitForCommandAsync(reportingSentinel, "SUBSCRIBE +sdown");
        var router = client.Core.Sentinel!;
        using (var monitorTimeout = new CancellationTokenSource(Limit))
            await SentinelTestSetup.WaitForSubscriptionsAsync(router, 2, monitorTimeout.Token);
        while (router.NotificationRediscovery is { } initialRediscovery)
            await initialRediscovery.WaitAsync(Limit);

        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var firstDiscoveryCount = firstSentinel.ReceivedCommands.Count(command => command == discovery);
        var reportingDiscoveryCount = reportingSentinel.ReceivedCommands.Count(command => command == discovery);
        var monitorCommand = reportingSentinel.ReceivedCommands.ToList().FindIndex(command =>
            command.StartsWith("SUBSCRIBE +sdown", StringComparison.Ordinal));
        var monitorConnection = reportingSentinel.ReceivedConnectionIds[monitorCommand];
        Volatile.Write(ref reportingPort, promotedPrimary.Port);
        await SendSentinelMessageAsync(reportingSentinel, monitorConnection, "+sdown",
            $"master mymaster 127.0.0.1 {stalePrimary.Port}");

        using var timeout = new CancellationTokenSource(Limit);
        while (client.Endpoint.Port != promotedPrimary.Port)
            await Task.Delay(5, timeout.Token);

        await Assert.That(reportingSentinel.ReceivedCommands.Count(command => command == discovery))
            .IsGreaterThan(reportingDiscoveryCount);
        await Assert.That(firstSentinel.ReceivedCommands.Count(command => command == discovery))
            .IsEqualTo(firstDiscoveryCount);
    }

    [Test]
    public async Task DownEventDuringSwitchRediscoveryTriggersAnotherDiscovery()
    {
        await using var original = Primary();
        await using var intermediate = Primary();
        await using var promoted = Primary();
        await using var fallback = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +switch-master");
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        Volatile.Write(ref port, promoted.Port);
        sentinel.SuppressReply = command => command == discovery;
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {intermediate.Port}");
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {intermediate.Port} 127.0.0.1 {promoted.Port}");
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown",
            $"master mymaster 127.0.0.1 {promoted.Port}");
        // Release the blocked discovery only after both later events reached the router.
        await WaitForQueuedNotificationsAsync(router, queued + 3);

        Volatile.Write(ref port, fallback.Port);
        sentinel.SuppressReply = null;
        // The pending discovery uses the Sentinel command connection, which may differ from its
        // event monitor connection. Reply to the blocked query with the switch target explicitly.
        var queryIndex = sentinel.ReceivedCommands.ToList().FindLastIndex(command => command == discovery);
        var queryConnection = sentinel.ReceivedConnectionIds[queryIndex];
        await sentinel.SendRawAsync(AddressReply(promoted.Port), queryConnection);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 2);
        await WaitForEndpointAsync(client, fallback.Port);
        await Assert.That(client.Endpoint.Port).IsEqualTo(fallback.Port);
    }

    [Test]
    public async Task SingleSwitchNotificationRetriesFailedRediscovery()
    {
        await using var original = Primary();
        await using var unavailable = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null,
        };
        await using var recovered = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        });
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        Volatile.Write(ref port, unavailable.Port);
        sentinel.SuppressReply = command => command == discovery;

        // Only one Sentinel publishes the switch, and its rediscovery fails ROLE validation.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {unavailable.Port}");
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        Volatile.Write(ref port, recovered.Port);
        sentinel.SuppressReply = null;
        var queryConnection = sentinel.ReceivedConnectionIds[^1];
        await sentinel.SendRawAsync(AddressReply(unavailable.Port), queryConnection);

        await WaitForEndpointAsync(client, recovered.Port);
        await Assert.That(client.Endpoint.Port).IsEqualTo(recovered.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SwitchRetryRejectsOriginalPrimaryAfterTransientTargetFailure(bool denyEpoch)
    {
        await using var original = Primary();
        var targetReady = false;
        await using var promoted = Primary((_, command) => command == "ROLE" && !Volatile.Read(ref targetReady)
            ? "*0\r\n"u8.ToArray() : null);
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        if (denyEpoch)
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
        }
        var retryDelay = TimeSpan.FromSeconds(1);
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = retryDelay, MaxDelay = retryDelay, JitterRatio = 0 },
        });
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var clock = new FenceClock();
        router.Clock = clock;
        Volatile.Write(ref port, promoted.Port);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("switch-retry",
            new("127.0.0.1", promoted.Port), new("127.0.0.1", original.Port),
            ReportingSentinel: new("127.0.0.1", sentinel.Port)));

        var firstRetry = await ReadFenceTimerAsync(clock, retryDelay);
        // The target failed ROLE validation. A stale reply still naming the source must not
        // publish a replacement, even when that source continues to answer ROLE master.
        Volatile.Write(ref port, original.Port);
        firstRetry.Fire();
        var secondRetry = await ReadFenceTimerAsync(clock, retryDelay);
        await Assert.That(router.Current!.IsRetired).IsTrue();

        Volatile.Write(ref targetReady, true);
        Volatile.Write(ref port, promoted.Port);
        secondRetry.Fire();
        await WaitForEndpointAsync(client, promoted.Port);
    }

    [Test]
    public async Task CoveredStartupGapDoesNotRepeatRoleValidation()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var roles = primary.ReceivedCommands.Count(command => command == "ROLE");
        var startup = SentinelHint.FromGap(new("127.0.0.1", sentinel.Port)) with
        {
            StartupSubscriptionVersion = router.Monitoring.SubscriptionVersion,
        };
        await router.GetGenerationAsync(default, forceDiscovery: true, notificationHint: startup);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(roles);
        // An actual reconnect/overflow gap still requires fresh validation.
        await router.GetGenerationAsync(default, forceDiscovery: true,
            notificationHint: startup with { StartupSubscriptionVersion = 0 });
        await Assert.That(primary.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(roles + 1);
    }

    [Test]
    public async Task StartupCoverageDoesNotSkipUnqueriedReporter()
    {
        await using var primary = Primary();
        await using var promoted = Primary();
        await using var first = Sentinel(() => primary.Port, () => 0);
        await using var second = Sentinel(() => promoted.Port, () => 1);
        second.SuppressReply = command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal);
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        var router = client.Core.Sentinel!;
        // Offer the alternate reporter at an already validated version, as when two
        // startup gaps attach before one lookup begins. Control the hint directly so
        // transport scheduling cannot hide the unqueried-reporter regression.
        var startup = SentinelHint.FromGap(new("127.0.0.1", second.Port)) with
        {
            StartupSubscriptionVersion = router.Monitoring.SubscriptionVersion,
        };
        await router.GetGenerationAsync(default, forceDiscovery: true, notificationHint: startup);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);
        await Assert.That(second.ReceivedCommands.Any(command => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME "))).IsTrue();
        await Assert.That(promoted.ReceivedCommands.Contains("ROLE")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task InitialSentinelValidationWaitsForAllExpectedAcknowledgements(bool promotionBeforeAttach)
    {
        await using var primary = Primary();
        await using var promoted = Primary();
        await using var first = Sentinel(() => primary.Port);
        var reportedPort = primary.Port;
        await using var second = Sentinel(() => Volatile.Read(ref reportedPort),
            () => Volatile.Read(ref reportedPort) == primary.Port ? 0 : 1);
        second.SuppressReply = command => command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal);
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForCommandAsync(second, "SUBSCRIBE +switch-master");
        var readiness = WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        await Assert.That(readiness.IsCompleted).IsFalse();
        var index = second.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master"));
        var connection = second.ReceivedConnectionIds[index];
        var acknowledgements = second.ReplyOverride!(connection, second.ReceivedCommands[index]);
        if (promotionBeforeAttach) Volatile.Write(ref reportedPort, promoted.Port);
        second.SuppressReply = null;
        await second.SendRawAsync(acknowledgements!, connection);
        await readiness.WaitAsync(Limit);
        await Assert.That(client.Core.Sentinel!.SubscribedSentinelCount).IsEqualTo(2);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promotionBeforeAttach ? promoted.Port : primary.Port);
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(true, true, true)]
    public async Task AlternateReporterUsesEpochInsteadOfArrivalOrder(bool switchHint, bool laterPromotion, bool denyEpoch)
    {
        await using var original = Primary();
        await using var intermediate = Primary();
        await using var latest = Primary();
        var firstPort = original.Port;
        var secondPort = original.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort),
            () => Volatile.Read(ref firstPort) == original.Port ? 1 : 2);
        await using var second = Sentinel(() => Volatile.Read(ref secondPort),
            () => Volatile.Read(ref secondPort) == original.Port ? 1 : 3);
        if (denyEpoch)
        {
            foreach (var sentinel in new[] { first, second })
            {
                var reply = sentinel.ReplyOverride!;
                sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                    ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
            }
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var router = client.Core.Sentinel!;
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var firstQueries = first.ReceivedCommands.Count(command => command == query);
        var secondQueries = second.ReceivedCommands.Count(command => command == query);
        first.SuppressReply = command => command == query;
        Volatile.Write(ref firstPort, intermediate.Port);
        Volatile.Write(ref secondPort, laterPromotion ? latest.Port : original.Port);
        SentinelHint HintFrom(FakeRespServer reporter) => switchHint
            ? SentinelHint.FromSwitchMaster("reconcile", new("127.0.0.1", original.Port),
                new("127.0.0.1", intermediate.Port), new("127.0.0.1", reporter.Port))
            : SentinelHint.FromDown("reconcile", new("127.0.0.1", reporter.Port),
                new("127.0.0.1", original.Port));
        var hint = HintFrom(first);
        router.QueueNotificationRediscovery(in hint);
        await WaitForCommandCountAsync(first, query, firstQueries + 1);
        var blockedConnection = first.ReceivedConnectionIds[^1];
        router.QueueNotificationRediscovery(HintFrom(second));
        first.SuppressReply = null;
        await first.SendRawAsync(AddressReply(intermediate.Port), blockedConnection);
        await WaitForCommandCountAsync(second, query, secondQueries + 1);
        if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(laterPromotion ? latest.Port : intermediate.Port);
    }

    [Test]
    public async Task IndependentFailbackSurvivesDelayedActiveSwitchWithoutEpochs()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var firstPort = original.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => original.Port);
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var router = client.Core.Sentinel!;
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var queries = first.ReceivedCommands.Count(command => command == query);
        first.SuppressReply = command => command == query;
        Volatile.Write(ref firstPort, promoted.Port);
        var outbound = SentinelHint.FromSwitchMaster("a-to-b", new("127.0.0.1", original.Port),
            new("127.0.0.1", promoted.Port), new("127.0.0.1", first.Port));
        router.QueueNotificationRediscovery(in outbound);
        await WaitForCommandCountAsync(first, query, queries + 1);
        var queryIndex = first.ReceivedCommands.ToList().FindLastIndex(command => command == query);
        router.QueueNotificationRediscovery(SentinelHint.FromSwitchMaster("b-to-a", outbound.Target,
            outbound.OldPrimary, new("127.0.0.1", second.Port)));
        router.QueueNotificationRediscovery(in outbound);
        var worker = router.NotificationRediscovery!;
        first.SuppressReply = null;
        await first.SendRawAsync(AddressReply(promoted.Port), first.ReceivedConnectionIds[queryIndex]);
        await worker.WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(original.Port);
        await Assert.That(router.Current!.IsRetired).IsFalse();
    }

    [Test]
    public async Task CompletedSwitchCycleAllowsGenuineRecurrenceWithoutEpochs()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var firstPort = original.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => original.Port);
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var router = client.Core.Sentinel!;
        var outbound = SentinelHint.FromSwitchMaster("a-to-b", new("127.0.0.1", original.Port),
            new("127.0.0.1", promoted.Port), new("127.0.0.1", first.Port));
        Volatile.Write(ref firstPort, promoted.Port);
        await CompleteAsync(outbound, promoted.Port);
        await CompleteAsync(SentinelHint.FromSwitchMaster("b-to-a", outbound.Target,
            outbound.OldPrimary, new("127.0.0.1", second.Port)), original.Port);
        // A real third transition has exactly the same switch payload, reporter, GET-MASTER
        // reply, and ROLE result as a delayed first transition. Completed edge history alone
        // cannot reject the delayed event without also rejecting this genuine recurrence.
        await CompleteAsync(outbound, promoted.Port);

        async Task CompleteAsync(SentinelHint hint, int expectedPort)
        {
            router.QueueNotificationRediscovery(in hint);
            await WaitForEndpointAsync(client, expectedPort);
            if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
            await Assert.That(router.Current!.IsRetired).IsFalse();
        }
    }

    [Test]
    public async Task CurrentTargetSwitchSurvivesStaleUntargetedDiscovery()
    {
        await using var current = Primary();
        await using var stale = Primary();
        var firstPort = current.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => current.Port);
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM configuration metadata denied\r\n"u8.ToArray() : reply(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var router = client.Core.Sentinel!;
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var queries = first.ReceivedCommands.Count(command => command == query);
        first.SuppressReply = command => command == query;
        Volatile.Write(ref firstPort, stale.Port);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("gap", MustRediscover: true,
            ReportingSentinel: new("127.0.0.1", first.Port)));
        await WaitForCommandCountAsync(first, query, queries + 1);
        var queryIndex = first.ReceivedCommands.ToList().FindLastIndex(command => command == query);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("confirmed-current",
            new("127.0.0.1", current.Port), new("127.0.0.1", stale.Port),
            ReportingSentinel: new("127.0.0.1", second.Port)));
        var worker = router.NotificationRediscovery!;
        first.SuppressReply = null;
        await first.SendRawAsync(AddressReply(stale.Port), first.ReceivedConnectionIds[queryIndex]);
        await worker.WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(current.Port);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task FailbackReconciliationConsumesValidatedPrimaryAliases(bool hostname, bool staleReporterFirst)
    {
        await using var current = Primary();
        await using var stale = Primary();
        var secondPort = current.Port;
        await using var first = Sentinel(() => current.Port);
        await using var second = Sentinel(() => Volatile.Read(ref secondPort));
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        byte[] CurrentReply() => hostname
            ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(AddressReply(current.Port)).Replace("127.0.0.1", "localhost"))
            : AddressReply(current.Port);
        foreach (var sentinel in new[] { first, second })
        {
            var previous = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM configuration metadata denied\r\n"u8.ToArray()
                : ReferenceEquals(sentinel, first) && command == query ? CurrentReply() : previous(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var router = client.Core.Sentinel!;
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        var queries = first.ReceivedCommands.Count(command => command == query);
        first.SuppressReply = command => command == query;
        router.QueueNotificationRediscovery(SentinelHint.FromGap(new("127.0.0.1", first.Port)));
        await WaitForCommandCountAsync(first, query, queries + 1);
        var queryIndex = first.ReceivedCommands.ToList().FindLastIndex(command => command == query);
        Volatile.Write(ref secondPort, stale.Port);
        var confirmation = SentinelHintBuilder.Create("b-to-a",
            new("127.0.0.1", current.Port), new("127.0.0.1", stale.Port),
            ReportingSentinel: new("127.0.0.1", first.Port));
        var delayed = SentinelHintBuilder.Create("a-to-b-delayed",
            new("127.0.0.1", stale.Port), new("127.0.0.1", current.Port),
            ReportingSentinel: new("127.0.0.1", second.Port));
        router.QueueNotificationRediscovery(staleReporterFirst ? delayed : confirmation);
        router.QueueNotificationRediscovery(staleReporterFirst ? confirmation : delayed);
        var worker = router.NotificationRediscovery!;
        first.SuppressReply = null;
        await first.SendRawAsync(CurrentReply(), first.ReceivedConnectionIds[queryIndex]);
        await worker.WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(current.Port);
        await Assert.That(stale.ReceivedCommands.Contains("ROLE")).IsFalse();
    }

    [Test]
    public async Task StaleSwitchHintAcceptsIndependentlyPublishedLaterPrimary()
    {
        await using var original = Primary();
        await using var target = Primary();
        await using var latest = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        Volatile.Write(ref port, latest.Port);
        var current = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true);
        var hint = SentinelHintBuilder.Create("old-switch", new("127.0.0.1", target.Port),
            new("127.0.0.1", original.Port), ReportingSentinel: new("127.0.0.1", sentinel.Port));
        var confirmed = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true, notificationHint: hint);
        await Assert.That(ReferenceEquals(confirmed, current)).IsTrue();
    }

    [Test]
    [Arguments("localhost", "localhost", false)]
    [Arguments("LOCALHOST", "localHOST", false)]
    [Arguments("::ffff:127.0.0.1", "127.0.0.1", false)]
    [Arguments("localhost", "localhost", true)]
    [Arguments("LOCALHOST", "localHOST", true)]
    [Arguments("::ffff:127.0.0.1", "127.0.0.1", true)]
    public async Task SameOutageFromAnotherReporterCannotUndoPromotion(string firstHost, string secondHost, bool delayed)
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var firstPort = original.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => original.Port);
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) =>
            {
                if (command == "SENTINEL MASTER mymaster") return "-NOPERM metadata unavailable\r\n"u8.ToArray();
                var reportedPort = ReferenceEquals(sentinel, first) ? Volatile.Read(ref firstPort) : original.Port;
                if (command == "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" && reportedPort == original.Port
                    && firstHost.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return AddressReply("localhost", original.Port);
                return reply(id, command);
            };
        }
        await using var client = RespireClient.Create(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        client.Core.Sentinel!.HostResolver = static (_, _) => Task.FromResult(new[] { IPAddress.Loopback });
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        var firstMonitor = first.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var secondMonitor = second.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var firstQueries = first.ReceivedCommands.Count(command => command == query);
        var secondQueries = second.ReceivedCommands.Count(command => command == query);
        first.SuppressReply = command => command == query;
        Volatile.Write(ref firstPort, promoted.Port);

        await SendSentinelMessageAsync(first, first.ReceivedConnectionIds[firstMonitor], "+sdown",
            $"master mymaster {firstHost} {original.Port}");
        await WaitForCommandCountAsync(first, query, firstQueries + 1);
        var blockedConnection = first.ReceivedConnectionIds[^1];
        var worker = router.NotificationRediscovery!;
        if (delayed)
        {
            first.SuppressReply = null;
            await first.SendRawAsync(AddressReply(promoted.Port), blockedConnection);
            await worker.WaitAsync(Limit);
            await Assert.That(router.NotificationRediscovery).IsNull();
        }
        await SendSentinelMessageAsync(second, second.ReceivedConnectionIds[secondMonitor], "+odown",
            $"master mymaster {secondHost} {original.Port} #quorum 2/2");
        await WaitForQueuedNotificationsAsync(router, queued + 2);

        // The second Sentinel still names the old server, which still answers ROLE master.
        // Equivalent outage identities must reconcile without undoing the first publication.
        if (!delayed)
        {
            first.SuppressReply = null;
            await first.SendRawAsync(AddressReply(promoted.Port), blockedConnection);
            await worker.WaitAsync(Limit);
        }
        else if (router.NotificationRediscovery is { } delayedWorker) await delayedWorker.WaitAsync(Limit);
        await Assert.That(second.ReceivedCommands.Count(command => command == query)).IsGreaterThan(secondQueries);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);

        // A later outage of B can legitimately fail back to A without epoch metadata.
        Volatile.Write(ref firstPort, original.Port);
        await SendSentinelMessageAsync(first, first.ReceivedConnectionIds[firstMonitor], "+sdown",
            $"master mymaster 127.0.0.1 {promoted.Port}");
        await WaitForEndpointAsync(client, original.Port);
        if (router.NotificationRediscovery is { } failback) await failback.WaitAsync(Limit);
        // A recurring A outage is fresh after failback; old outage identity must not fence B.
        Volatile.Write(ref firstPort, promoted.Port);
        await SendSentinelMessageAsync(first, first.ReceivedConnectionIds[firstMonitor], "+sdown",
            $"master mymaster {firstHost} {original.Port}");
        await WaitForEndpointAsync(client, promoted.Port);
    }

    [Test]
    [Arguments("primary.test", "127.0.0.1", true)]
    [Arguments("PRIMARY.test", "::ffff:127.0.0.1", true)]
    [Arguments("primary.test", "127.0.0.1,::ffff:127.0.0.1", true)]
    [Arguments("primary.test", "127.0.0.1,192.0.2.1", false)]
    [Arguments("primary.test", "192.0.2.1", false)]
    [Arguments("primary.test", "", false)]
    public async Task HostnameDownReportRecognizesTheValidatedNumericPeer(string host, string addresses, bool accepts)
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        router.HostResolver = (_, _) => addresses.Length == 0
            ? throw new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound)
            : Task.FromResult(addresses.Split(',').Select(IPAddress.Parse).ToArray());
        Volatile.Write(ref port, promoted.Port);
        var hint = SentinelHint.FromDown("alias-down", new("127.0.0.1", sentinel.Port), new(host, original.Port));
        var discovery = router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
            notificationHint: hint).AsTask().WaitAsync(Limit);
        if (accepts)
        {
            var selected = await discovery;
            await Assert.That(selected.Endpoint.Port).IsEqualTo(promoted.Port);
        }
        else
        {
            await Assert.That(async () => await discovery).Throws<RespireConnectionException>();
            await Assert.That(client.Endpoint.Port).IsEqualTo(original.Port);
            await Assert.That(promoted.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DownReportAliasTimeoutPreservesCandidateDeadlineAndCallerCancellation(bool cancelCaller)
    {
        const int primaryPort = 7001;
        await using var sentinel = HostnameSentinel(primaryPort);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = Options(sentinel.Port) with { ConnectTimeout = cancelCaller ? Limit : TimeSpan.FromMilliseconds(200) };
        var owner = new RespireEndpoint("127.0.0.1", primaryPort);
        var reporter = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var hint = SentinelHint.FromDown("slow", reporter, new("slow.test", primaryPort))
            .BindDownReportsToCurrentPrimary(new(owner, owner));
        var validations = 0;
        var pending = SentinelResolver.ResolveAndConnectPrimaryAsync(options, async (candidate, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(50, token);
            validations++;
            return candidate.PrimaryEndpoint;
        }, cancellation.Token, notificationHint: hint, hostResolver: async (host, token) =>
        {
            if (host != "slow.test") return [IPAddress.Loopback];
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return [];
        }).AsTask();
        await entered.Task.WaitAsync(Limit);
        if (cancelCaller)
        {
            cancellation.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
            await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
            await Assert.That(validations).IsEqualTo(0);
        }
        else
        {
            var selected = await pending.WaitAsync(Limit);
            await Assert.That(selected.Host).IsEqualTo("owner.test");
            await Assert.That(validations).IsEqualTo(1);
        }
    }

    [Test]
    public async Task UnreportedSentinelCannotBorrowAnotherDownReportsEvidence()
    {
        await using var stale = Primary();
        await using var current = Primary();
        await using var replica = Primary((_, command) => command == "ROLE" ? "*1\r\n+slave\r\n"u8.ToArray() : null);
        var firstPort = current.Port;
        var secondPort = current.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => Volatile.Read(ref secondPort));
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        Volatile.Write(ref firstPort, replica.Port);
        Volatile.Write(ref secondPort, stale.Port);
        var hint = SentinelHint.FromDown("current-down", new("127.0.0.1", first.Port), new("127.0.0.1", current.Port));
        await Assert.That(async () => await client.Core.Sentinel!.GetGenerationAsync(CancellationToken.None,
            forceDiscovery: true, notificationHint: hint).AsTask().WaitAsync(Limit)).Throws<RespireConnectionException>();
        await Assert.That(client.Endpoint.Port).IsEqualTo(current.Port);
        await Assert.That(stale.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SlowDownReportAliasCannotHideOtherCurrentOwnerEvidence(bool numeric)
    {
        await using var current = Primary();
        await using var promoted = Primary();
        var port = current.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.HostResolver = async (host, token) =>
        {
            if (host != "slow.test") return [IPAddress.Loopback];
            slowStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { slowStopped.TrySetResult(); }
            return [];
        };
        Volatile.Write(ref port, promoted.Port);
        var reporter = new RespireEndpoint("127.0.0.1", sentinel.Port);
        var slow = SentinelHint.FromDown("slow", reporter, new("slow.test", current.Port));
        var known = SentinelHint.FromDown("known", reporter, new(numeric ? "127.0.0.1" : "known.test", current.Port));
        var hint = SentinelNotificationCoalescer.Merge(slow, in known);
        var selected = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
            notificationHint: hint).AsTask().WaitAsync(Limit);
        await Assert.That(selected.Endpoint.Port).IsEqualTo(promoted.Port);
        if (slowStarted.Task.IsCompleted) await slowStopped.Task.WaitAsync(Limit);
    }

    [Test]
    public async Task NewerEpochDoesNotRequireDownReportAliasResolution()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        long epoch = 1;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port), () => Volatile.Read(ref epoch));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        router.HostResolver = (_, _) => throw new InvalidOperationException("A newer epoch needs no down-report DNS proof.");
        Volatile.Write(ref port, promoted.Port);
        Volatile.Write(ref epoch, 2);
        var hint = SentinelHint.FromDown("alias-down", new("127.0.0.1", sentinel.Port), new("unavailable.test", original.Port));
        var selected = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
            notificationHint: hint).AsTask().WaitAsync(Limit);
        await Assert.That(selected.Endpoint.Port).IsEqualTo(promoted.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedDownReportsKeepEachReportersOwnershipFence(bool reverse)
    {
        await using var original = Primary();
        await using var current = Primary();
        await using var promoted = Primary();
        var firstPort = current.Port;
        var secondPort = current.Port;
        await using var first = Sentinel(() => Volatile.Read(ref firstPort));
        await using var second = Sentinel(() => Volatile.Read(ref secondPort));
        foreach (var sentinel in new[] { first, second })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        }
        await using var client = await RespireClient.ConnectAsync(Options(first.Port) with
        {
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
        await WaitForInitialSentinelValidationAsync(client, first);
        await WaitForInitialSentinelValidationAsync(client, second, expectedSubscriptions: 2);
        Volatile.Write(ref firstPort, original.Port);
        Volatile.Write(ref secondPort, promoted.Port);
        var stale = SentinelHint.FromDown("old-down", new("127.0.0.1", first.Port), new("127.0.0.1", original.Port));
        var fresh = SentinelHint.FromDown("current-down", new("127.0.0.1", second.Port), new("127.0.0.1", current.Port));
        var hint = reverse ? SentinelNotificationCoalescer.Merge(fresh, in stale)
            : SentinelNotificationCoalescer.Merge(stale, in fresh);
        var selected = await client.Core.Sentinel!.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
            notificationHint: hint).AsTask().WaitAsync(Limit);
        await Assert.That(selected.Endpoint.Port).IsEqualTo(promoted.Port);
        await Assert.That(original.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(0);
    }

    [Test]
    public async Task DownReportQueuedDuringPublicationCanDescribeTheNextOutage()
    {
        await using var original = Primary();
        await using var intermediate = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitor = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitor];
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);
        const string query = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var queries = sentinel.ReceivedCommands.Count(command => command == query);
        sentinel.SuppressReply = command => command == query;
        Volatile.Write(ref port, intermediate.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown",
            $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForCommandCountAsync(sentinel, query, queries + 1);
        var blockedConnection = sentinel.ReceivedConnectionIds[^1];
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown",
            $"master mymaster 127.0.0.1 {intermediate.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 2);
        Volatile.Write(ref port, promoted.Port);
        sentinel.SuppressReply = null;
        await sentinel.SendRawAsync(AddressReply(intermediate.Port), blockedConnection);
        await WaitForEndpointAsync(client, promoted.Port);
    }

    [Test]
    public async Task OptionalMetadataLoggerFailureCannotStopNotificationPromotion()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) =>
        {
            if (command != "SENTINEL MASTER mymaster") return reply(id, command);
            return Volatile.Read(ref port) == original.Port
                ? "-NOPERM metadata unavailable\r\n"u8.ToArray()
                : "?invalid RESP\r\n"u8.ToArray();
        };
        var logger = new SentinelTests.OptionalDiscoveryLogger();
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { LoggerFactory = logger });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitor = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, sentinel.ReceivedConnectionIds[monitor], "+sdown",
            $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForEndpointAsync(client, promoted.Port);
        await Assert.That(logger.Failures).IsGreaterThan(0);
        await Assert.That(promoted.ReceivedCommands).Contains("ROLE");
    }

    [Test]
    public async Task RepeatedMasterDownDuringRediscoveryTriggersAnotherDiscovery()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +sdown");
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +odown");
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var downMessage = $"master mymaster 127.0.0.1 {original.Port}";
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);
        sentinel.SuppressReply = command => command == discovery;

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown", downMessage);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown", downMessage);
        await WaitForQueuedNotificationsAsync(router, queued + 2);

        sentinel.SuppressReply = null;
        var firstQueryConnection = sentinel.ReceivedConnectionIds[^1];
        await sentinel.SendRawAsync(AddressReply(original.Port), firstQueryConnection);
        await WaitForEndpointAsync(client, promoted.Port);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 2);
    }

    [Test]
    public async Task DuplicateSwitchNotificationRetriesFailedRediscovery()
    {
        await using var original = Primary();
        await using var unavailable = new FakeRespServer(8, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null,
        };
        await using var recovered = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        Volatile.Write(ref port, unavailable.Port);
        sentinel.SuppressReply = command => command == discovery;
        var switchText = $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {unavailable.Port}";
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master", switchText);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master", switchText);
        // The duplicate must arrive while the first discovery is still blocked.
        await WaitForQueuedNotificationsAsync(router, queued + 2);
        Volatile.Write(ref port, recovered.Port);
        sentinel.SuppressReply = null;

        var queryConnection = sentinel.ReceivedConnectionIds[^1];
        await sentinel.SendRawAsync(AddressReply(unavailable.Port), queryConnection);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 2);
        await WaitForEndpointAsync(client, recovered.Port);
        await Assert.That(client.Endpoint.Port).IsEqualTo(recovered.Port);
    }

    [Test]
    public async Task DuplicateSwitchNotificationDoesNotRepeatSuccessfulRediscovery()
    {
        await using var original = Primary();
        await using var recovered = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var switchText = $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {recovered.Port}";
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);
        sentinel.SuppressReply = command => command == discovery;

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master", switchText);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master", switchText);
        await WaitForQueuedNotificationsAsync(router, queued + 2);

        Volatile.Write(ref port, recovered.Port);
        sentinel.SuppressReply = null;
        var queryConnection = sentinel.ReceivedConnectionIds[^1];
        await sentinel.SendRawAsync(AddressReply(recovered.Port), queryConnection);
        await WaitForEndpointAsync(client, recovered.Port);
        await Task.Delay(100);
        await Assert.That(sentinel.ReceivedCommands.Count(command => command == discovery))
            .IsEqualTo(initialDiscoveries + 2);
    }

    [Test]
    public async Task StaleSwitchSourceDoesNotRetireCurrentPrimary()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        sentinel.SuppressReply = command => command == discovery;
        var router = client.Core.Sentinel!;
        var queued = QueuedNotificationCount(router);

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 1 127.0.0.1 {promoted.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(original.Port);

        Volatile.Write(ref port, original.Port);
        sentinel.SuppressReply = null;
        var queryConnection = sentinel.ReceivedConnectionIds[^1];
        await sentinel.SendRawAsync(AddressReply(original.Port), queryConnection);
    }

    [Test]
    public async Task PendingSwitchRetiresTheMatchingPrimaryWithoutWaitingForActiveDiscovery()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var router = client.Core.Sentinel!;
        var current = router.Current!;
        var queued = QueuedNotificationCount(router);
        sentinel.SuppressReply = command => command == discovery;

        // A master-down hint starts a discovery that stays blocked in Sentinel.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown", $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        // The switch waits behind that discovery, but its source matches Current directly.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {promoted.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 2);

        await Assert.That(current.IsRetired).IsTrue();

        Volatile.Write(ref port, promoted.Port);
        sentinel.SuppressReply = null;
        var queryIndex = sentinel.ReceivedCommands.ToList().FindLastIndex(command => command == discovery);
        await sentinel.SendRawAsync(AddressReply(promoted.Port), sentinel.ReceivedConnectionIds[queryIndex]);
        await WaitForEndpointAsync(client, promoted.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ResolvedSwitchSourceRetiresOnlyTheGenerationCurrentWhenItArrived(bool sameGeneration)
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var arrivedDuring = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("127.0.0.1", 6380)] });
        // A later failover back to the same endpoint publishes a different generation.
        await using var republished = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("127.0.0.1", 6380)] });
        var current = sameGeneration ? arrivedDuring : republished;
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var hint = SentinelHintBuilder.Create("switch", new RespireEndpoint("127.0.0.1", 6381), new RespireEndpoint("127.0.0.1", 6380));
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter)
            .GetField("_coalescer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        coalescer.Offer(in hint, targetIsCurrent: false);
        await ((Task)resolve.Invoke(router, [hint, arrivedDuring, CancellationToken.None, null])!).WaitAsync(Limit);

        await Assert.That(current.IsRetired).IsEqualTo(sameGeneration);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task DelayedSourceResolutionKeepsNewerFailbackEvidence(bool discoveryCompleted, bool failbackArrives)
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var current = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("old-primary.invalid", 6379)] });
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);
        var addresses = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.HostResolver = (_, token) => addresses.Task.WaitAsync(token);
        var hint = SentinelHintBuilder.Create("switch-out", new("127.0.0.1", 6380), current.Endpoint);
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter)
            .GetField("_coalescer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        coalescer.Offer(in hint, targetIsCurrent: false);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task)resolve.Invoke(router, [hint, current, CancellationToken.None, null])!;
        var failback = SentinelHintBuilder.Create("switch-back", current.Endpoint, new("127.0.0.1", 6380), MustRediscover: true);
        if (failbackArrives) coalescer.Offer(in failback, targetIsCurrent: true);
        if (discoveryCompleted) coalescer.Complete();
        addresses.SetResult([IPAddress.Loopback]);
        await pending.WaitAsync(Limit);
        await Assert.That(current.IsRetired).IsEqualTo(!failbackArrives);
        if (discoveryCompleted && failbackArrives) await Assert.That(coalescer.Active).IsNull();
        else if (failbackArrives) await Assert.That(coalescer.Pending!.Value.Targets).Contains(current.Endpoint);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DelayedSourceResolutionRetainsSourceAfterDeliveryGap(bool discoveryCompleted)
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var current = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("old-primary.invalid", 6379)] });
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);
        var addresses = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.HostResolver = (_, token) => addresses.Task.WaitAsync(token);
        var hint = SentinelHintBuilder.Create("switch", new("127.0.0.1", 6380), current.Endpoint);
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter)
            .GetField("_coalescer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        coalescer.Offer(in hint, targetIsCurrent: false);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var pending = (Task)resolve.Invoke(router, [hint, current, CancellationToken.None, null])!;
        coalescer.Complete();
        coalescer.Offer(SentinelHintBuilder.Create("gap", MustRediscover: true), targetIsCurrent: false);
        if (discoveryCompleted) coalescer.Complete();
        addresses.SetResult([IPAddress.Loopback]);
        await pending.WaitAsync(Limit);
        await Assert.That(current.IsRetired).IsTrue();
    }

    [Test]
    [NotInParallel]
    public async Task RetiredSwitchSourceResolutionQueuesFreshDiscoveryBehindActiveHint()
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var current = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("old-primary.invalid", 6379)] });
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        var hint = SentinelHintBuilder.Create("switch", new("127.0.0.1", 6380), current.Endpoint);
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter)
            .GetField("_coalescer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(router)!;
        coalescer.Offer(in hint, targetIsCurrent: false);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await ((Task)resolve.Invoke(router, [hint, current, CancellationToken.None, null])!).WaitAsync(Limit);

        await Assert.That(current.IsRetired).IsTrue();
        await Assert.That(coalescer.Pending).IsNotNull();
        await Assert.That(coalescer.Pending!.Value.MustRediscover).IsTrue();
    }

    [Test]
    public async Task DisposalJoinsAPendingSwitchSourceResolution()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        await using var sentinel = Sentinel(() => original.Port);
        var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Ignores cancellation, as a resolver stuck in the OS can.
        router.HostResolver = (_, _) => { resolving.TrySetResult(); return release.Task; };
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];

        // A host name source cannot match the current endpoint textually, so it is resolved in the background.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster old-primary.invalid {original.Port} 127.0.0.1 {promoted.Port}");
        await resolving.Task.WaitAsync(Limit);
        // The router registers the resolution under its gate before the resolver can run, so a
        // disposal snapshot can never miss a started resolution. No wait is needed here.
        await Assert.That(router.PendingSwitchSourceResolutions).IsEqualTo(1);

        var disposal = client.DisposeAsync().AsTask();
        await Task.Delay(200);
        await Assert.That(disposal.IsCompleted).IsFalse();
        release.TrySetResult([IPAddress.Loopback]);
        await disposal.WaitAsync(Limit);
        await Assert.That(router.PendingSwitchSourceResolutions).IsEqualTo(0);
    }

    [Test]
    public async Task MonitorReconnectCapturesFreshRearmAfterEarlierHealthyPublication()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);

        var router = client.Core.Sentinel!;
        var previousEpoch = router.CurrentMonitorRearm();
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {promoted.Port}");
        await WaitForEndpointAsync(client, promoted.Port);

        await Assert.That(previousEpoch.IsCompleted).IsTrue();
        var reconnectEpoch = router.CurrentMonitorRearm();
        await Assert.That(ReferenceEquals(reconnectEpoch, previousEpoch)).IsFalse();
        await Assert.That(reconnectEpoch.IsCompleted).IsFalse();
    }

    [Test]
    public async Task ExhaustedMonitorResumesAfterTheNextPublication()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        using var unreachable = new ReservedUnavailablePort();
        var deadPort = unreachable.Port;
        var logger = new MonitorExhaustionLogger(deadPort);
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            Endpoints = [new("127.0.0.1", sentinel.Port), new("127.0.0.1", deadPort)],
            LoggerFactory = logger,
            ReconnectPolicy = new()
            {
                InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10),
                JitterRatio = 0, MaxAttempts = 1,
            },
        });
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await logger.WaitForExhaustionsAsync(1);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];

        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {promoted.Port}");
        await WaitForEndpointAsync(client, promoted.Port);

        // The publication grants the parked monitor a fresh budget, which it spends and exhausts again.
        await logger.WaitForExhaustionsAsync(2);
        await Assert.That(logger.Resumptions).IsGreaterThanOrEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MonitorResumesWhenAPublicationLandedDuringItsFinalRetry(bool closesBeforeSubscriptionAck)
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        using var refused = closesBeforeSubscriptionAck ? null : new ReservedUnavailablePort();
        await using var unreachable = closesBeforeSubscriptionAck ? new FakeRespServer(64, FakeRespServer.OkReply) : null;
        var deadPort = refused?.Port ?? unreachable!.Port;
        if (unreachable is not null)
            unreachable.ReplyOverride = (id, command) =>
            {
                if (command.StartsWith("SUBSCRIBE ", StringComparison.Ordinal)) unreachable.CloseConnection(id);
                return FakeRespServer.OkReply;
            };
        var logger = new MonitorExhaustionLogger(deadPort);
        // A distinctive delay identifies the dead monitor's retry timer on the gated clock.
        var retryDelay = TimeSpan.FromSeconds(7);
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            Endpoints = [new("127.0.0.1", sentinel.Port), new("127.0.0.1", deadPort)],
            LoggerFactory = logger,
            ReconnectPolicy = new()
            {
                InitialDelay = retryDelay, MaxDelay = retryDelay, JitterRatio = 0, MaxAttempts = 1,
            },
        });
        var router = client.Core.Sentinel!;
        var clock = new FenceClock();
        router.Clock = clock;
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel, waitForRediscovery: false);
        // The dead monitor's first subscription failed; its one retry waits on the gated clock.
        var retry = await ReadFenceTimerAsync(clock, retryDelay);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];

        // A primary is published before the dead monitor exhausts its budget and parks.
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {promoted.Port}");
        await WaitForEndpointAsync(client, promoted.Port);
        await Assert.That(logger.Exhaustions).IsEqualTo(0);
        retry.Fire();

        // That publication already granted the fresh budget, so the monitor does not wait for another.
        await logger.WaitForResumptionsAsync(1);
        await Assert.That(logger.Exhaustions).IsEqualTo(1);
        await ReadFenceTimerAsync(clock, retryDelay);
    }

    [Test]
    public async Task RepeatedNotificationRediscoveryFailuresWarnOnlyOnce()
    {
        await using var original = Primary();
        await using var unavailable = new FakeRespServer(64, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null,
        };
        await using var recovered = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var logger = new RediscoveryLogger();
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            LoggerFactory = logger,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        });
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        Volatile.Write(ref port, unavailable.Port);

        // Every rediscovery fails ROLE validation until Sentinel names a real primary.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {unavailable.Port}");
        using (var timeout = new CancellationTokenSource(Limit))
            while (logger.Failures.Count < 3) await Task.Delay(5, timeout.Token);
        Volatile.Write(ref port, recovered.Port);
        await WaitForEndpointAsync(client, recovered.Port);
        using (var timeout = new CancellationTokenSource(Limit))
            while (logger.Recoveries == 0) await Task.Delay(5, timeout.Token);

        var levels = logger.Failures.ToArray();
        await Assert.That(levels[0]).IsEqualTo(Microsoft.Extensions.Logging.LogLevel.Warning);
        await Assert.That(levels.Skip(1).All(level => level == Microsoft.Extensions.Logging.LogLevel.Debug)).IsTrue();
    }

    [Test]
    public async Task PendingHintsDoNotResetNotificationRediscoveryFailureBudget()
    {
        await using var original = Primary();
        await using var unavailable = Primary((_, command) => command == "ROLE"
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        unavailable.DelayCommand("ROLE", 100);
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            ReconnectPolicy = new()
            {
                InitialDelay = TimeSpan.FromHours(1), MaxDelay = TimeSpan.FromHours(1), JitterRatio = 0,
                MaxAttempts = 1,
            },
        });
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref port, unavailable.Port);

        var router = client.Core.Sentinel!;
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("initial-hint", MustRediscover: true));
        await WaitForCommandCountAsync(unavailable, "ROLE", 1);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("pending-hint-1", MustRediscover: true));
        await WaitForCommandCountAsync(unavailable, "ROLE", 2);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("pending-hint-2", MustRediscover: true));

        var rediscovery = router.NotificationRediscovery;
        await Assert.That(rediscovery).IsNotNull();
        await rediscovery!.WaitAsync(Limit);
        await Assert.That(unavailable.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(2);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LoggerFailureMetricsCannotInterruptSentinelRecovery(bool throwingListener)
    {
        var failures = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.guarded_logging.failures")
                owner.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
        {
            Interlocked.Add(ref failures, value);
            if (throwingListener) throw new InvalidOperationException("Metrics listener failed");
        });
        listener.Start();
        await using var original = Primary();
        await using var promoted = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        var logger = new RediscoveryLogger { ThrowOnSentinelLog = true };
        await using var client = RespireClient.Create(Options(sentinel.Port) with { LoggerFactory = logger });
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await Assert.That(Volatile.Read(ref failures)).IsGreaterThan(0);
        var beforeEvent = Volatile.Read(ref failures);
        var monitor = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        Volatile.Write(ref port, promoted.Port);
        await SendSentinelMessageAsync(sentinel, sentinel.ReceivedConnectionIds[monitor], "+sdown",
            $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForEndpointAsync(client, promoted.Port);
        await Assert.That(Volatile.Read(ref failures)).IsGreaterThan(beforeEvent);
    }

    private sealed class RediscoveryLogger : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        private int _recoveries;
        internal Action? OnRecovery { get; set; }
        internal bool ThrowOnSentinelLog { get; init; }
        internal ConcurrentQueue<Microsoft.Extensions.Logging.LogLevel> Failures { get; } = new();
        internal int Recoveries => Volatile.Read(ref _recoveries);
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (ThrowOnSentinelLog && message.StartsWith("Sentinel ", StringComparison.Ordinal))
                throw new InvalidOperationException("Sentinel logger failed");
            if (message.StartsWith("Sentinel notification-triggered primary discovery failed", StringComparison.Ordinal))
                Failures.Enqueue(level);
            else if (message.StartsWith("Sentinel notification-triggered primary discovery succeeded after", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _recoveries);
                OnRecovery?.Invoke();
            }
        }
    }

    [Test]
    public async Task NotificationReconciliationCannotOverwriteInterveningPublication()
    {
        await using var original = Primary();
        var ready = false;
        await using var promoted = Primary((_, command) => command == "ROLE" && !Volatile.Read(ref ready)
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        var freshPort = original.Port;
        var stalePort = original.Port;
        await using var fresh = Sentinel(() => Volatile.Read(ref freshPort));
        await using var stale = Sentinel(() => Volatile.Read(ref stalePort));
        await using var alternate = Sentinel(() => Volatile.Read(ref stalePort));
        foreach (var sentinel in new[] { fresh, stale, alternate })
        {
            var reply = sentinel.ReplyOverride!;
            sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
                ? "-NOPERM metadata unavailable\r\n"u8.ToArray() : reply(id, command);
        }
        var logger = new RediscoveryLogger();
        await using var client = RespireClient.Create(Options(fresh.Port) with
        {
            Endpoints = [new("127.0.0.1", fresh.Port), new("127.0.0.1", stale.Port), new("127.0.0.1", alternate.Port)],
            LoggerFactory = logger,
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        });
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, fresh);
        await WaitForInitialSentinelValidationAsync(client, stale);
        await WaitForInitialSentinelValidationAsync(client, alternate);
        var router = client.Core.Sentinel!;
        using var release = new ManualResetEventSlim();
        var revalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        logger.OnRecovery = () =>
        {
            revalidated.TrySetResult();
            if (!release.Wait(Limit)) throw new TimeoutException("Reconciliation was not released");
        };
        Volatile.Write(ref freshPort, promoted.Port);
        Volatile.Write(ref stalePort, promoted.Port);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("switch", new("127.0.0.1", promoted.Port),
            new("127.0.0.1", original.Port), ReportingSentinel: new("127.0.0.1", fresh.Port),
            AdditionalReportingSentinels: [new("127.0.0.1", stale.Port), new("127.0.0.1", alternate.Port)]));
        var worker = router.NotificationRediscovery!;
        try
        {
            using var deadline = new CancellationTokenSource(Limit);
            while (logger.Failures.IsEmpty) await Task.Delay(5, deadline.Token);
            Volatile.Write(ref ready, true);
            await revalidated.Task.WaitAsync(Limit);
            Volatile.Write(ref freshPort, original.Port);
            var failback = await router.GetGenerationAsync(deadline.Token, forceDiscovery: true);
            await Assert.That(failback.Endpoint.Port).IsEqualTo(original.Port);
            release.Set();
            await worker.WaitAsync(Limit);
            await Assert.That(router.Current).IsSameReferenceAs(failback);
        }
        finally { release.Set(); }
    }

    private sealed class MonitorExhaustionLogger(int port) : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        private int _exhaustions;
        private int _resumptions;
        private readonly ConcurrentQueue<string> _events = new();
        internal int Resumptions => Volatile.Read(ref _resumptions);
        internal int Exhaustions => Volatile.Read(ref _exhaustions);
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level >= Microsoft.Extensions.Logging.LogLevel.Information;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.Contains(":" + port, StringComparison.Ordinal)) return;
            _events.Enqueue(exception is null ? message : $"{message}: {exception}");
            if (message.StartsWith("Sentinel event monitor exhausted reconnect attempts", StringComparison.Ordinal))
                Interlocked.Increment(ref _exhaustions);
            else if (message.StartsWith("Sentinel event monitor at", StringComparison.Ordinal)
                && message.Contains("resumes", StringComparison.Ordinal))
                Interlocked.Increment(ref _resumptions);
        }

        internal async Task WaitForExhaustionsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { while (Volatile.Read(ref _exhaustions) < count) await Task.Delay(10, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { throw new TimeoutException($"Expected {count} exhaustions; observed {Exhaustions}. Events: {string.Join(Environment.NewLine, _events)}"); }
        }

        internal async Task WaitForResumptionsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { while (Volatile.Read(ref _resumptions) < count) await Task.Delay(10, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            { throw new TimeoutException($"Expected {count} resumptions; observed {Resumptions}, exhaustions {Exhaustions}. Events: {string.Join(Environment.NewLine, _events)}"); }
        }
    }

    [Test]
    public async Task MasterDownForHealthyPrimaryRevalidatesOnExistingConnection()
    {
        await using var original = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +switch-master");
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var roles = original.ReceivedCommands.Count(command => command == "ROLE");
        var connections = original.ReceivedConnectionIds.Distinct().Count();
        var generation = client.Core.Sentinel!.Current;

        // Sentinel still reports the same primary, so no candidate connection should be opened.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown", $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        await WaitForCommandCountAsync(original, "ROLE", roles + 1);
        await client.PingAsync().AsTask().WaitAsync(Limit);

        await Assert.That(original.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(connections);
        await Assert.That(client.Core.Sentinel!.Current).IsSameReferenceAs(generation);
        await Assert.That(generation!.IsRetired).IsFalse();
    }

    [Test]
    public async Task SwitchSourceMatchesCurrentPrimaryByConnectedAddress()
    {
        await using var original = Primary();
        await using var promoted = Primary();
        var host = "localhost";
        var port = original.Port;
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(Volatile.Read(ref host), Volatile.Read(ref port))
                : command == "SUBSCRIBE +switch-master +sdown +odown"
                    ? "*3\r\n$9\r\nsubscribe\r\n$14\r\n+switch-master\r\n:1\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+sdown\r\n:2\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+odown\r\n:3\r\n"u8.ToArray()
                    : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var current = router.Current!;
        await Assert.That(current.Endpoint.Host).IsEqualTo("localhost");
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var discoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var queued = QueuedNotificationCount(router);
        sentinel.SuppressReply = command => command == discovery;

        // Sentinel announces the old primary by IP while this client reached it by hostname.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {promoted.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        await WaitForCommandCountAsync(sentinel, discovery, discoveries + 1);
        using var retiredTimeout = new CancellationTokenSource(Limit);
        while (!current.IsRetired) await Task.Delay(10, retiredTimeout.Token);
        await Assert.That(current.IsRetired).IsTrue();

        Volatile.Write(ref host, "127.0.0.1");
        Volatile.Write(ref port, promoted.Port);
        sentinel.SuppressReply = null;
        var queryIndex = sentinel.ReceivedCommands.ToList().FindLastIndex(command => command == discovery);
        await sentinel.SendRawAsync(AddressReply(promoted.Port), sentinel.ReceivedConnectionIds[queryIndex]);
        await WaitForEndpointAsync(client, promoted.Port);
    }

    [Test]
    public async Task HostnameSourceFencesItsConnectedPeerBeforeAnyDnsLookup()
    {
        await using var original = Primary();
        await using var target = Primary();
        var reportAlias = false;
        await using var sentinel = Sentinel(() => original.Port);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command == "SENTINEL MASTER mymaster"
            ? "-NOPERM configuration metadata denied\r\n"u8.ToArray()
            : command.StartsWith("SENTINEL GET-MASTER-ADDR")
                ? AddressReply(Volatile.Read(ref reportAlias) ? "127.0.0.1" : "localhost", original.Port)
                : reply(id, command);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var originalGeneration = router.Current!;
        var roles = original.ReceivedCommands.Count(command => command == "ROLE");
        router.HostResolver = (_, _) => throw new InvalidOperationException("The connected peer needs no DNS lookup.");
        var monitorIndex = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var queued = QueuedNotificationCount(router);
        Volatile.Write(ref reportAlias, true);
        await SendSentinelMessageAsync(sentinel, sentinel.ReceivedConnectionIds[monitorIndex], "+switch-master",
            $"mymaster localhost {original.Port} 127.0.0.1 {target.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        await Assert.That(originalGeneration.IsRetired).IsTrue();
        await Assert.That(router.Current).IsSameReferenceAs(originalGeneration);
        await Assert.That(original.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(roles);
    }

    [Test]
    public async Task ResolvedHostnameTargetSurvivesFailedCycleRediscovery()
    {
        await using var current = Primary();
        await using var other = Primary();
        await using var sentinel = Sentinel(() => current.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, JitterRatio = 0 },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var generation = router.Current!;
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? "-ERR discovery unavailable\r\n"u8.ToArray() : reply(id, command);
        var hostname = new RespireEndpoint("current.internal", current.Port);
        var different = new RespireEndpoint("127.0.0.1", other.Port);
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("cycle", [hostname, different],
            [new(hostname, ["127.0.0.1"]), new(different, null)], [new("127.0.0.1", sentinel.Port)], true));
        if (router.NotificationRediscovery is { } worker) await worker.WaitAsync(Limit);
        await Assert.That(generation.IsRetired).IsFalse();
        await Assert.That(router.Current).IsSameReferenceAs(generation);
        await client.PingAsync().AsTask().WaitAsync(Limit);
    }

    [Test]
    [Arguments(false, true)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(true, false)]
    public async Task ResolvedSwitchSourceDistinguishesTargetPeerEvidence(bool literalSource, bool targetIsCurrent)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var current = router.Current!;
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? "-ERR discovery unavailable\r\n"u8.ToArray() : reply(id, command);
        router.HostResolver = (host, _) => Task.FromResult<IPAddress[]>(
            [host == "promoted.internal" && !targetIsCurrent ? IPAddress.Parse("192.0.2.2") : IPAddress.Loopback]);
        var hint = SentinelHint.FromSwitchMaster("switch", new(literalSource ? "127.0.0.1" : "former.internal", primary.Port),
            new("promoted.internal", primary.Port), new("127.0.0.1", sentinel.Port));
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)resolve.Invoke(router, [hint, current, CancellationToken.None, null])!).WaitAsync(Limit);
        // The established peer is the old owner even when both fresh DNS names alias it.
        // Their overlap must not erase evidence against this known physical connection.
        await Assert.That(current.IsRetired).IsTrue();
        await Assert.That(router.Current).IsSameReferenceAs(current);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task TargetConnectionCannotPublishDemotedPeerAfterDnsChanges(bool reachesDemotedPeer)
    {
        await using var primary = Primary();
        var announceHostname = false;
        await using var sentinel = Sentinel(() => primary.Port, () => announceHostname ? 2 : 1);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) =>
        {
            var response = reply(id, command);
            return announceHostname && (command.StartsWith("SENTINEL GET-MASTER") || command == "SENTINEL MASTER mymaster")
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response!).Replace("127.0.0.1", "localhost"))
                : response;
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        announceHostname = true;
        var demotedAddress = reachesDemotedPeer ? "127.0.0.1" : "192.0.2.1";
        if (!reachesDemotedPeer)
            typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.SetValue(original.Multiplexer.GetConnection(), demotedAddress);
        // Discovery sees the promoted address; the actual localhost connection still reaches
        // the former primary, which continues to answer ROLE master during split brain.
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>(reachesDemotedPeer
            ? [IPAddress.Parse("192.0.2.2")]
            : [IPAddress.Parse(demotedAddress), IPAddress.Loopback]);
        var hint = SentinelHint.FromSwitchMaster("switch", new(demotedAddress, primary.Port), new("localhost", primary.Port),
            new("127.0.0.1", sentinel.Port));
        var accepted = false;
        try
        {
            await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true, notificationHint: hint);
            accepted = true;
        }
        catch (RespireConnectionException) { }
        await Assert.That(accepted).IsEqualTo(!reachesDemotedPeer);
        if (reachesDemotedPeer) await Assert.That(router.Current).IsSameReferenceAs(original);
        else await Assert.That(router.Current!.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task CandidateChecksEveryValidatedSocketAgainstSwitchSource(bool secondSocket, bool includesSource)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        var options = Options(sentinel.Port);
        await using var client = await RespireClient.ConnectAsync(options);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        await using var candidate = new SentinelRouter.Generation(router, client.Core, options with
        {
            Endpoints = [new("localhost", primary.Port)],
            SentinelPrimaryName = null,
            Connections = 2,
        });
        await candidate.Multiplexer.EnsureConnectedAsync(CancellationToken.None);
        var first = candidate.Multiplexer.GetConnection();
        var second = candidate.Multiplexer.GetConnection();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
        var changed = secondSocket ? second : first;
        var last = secondSocket ? first : second;
        // Model mixed DNS peers after both sockets answer ROLE master. The last validated
        // socket is the promoted peer, so checking only ValidatedPeer misses the source.
        typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(changed, includesSource ? "192.0.2.1" : "192.0.2.2");
        await candidate.ValidateAsync(last, CancellationToken.None);
        await Assert.That(candidate.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
        var hint = SentinelHint.FromSwitchMaster("switch", new("192.0.2.1", primary.Port),
            candidate.Endpoint, new("127.0.0.1", sentinel.Port));
        var validate = typeof(SentinelRouter).GetMethod("ValidateSwitchTargetPeer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Exception? rejection = null;
        try { validate.Invoke(router, [candidate, hint]); }
        catch (System.Reflection.TargetInvocationException error) { rejection = error.InnerException; }
        await Assert.That(rejection is RespireConnectionException).IsEqualTo(includesSource);
        if (!includesSource) await Assert.That(rejection).IsNull();
        await Assert.That(candidate.IsRetired).IsEqualTo(includesSource);
        await Assert.That(router.Current).IsSameReferenceAs(original);
        await Assert.That(original.IsRetired).IsFalse();
    }

    [Test]
    public async Task SourceDnsMovingToFutureTargetDoesNotFenceItsPublication()
    {
        await using var primary = Primary();
        var promoted = false;
        await using var sentinel = Sentinel(() => primary.Port, () => promoted ? 2 : 1);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) =>
        {
            var response = reply(id, command);
            return command.StartsWith("SENTINEL GET-MASTER") || command == "SENTINEL MASTER mymaster"
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response!).Replace("127.0.0.1", "localhost"))
                : response;
        };
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var original = router.Current!;
        // The established socket is the former peer. Both hostnames now resolve to the
        // promoted peer, so their DNS overlap cannot identify that peer as the old owner.
        typeof(RespireConnection).GetField("_networkPeerAddress", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(original.Multiplexer.GetConnection(), "192.0.2.1");
        router.HostResolver = (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]);
        promoted = true;
        var hint = SentinelHint.FromSwitchMaster("switch", new("former.internal", primary.Port),
            new("localhost", primary.Port), new("127.0.0.1", sentinel.Port));
        var coalescer = (SentinelNotificationCoalescer)typeof(SentinelRouter).GetField("_coalescer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(router)!;
        coalescer.Offer(hint, false);
        var resolve = typeof(SentinelRouter).GetMethod("ResolveAndRetireSwitchSourceAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)resolve.Invoke(router, [hint, original, CancellationToken.None, null])!).WaitAsync(Limit);
        var selected = await router.GetGenerationAsync(CancellationToken.None, forceDiscovery: true,
            notificationHint: coalescer.Active);
        await Assert.That(selected.ValidatedPeer!.Value.Host).IsEqualTo("127.0.0.1");
        await Assert.That(ReferenceEquals(selected, original)).IsFalse();
        await Assert.That(router.Current).IsSameReferenceAs(selected);
    }

    [Test]
    public async Task RetiredArrivalGenerationStillResolvesLaterSwitchSource()
    {
        await using var original = Primary();
        await using var middle = Primary();
        await using var final = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);

        var router = client.Core.Sentinel!;
        var originalGeneration = router.Current!;
        var resolving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResolution = new TaskCompletionSource<IPAddress[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.HostResolver = (host, _) =>
        {
            if (host == "middle-primary.invalid") resolving.TrySetResult();
            return releaseResolution.Task;
        };
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        var queued = QueuedNotificationCount(router);
        sentinel.SuppressReply = command => command == discovery;

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster 127.0.0.1 {original.Port} 127.0.0.1 {middle.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        using var originalRetirement = new CancellationTokenSource(Limit);
        while (!originalGeneration.IsRetired) await Task.Delay(10, originalRetirement.Token);

        await SendSentinelMessageAsync(sentinel, monitorConnection, "+switch-master",
            $"mymaster middle-primary.invalid {middle.Port} 127.0.0.1 {final.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 2);
        await resolving.Task.WaitAsync(Limit);

        port = middle.Port;
        var firstQuery = sentinel.ReceivedCommands.ToList().FindLastIndex(command => command == discovery);
        await sentinel.SendRawAsync(AddressReply(middle.Port), sentinel.ReceivedConnectionIds[firstQuery]);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 2);
        await sentinel.SendRawAsync(AddressReply(middle.Port), sentinel.ReceivedConnectionIds[firstQuery]);
        await WaitForEndpointAsync(client, middle.Port);
        var middleGeneration = router.Current!;
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 3);

        releaseResolution.TrySetResult([IPAddress.Loopback]);
        using var middleRetirement = new CancellationTokenSource(Limit);
        while (!middleGeneration.IsRetired) await Task.Delay(10, middleRetirement.Token);
        await Assert.That(middleGeneration.IsRetired).IsTrue();
    }

    [Test]
    public async Task SubscriptionGapTriggersRediscovery()
    {
        await using var original = Primary();
        var port = original.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), MaxDelay = TimeSpan.FromMilliseconds(10), JitterRatio = 0 },
        });
        var router = client.Core.Sentinel!;
        var discovery = "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster";
        var queuedBeforeDiscovery = QueuedNotificationCount(router);
        var discoveriesBeforeClientStart = sentinel.ReceivedCommands.Count(command => command == discovery);
        await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +switch-master");
        await WaitForQueuedNotificationsAsync(router, queuedBeforeDiscovery + 1);
        await WaitForCommandCountAsync(sentinel, discovery, discoveriesBeforeClientStart + 2);
        var monitorCommand = sentinel.ReceivedCommands.ToList()
            .FindIndex(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));
        var monitorConnection = sentinel.ReceivedConnectionIds[monitorCommand];
        var queued = QueuedNotificationCount(router);
        var initialDiscoveries = sentinel.ReceivedCommands.Count(command => command == discovery);
        // Prove the subscription is established: a delivered event reaches the router and its
        // discovery (still the same primary) completes before the connection is dropped.
        await SendSentinelMessageAsync(sentinel, monitorConnection, "+sdown", $"master mymaster 127.0.0.1 {original.Port}");
        await WaitForQueuedNotificationsAsync(router, queued + 1);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 1);
        await WaitForCommandCountAsync(original, "ROLE", 2);
        var subscriptions = sentinel.ReceivedCommands.Count(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal));

        // Events published while the monitor reconnects are lost, so the resubscription must rediscover.
        sentinel.CloseConnections();
        await WaitForQueuedNotificationsAsync(router, queued + 2);
        await WaitForCommandCountAsync(sentinel, discovery, initialDiscoveries + 2);

        await Assert.That(sentinel.ReceivedCommands.Count(command => command.StartsWith("SUBSCRIBE +switch-master", StringComparison.Ordinal)))
            .IsGreaterThan(subscriptions);
        await client.PingAsync().AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(original.Port);
    }

    [Test]
    [Arguments("unused")]
    [Arguments("failed-validation")]
    [Arguments("published")]
    public async Task DisposalReportsOnlyPublishedDataEndpoints(string state)
    {
        await using var primary = Primary((_, command) => command == "ROLE" && state == "failed-validation"
            ? "*0\r\n"u8.ToArray() : null);
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        if (state == "failed-validation")
            await Assert.That(async () => await client.PingAsync().AsTask().WaitAsync(Limit)).Throws<RespireConnectionException>();
        else if (state == "published")
        {
            await client.PingAsync().AsTask().WaitAsync(Limit);
            // Publication starts the event monitor and its revalidation. Let that Sentinel traffic
            // settle, so commands sent before disposal cannot be counted after it.
            await WaitForInitialSentinelValidationAsync(client, sentinel);
        }
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.Unspecified && change.State == RespireConnectionState.Disconnected)
                changes.Enqueue(change);
        };
        var discoveryCommands = sentinel.CommandsSeen;
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        var commandsAfterDisposal = sentinel.CommandsSeen;
        await Task.Delay(100);
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(commandsAfterDisposal);
        if (state != "published")
            await Assert.That(commandsAfterDisposal).IsEqualTo(discoveryCommands);
        await Assert.That(changes.Any(change => change.Endpoint.Port == sentinel.Port)).IsFalse();
        await Assert.That(changes.Select(change => change.Endpoint).ToArray()).IsEquivalentTo(
            state == "published" ? new[] { new RespireEndpoint("127.0.0.1", primary.Port) } : []);
        if (state == "unused")
        {
            await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
            await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        }
    }

    [Test]
    [NotInParallel]
    public async Task EndpointSnapshotsNeverMixPublishedGenerations()
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var first = new Respire.Internal.SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("first.invalid", 6379)] });
        await using var second = new Respire.Internal.SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("second.invalid", 6380)] });
        var current = typeof(Respire.Internal.SentinelRouter).GetField("_current",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        // Isolate the atomic publication boundary without connecting to synthetic endpoints.
        current.SetValue(router, first);
        var stop = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = Task.Factory.StartNew(() =>
        {
            started.TrySetResult();
            while (Volatile.Read(ref stop) == 0)
            {
                current.SetValue(router, second);
                current.SetValue(router, first);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        RespireEndpoint? mixed = null;
        try
        {
            await started.Task.WaitAsync(Limit);
            for (var index = 0; index < 1_000_000; index++)
            {
                var endpoint = client.Endpoint;
                if (endpoint != first.Endpoint && endpoint != second.Endpoint)
                {
                    mixed = endpoint;
                    break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            await publisher.WaitAsync(Limit);
            current.SetValue(router, null);
        }
        await Assert.That(mixed).IsNull();
    }

    [Test]
    [NotInParallel]
    public async Task StaleQueuedSwitchSourceCannotRetireNewGeneration()
    {
        await using var client = RespireClient.Create(Options(26379));
        var router = client.Core.Sentinel!;
        await using var old = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("old.invalid", 6379)] });
        await using var current = new SentinelRouter.Generation(router, client.Core,
            Options(26379) with { Endpoints = [new("current.invalid", 6380)] });
        typeof(SentinelRouter).GetField("_current", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!.SetValue(router, current);

        // The switch was parsed from the old generation before current was published.
        router.QueueNotificationRediscovery(SentinelHintBuilder.Create("stale-switch", OldPrimary: old.Endpoint));

        await Assert.That(current.IsRetired).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task RapidFailoversDoNotWaitForBlockedObserversAndDrainNotificationsInOrder()
    {
        const int handoffs = 12;
        static byte[]? Reply(int _, string command) => command.StartsWith("SET retire", StringComparison.Ordinal)
            ? "-READONLY replica\r\n"u8.ToArray() : null;
        await using var first = Primary(Reply);
        await using var second = Primary(Reply);
        var port = first.Port;
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(Volatile.Read(ref port))
                : command == "SUBSCRIBE +switch-master +sdown +odown"
                    ? "*3\r\n$9\r\nsubscribe\r\n$14\r\n+switch-master\r\n:1\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+sdown\r\n:2\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+odown\r\n:3\r\n"u8.ToArray()
                    : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endpoints = new ConcurrentQueue<int>();
        var measurements = 0L;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && (Equals(tag.Value, first.Port) || Equals(tag.Value, second.Port)))
                    Interlocked.Add(ref measurements, value);
        });
        listener.Start();
        client.ConnectionStateChanged += change =>
        {
            if (change.State != RespireConnectionState.Connected) return;
            endpoints.Enqueue(change.Endpoint.Port);
            if (endpoints.Count == 1)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
            if (endpoints.Count == handoffs + 1) drained.TrySetResult();
        };
        try
        {
            await client.SetAsync("initial", "value").AsTask().WaitAsync(Limit);
            await entered.Task.WaitAsync(Limit);
            // The monitor's revalidation could otherwise observe a port flip below and publish an
            // extra generation. It confirms the unchanged primary without waiting for the observer.
            await WaitForInitialSentinelValidationAsync(client, sentinel);
            for (var index = 1; index <= handoffs; index++)
            {
                Volatile.Write(ref port, index % 2 == 0 ? first.Port : second.Port);
                await Assert.That(async () => await client.SetAsync("retire", "value").AsTask().WaitAsync(Limit))
                    .Throws<RespireServerException>();
                await client.SetAsync("current", "value").AsTask().WaitAsync(Limit);
                await Assert.That(client.Endpoint.Port).IsEqualTo(port);
            }
            await Assert.That(endpoints.Count).IsEqualTo(1);
            await Assert.That(Interlocked.Read(ref measurements)).IsEqualTo(0L);
        }
        finally { release.TrySetResult(); }
        await drained.Task.WaitAsync(Limit);
        await Assert.That(endpoints.SequenceEqual(
            Enumerable.Range(0, handoffs + 1).Select(index => index % 2 == 0 ? first.Port : second.Port))).IsTrue();
        await Assert.That(Interlocked.Read(ref measurements)).IsEqualTo((long)handoffs);
    }

    [Test]
    public async Task RoleDemotionRetiresTheGenerationBeforeTheNextWrite()
    {
        var replica = false;
        await using var oldPrimary = Primary((_, command) => command == "ROLE" && Volatile.Read(ref replica)
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref replica, true);
        using (var role = await client.ExecuteAsync($"ROLE"))
            await Assert.That(role[0].AsString()).IsEqualTo("slave");
        await Assert.That(client.IsConnected).IsFalse();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE", "ROLE"]);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task RetirementRejectsAnUnacceptedWaiterAndDrainsAcceptedCommands()
    {
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptedCount = 0;
        await using var oldPrimary = Primary();
        oldPrimary.SuppressReply = command =>
        {
            if (command != "PING") return false;
            if (Interlocked.Increment(ref acceptedCount) == 4) full.TrySetResult();
            return true;
        };
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { MaxInflightCommands = 4 });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Multiplexer.GetConnection();
        var accepted = Enumerable.Range(0, 4).Select(_ => original.SendAsync(
            new Respire.Commands.RawCommand(FakeRespServer.PingFrame), default, armCommandDeadline: false).AsTask()).ToArray();
        await full.Task.WaitAsync(Limit);
        // Pin this waiter to the full old connection before retirement. A public call can
        // still be in async route acquisition and legitimately select the replacement.
        var waiter = original.SendAsync(new Respire.Commands.Cmd2(Respire.Commands.Verbs.Set,
            "unaccepted", "value"), default, armCommandDeadline: false).AsTask();
        await Assert.That(waiter.IsCompleted).IsFalse();
        Volatile.Write(ref primaryPort, promoted.Port);
        await oldPrimary.SendRawAsync("-READONLY replica\r\n"u8.ToArray());
        using (var rejected = await accepted[0].WaitAsync(Limit)) await Assert.That(rejected.IsError).IsTrue();
        try
        {
            await Assert.That(async () => await waiter.WaitAsync(Limit)).Throws<RespireException>();
            await Assert.That(original.IsAcceptingCommands).IsFalse();
            await client.SetAsync("new", "value").AsTask().WaitAsync(Limit);
            await Assert.That(accepted.Skip(1).All(task => !task.IsCompleted)).IsTrue();
            await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE", "PING", "PING", "PING", "PING"]);
            await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET new value"]);
        }
        finally
        {
            await oldPrimary.SendRawAsync("+PONG\r\n+PONG\r\n+PONG\r\n"u8.ToArray());
            foreach (var pending in accepted.Skip(1)) { using var reply = await pending.WaitAsync(Limit); }
        }
    }

    [Test]
    public async Task LazyClientDiscoversOnFirstOperationAndPrefixViewsShareThePrimary()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        await using var prefixed = client.WithKeyPrefix("tenant:");
        await Assert.That(sentinel.CommandsSeen).IsEqualTo(0);
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        await Assert.That(client.IsConnected).IsFalse();
        await prefixed.SetAsync("key", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", primary.Port));
        await Assert.That(prefixed.Endpoint).IsEqualTo(client.Endpoint);
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:key value", "ROLE"]);
    }

    [Test]
    public async Task ReadOnlyRetiresStalePrimaryAndNextWriteUsesTheSameClientCore()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY You can't write against a read only replica.\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var prefixed = client.WithKeyPrefix("tenant:");
        var core = client.Core;
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await prefixed.SetAsync("rejected", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireServerException>();
        await prefixed.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Core).IsSameReferenceAs(core);
        await Assert.That(client.Endpoint).IsEqualTo(new RespireEndpoint("127.0.0.1", promoted.Port));
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET tenant:next value"]);
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
    }

    [Test]
    public async Task DisconnectReResolvesWithoutReplayingAnAcceptedWrite()
    {
        await using var oldPrimary = Primary();
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        oldPrimary.CloseConnectionAfterCommand = oldPrimary.CommandsSeen + 1;
        Volatile.Write(ref primaryPort, promoted.Port);
        await Assert.That(async () => await client.IncrementAsync("ambiguous").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE", "INCR ambiguous"]);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task DisconnectRevalidatesTheSamePrimaryBeforeAcceptingNewWork()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Sentinel!.Current!;
        primary.CloseConnectionAfterCommand = primary.CommandsSeen + 1;
        await Assert.That(async () => await client.IncrementAsync("ambiguous").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        primary.CloseConnectionAfterCommand = null;
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(original.IsRetired).IsTrue();
        await Assert.That(client.Core.Sentinel.Current).IsNotSameReferenceAs(original);
        await Assert.That(sentinel.ReceivedCommands.Count(command => command.StartsWith("SENTINEL GET-MASTER"))).IsEqualTo(6);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "ROLE", "INCR ambiguous", "ROLE", "SET next value"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentFirstWritesAndRediscoveryShareOneValidatedDiscovery(bool rediscovery)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        if (rediscovery)
        {
            await client.PingAsync();
            // This case measures one post-startup replacement. Do not retire the
            // generation while its first-subscription validation is still using it.
            await WaitForInitialSentinelValidationAsync(client, sentinel);
            var generation = client.Core.Sentinel!.Current!;
            await generation.Multiplexer.GetConnection().DisposeAsync();
        }
        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => client.SetAsync($"key:{index}", "value").AsTask())).WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var discoveryCount = sentinel.ReceivedCommands.Count(command => command == "SENTINEL MASTER mymaster");
        await Assert.That(discoveryCount).IsGreaterThanOrEqualTo(2);
        await Assert.That(discoveryCount).IsLessThanOrEqualTo(rediscovery ? 3 : 2);
        await Assert.That(primary.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(discoveryCount);
        await Assert.That(primary.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(16);
    }

    [Test]
    public async Task PromotionDoesNotMoveAnExistingWatchedTransaction()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var watched = await client.CreateTransactionAsync(["watched"]);
        var queued = watched.Set("old transaction", "value");
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("new generation", "value").AsTask().WaitAsync(Limit);
        await Assert.That(async () => await watched.CommitAsync().AsTask().WaitAsync(Limit)).Throws<RespireException>();
        await Assert.That(queued.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(oldPrimary.ReceivedCommands.Any(command => command is "MULTI" or "EXEC")).IsFalse();
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("WATCH ") || command is "MULTI" or "EXEC")).IsFalse();
    }

    [Test]
    public async Task AcceptedBlockingOperationDrainsThroughItsOriginalPoolAfterPromotion()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) =>
        {
            if (command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)) return "-READONLY replica\r\n"u8.ToArray();
            return null;
        });
        oldPrimary.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var originalPool = client.Core.DedicatedPool;
        var pending = client.Lists.LeftPopAsync("queue", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(oldPrimary, "BLPOP ");
        var index = oldPrimary.ReceivedCommands.ToList().FindIndex(command => command.StartsWith("BLPOP "));
        var connectionId = oldPrimary.ReceivedConnectionIds[index];
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(ReferenceEquals(originalPool, client.Core.DedicatedPool)).IsFalse();
        await Assert.That(pending.IsCompleted).IsFalse();
        await oldPrimary.SendRawAsync("*2\r\n$5\r\nqueue\r\n$5\r\nvalue\r\n"u8.ToArray(), connectionId);
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await originalPool.RetireAsync().AsTask().WaitAsync(Limit);
        await Assert.That(originalPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    [NotInParallel]
    public async Task RetriedStreamedSetCompletesOneTelemetryScope()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) =>
            command == "SET trigger value" && Volatile.Read(ref rejectWrites)
                ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var started = new ConcurrentQueue<Activity>();
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => options.Name == "SET"
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = activity => { if (IsUploadActivity(activity)) started.Enqueue(activity); },
            ActivityStopped = activity => { if (IsUploadActivity(activity)) stopped.Enqueue(activity); },
        };
        bool IsUploadActivity(Activity activity) => activity.OperationName == "SET"
            && activity.GetTagItem("server.port") is int port && (port == oldPrimary.Port || port == promoted.Port);
        ActivitySource.AddActivityListener(listener);
        await using var source = new PausedTelemetryStream();
        var pending = client.Strings.SetAsync("upload", source, source.Length).AsTask();
        await source.Started.Task.WaitAsync(Limit);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.Core.EnsureConnectedAsync(CancellationToken.None).AsTask().WaitAsync(Limit);
        source.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsTrue();
        // One logical upload and the READONLY trigger each start and stop exactly one activity.
        await Assert.That(started.Count).IsEqualTo(2);
        await Assert.That(stopped.Count).IsEqualTo(2);
        await Assert.That(oldPrimary.ReceivedCommands).DoesNotContain("SET upload payload");
        await Assert.That(promoted.ReceivedCommands).Contains("SET upload payload");
    }

    private sealed class PausedTelemetryStream() : MemoryStream("payload"u8.ToArray())
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Test]
    public async Task AcceptedStreamedSetDrainsThroughItsOriginalPoolAfterPromotion()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) =>
            command == "SET trigger value" && Volatile.Read(ref rejectWrites)
                ? "-READONLY replica\r\n"u8.ToArray() : null);
        oldPrimary.SuppressReply = command => command == "SET upload payload";
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var originalPool = client.Core.DedicatedPool;
        await using var source = new MemoryStream("payload"u8.ToArray());
        var pending = client.Strings.SetAsync("upload", source, source.Length).AsTask();
        await WaitForCommandAsync(oldPrimary, "SET upload ");
        var index = oldPrimary.ReceivedCommands.ToList().FindIndex(command => command == "SET upload payload");
        var connectionId = oldPrimary.ReceivedConnectionIds[index];
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await using var nextSource = new MemoryStream("next"u8.ToArray());
        await Assert.That(await client.Strings.SetAsync("new-upload", nextSource, nextSource.Length).AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(ReferenceEquals(originalPool, client.Core.DedicatedPool)).IsFalse();
        await Assert.That(pending.IsCompleted).IsFalse();
        await oldPrimary.SendRawAsync(FakeRespServer.OkReply, connectionId);
        await Assert.That(await pending.WaitAsync(Limit)).IsTrue();
        await originalPool.RetireAsync().AsTask().WaitAsync(Limit);
        await Assert.That(originalPool.CaptureRetirementState().Borrowed).IsEqualTo(0);
        await Assert.That(promoted.ReceivedCommands).Contains("SET new-upload next");
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("SET upload "))).IsFalse();
    }

    [Test]
    public async Task CancelledDiscoveryDoesNotPublishAndTheNextOperationCanConnect()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        sentinel.SuppressReply = command => command.StartsWith("SENTINEL GET-MASTER");
        await using var client = RespireClient.Create(Options(sentinel.Port));
        using var cancellation = new CancellationTokenSource();
        var pending = client.SetAsync("cancelled", "value", cancellationToken: cancellation.Token).AsTask();
        await WaitForCommandAsync(sentinel, "SENTINEL GET-MASTER");
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(primary.CommandsSeen).IsEqualTo(0);
        sentinel.SuppressReply = null;
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        // The publication starts the event monitor, whose revalidation confirms the primary with one more ROLE.
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value", "ROLE"]);
    }

    [Test]
    public async Task DisposalCancelsUnpublishedRoleValidation()
    {
        await using var primary = Primary();
        primary.SuppressReply = command => command == "ROLE";
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var pending = client.SetAsync("never", "value").AsTask();
        await WaitForCommandAsync(primary, "ROLE");
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(primary.ReceivedCommands).IsEquivalentTo(["ROLE"]);
    }

    [Test]
    public async Task AReplicaReplacementIsNeverPublishedForApplicationWrites()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var replica = Primary((_, command) => command == "ROLE"
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref primaryPort, replica.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await Assert.That(async () => await client.SetAsync("never", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["ROLE"]);
        await Assert.That(client.IsConnected).IsFalse();
        Volatile.Write(ref primaryPort, promoted.Port);
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(client.Endpoint.Port).IsEqualTo(promoted.Port);
    }

    [Test]
    public async Task EndpointEventsAndFailoverCounterFollowValidatedPublication()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var events = new ConcurrentQueue<RespireConnectionStateChange>();
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            events.Enqueue(change);
            if (change.Endpoint.Port == promoted.Port && change.State == RespireConnectionState.Connected)
                published.TrySetResult();
        };
        var counted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && Equals(tag.Value, promoted.Port)) counted.TrySetResult(value);
        });
        listener.Start();
        await client.SetAsync("first", "value").AsTask().WaitAsync(Limit);
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(await counted.Task.WaitAsync(Limit)).IsEqualTo(1);
        await published.Task.WaitAsync(Limit);
        var ordered = events.ToArray();
        var disconnected = Array.FindIndex(ordered, change => change.Endpoint.Port == oldPrimary.Port
            && change.State == RespireConnectionState.Disconnected);
        var connected = Array.FindIndex(ordered, change => change.Endpoint.Port == promoted.Port
            && change.State == RespireConnectionState.Connected);
        await Assert.That(disconnected).IsGreaterThanOrEqualTo(0);
        await Assert.That(connected).IsGreaterThan(disconnected);
    }

    [Test]
    public async Task PromotionInvalidatesCachedReadsBeforeTheNextGenerationServesThem()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nold\r\n"u8.ToArray(),
            _ when command.StartsWith("SET ") && Volatile.Read(ref rejectWrites) => "-READONLY replica\r\n"u8.ToArray(),
            _ => null,
        });
        await using var promoted = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        });
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("old");
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await Assert.That(oldPrimary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(promoted.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task PromotionStopsJoiningOldSharedReadsWithoutAbortingAcceptedWork()
    {
        static byte[]? Reply(string command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" => "$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        };
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Primary((_, command) => Reply(command));
        primary.SuppressReply = command =>
        {
            if (command != "GET key") return false;
            accepted.TrySetResult();
            return true;
        };
        await using var promoted = Primary((_, command) => Reply(command));
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { CoalesceConcurrentMisses = true },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var generation = client.Core.Sentinel!.Current!;
        var connection = generation.Multiplexer.GetConnection();
        var leader = client.GetStringAsync("key").AsTask();
        await accepted.Task.WaitAsync(Limit);
        var follower = client.GetStringAsync("key").AsTask();
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(1);
        Volatile.Write(ref port, promoted.Port);
        using (var error = Respire.Protocol.RespValue.Error("READONLY replica"))
            generation.ObserveResponse(connection, "SET", in error);
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(leader.IsCompleted).IsFalse();
        await Assert.That(follower.IsCompleted).IsFalse();
        await primary.SendRawAsync("$3\r\nold\r\n"u8.ToArray());
        await Assert.That(await leader.WaitAsync(Limit)).IsEqualTo("old");
        await Assert.That(await follower.WaitAsync(Limit)).IsEqualTo("old");
        await Assert.That(await client.GetStringAsync("key").AsTask().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
        await Assert.That(promoted.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    public async Task SubscriptionMovesToTheValidatedPrimaryAndReportsTheDeliveryGap()
    {
        var rejectWrites = false;
        static byte[]? SubscriptionReply(string command) => command switch
        {
            "SUBSCRIBE events" => "*3\r\n$9\r\nsubscribe\r\n$6\r\nevents\r\n:1\r\n"u8.ToArray(),
            "UNSUBSCRIBE events" => "*3\r\n$11\r\nunsubscribe\r\n$6\r\nevents\r\n:0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : SubscriptionReply(command));
        await using var promoted = Primary((_, command) => SubscriptionReply(command));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var subscription = await client.SubscribeAsync("events");
        var gap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        subscription.DeliveryGap += _ => gap.TrySetResult();
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await WaitForCommandAsync(promoted, "SUBSCRIBE events");
        await gap.Task.WaitAsync(Limit);
        var index = promoted.ReceivedCommands.ToList().FindIndex(command => command == "SUBSCRIBE events");
        var connectionId = promoted.ReceivedConnectionIds[index];
        await promoted.SendRawAsync("*3\r\n$7\r\nmessage\r\n$6\r\nevents\r\n$5\r\nvalue\r\n"u8.ToArray(), connectionId);
        await using var reader = subscription.GetAsyncEnumerator();
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Gap);
        await Assert.That(await reader.MoveNextAsync().AsTask().WaitAsync(Limit)).IsTrue();
        await Assert.That(reader.Current.Kind).IsEqualTo(RespireMessageKind.Message);
        await Assert.That(Encoding.UTF8.GetString(reader.Current.Payload.Span)).IsEqualTo("value");
        await Assert.That(promoted.ReceivedCommands.Where((_, position) => promoted.ReceivedConnectionIds[position] == connectionId).First())
            .IsEqualTo("ROLE");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NewBatchUsesThePromotedGenerationAndDurabilityKeepsOneSocket(bool durability)
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary((_, command) => command.StartsWith("WAIT ") ? ":1\r\n"u8.ToArray() : null);
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        using var batch = client.CreateBatch();
        var pending = batch.Set("batched", "value");
        if (durability)
            await Assert.That(await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1)).AsTask().WaitAsync(Limit))
                .IsEqualTo(1);
        else
            await batch.ExecuteAsync().AsTask().WaitAsync(Limit);
        await Assert.That(pending.Result).IsTrue();
        await Assert.That(oldPrimary.ReceivedCommands.Any(command => command == "SET batched value")).IsFalse();
        var index = promoted.ReceivedCommands.ToList().IndexOf("SET batched value");
        await Assert.That(index).IsGreaterThanOrEqualTo(0);
        var connection = promoted.ReceivedConnectionIds[index];
        var frames = promoted.ReceivedCommands.Where((_, position) => promoted.ReceivedConnectionIds[position] == connection).ToArray();
        string[] expected = durability
            ? ["ROLE", "SET batched value", "WAIT 1 1000"] : ["ROLE", "SET batched value"];
        await Assert.That(frames).IsEquivalentTo(expected);
    }

    [Test]
    public async Task TrackedCorrectionRetainsItsOriginalPeerAfterPromotion()
    {
        var rejectWrites = false;
        static byte[]? ScriptReply(string command, int clientId)
            => command == "CLIENT ID" ? Encoding.ASCII.GetBytes($":{clientId}\r\n")
                : command.StartsWith("CLIENT KILL ") ? ":0\r\n"u8.ToArray()
                : command.StartsWith("EVAL") ? ":1\r\n"u8.ToArray() : null;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : ScriptReply(command, 41));
        await using var promoted = Primary((_, command) => ScriptReply(command, 42));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var script = RespireScript.Create("return 1");
        var execution = await client.StartTrackedScriptExecutionAsync(script, ["key"], [], default, true);
        using (var reply = await execution.Response) await Assert.That(reply.AsInteger()).IsEqualTo(1);
        var original = execution.ConnectionIdentity;
        await Assert.That(original.ServerClientId).IsEqualTo(41);
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        var next = await client.StartTrackedScriptExecutionAsync(script, ["next"], [], default, true);
        using (var reply = await next.Response) await Assert.That(reply.AsInteger()).IsEqualTo(1);
        await Assert.That(next.ConnectionIdentity.ServerClientId).IsEqualTo(42);
        await Assert.That(next.ConnectionIdentity.Endpoint.Port).IsEqualTo(promoted.Port);
        await client.ExecuteOnAllConnectionsAsync(script, ["key"], [], original).AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).Contains("EVAL return 1 1 key");
        await Assert.That(promoted.ReceivedCommands.Any(command => command == "EVAL return 1 1 key")).IsFalse();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task ManagedLockInitializesTheSelectedGenerationAfterPromotion(bool release, bool rejectIdentity)
    {
        var rejectWrites = false;
        static byte[]? IdentityReply(string command, int clientId)
            => command == "CLIENT ID" ? Encoding.ASCII.GetBytes($":{clientId}\r\n")
                : command.StartsWith("CLIENT KILL ") ? ":0\r\n"u8.ToArray() : null;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : IdentityReply(command, 41));
        await using var promoted = Primary((_, command) => command == "CLIENT ID" && rejectIdentity
            ? "-ERR identity unavailable\r\n"u8.ToArray()
            : command.StartsWith("DELEX ") ? ":1\r\n"u8.ToArray() : IdentityReply(command, 42));
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        await Assert.That(oldPrimary.ReceivedCommands).Contains("CLIENT ID");

        // Fail over after the managed-lock preflight, before selecting its command connection.
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        if (rejectIdentity)
        {
            await Assert.That(async () =>
            {
                var execution = await client.StartLockExecutionAsync("key", "token", release ? null : 1000, true, false, default);
                await execution.Response;
            }).ThrowsExactly<RespireServerException>();
            await Assert.That(promoted.ReceivedCommands.Any(IsLockMutation)).IsFalse();
        }
        else
        {
            var execution = await client.StartLockExecutionAsync("key", "token", release ? null : 1000, true, false, default);
            await Assert.That(await execution.Response).IsTrue();
            await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(42);
            await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(promoted.Port);
            var commands = promoted.ReceivedCommands.ToList();
            await Assert.That(commands.IndexOf("CLIENT ID")).IsLessThan(commands.FindIndex(IsLockMutation));
        }
        await Assert.That(oldPrimary.ReceivedCommands.Any(IsLockMutation)).IsFalse();

        static bool IsLockMutation(string command) => command.StartsWith("SET key ") || command.StartsWith("DELEX key ");
    }

    [Test]
    public async Task CapturedServerPoolRemainsPinnedAfterPromotion()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command == "PING" ? FakeRespServer.PongReply
            : command.StartsWith("SET ") && Volatile.Read(ref rejectWrites) ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var pool = client.Core.CreateServerPool(client.Endpoint);
        try
        {
            var connection = await pool.RentAsync(default);
            try
            {
                Volatile.Write(ref primaryPort, promoted.Port);
                Volatile.Write(ref rejectWrites, true);
                await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
                await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
                using var reply = await connection.SendAsync(new Respire.Commands.RawCommand(FakeRespServer.PingFrame));
                await Assert.That(reply.AsString()).IsEqualTo("PONG");
                await Assert.That(connection.Port).IsEqualTo(oldPrimary.Port);
                await Assert.That(promoted.ReceivedCommands.Any(command => command == "PING")).IsFalse();
            }
            finally { pool.Return(connection); }
        }
        finally { await client.Core.ReleaseServerPoolAsync(pool); }
    }

    [Test]
    public async Task DisposalJoinsRetirementWithAnAcceptedBlockingCommand()
    {
        var rejectWrites = false;
        await using var oldPrimary = Primary((_, command) => command.StartsWith("SET ") && Volatile.Read(ref rejectWrites)
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        oldPrimary.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var promoted = Primary();
        var primaryPort = oldPrimary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Sentinel!.Current!;
        var blocking = client.Lists.LeftPopAsync("queue", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(oldPrimary, "BLPOP ");
        Volatile.Write(ref primaryPort, promoted.Port);
        Volatile.Write(ref rejectWrites, true);
        await Assert.That(async () => await client.SetAsync("trigger", "value")).Throws<RespireServerException>();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(original.Retirement.IsCompleted).IsFalse();
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await blocking.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(original.Retirement.IsCompleted).IsTrue();
        await Assert.That(client.IsConnected).IsFalse();
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    public async Task FailedUnpublishedCandidateDoesNotFlushThePublishedCache()
    {
        static byte[]? Hello(string command) => command == "HELLO 3"
            ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : null;
        await using var primary = Primary((_, command) => command == "GET key"
            ? "$5\r\nvalue\r\n"u8.ToArray() : Hello(command));
        await using var replica = Primary((_, command) => command == "ROLE"
            ? "*1\r\n$5\r\nslave\r\n"u8.ToArray() : Hello(command));
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);
        var current = client.Core.Sentinel!.Current;
        // Exercise an unpublished candidate while the published generation remains healthy.
        // The discovery owner disposes this candidate after ROLE rejects it.
        await using var candidate = new Respire.Internal.SentinelRouter.Generation(client.Core.Sentinel, client.Core,
            Options(sentinel.Port) with { Endpoints = [new("127.0.0.1", replica.Port)], SentinelPrimaryName = null,
                Protocol = RespProtocol.Resp3 });
        await Assert.That(async () => await candidate.Multiplexer.EnsureConnectedAsync(default))
            .Throws<RespireConnectionException>();
        await Assert.That(candidate.IsRetired).IsTrue();
        await Assert.That(candidate.Retirement).IsSameReferenceAs(Task.CompletedTask);
        await Assert.That(client.Core.Sentinel.Current).IsSameReferenceAs(current);
        await Assert.That(client.IsConnected).IsTrue();
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(1);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(primary.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(1);
    }

    [Test]
    [Arguments("EXEC")]
    [Arguments("EVAL")]
    [Arguments("EVALSHA")]
    [Arguments("EVAL_RO")]
    [Arguments("EVALSHA_RO")]
    [Arguments("FCALL")]
    [Arguments("FCALL_RO")]
    public async Task NestedReadOnlyInHeterogeneousRepliesRetiresTheGeneration(string operation)
    {
        await using var primary = Primary((_, command) => command == operation
            ? "*1\r\n*1\r\n-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var primaryPort = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref primaryPort));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        Volatile.Write(ref primaryPort, promoted.Port);
        using (var response = await client.ExecuteAsync((RespireCommand)operation, []))
            await Assert.That(response[0][0].IsError).IsTrue();
        await Assert.That(client.IsConnected).IsFalse();
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    [Arguments("ROLE")]
    [Arguments("EXEC")]
    [Arguments("EVAL")]
    [Arguments("EVALSHA")]
    [Arguments("EVAL_RO")]
    [Arguments("EVALSHA_RO")]
    [Arguments("FCALL")]
    [Arguments("FCALL_RO")]
    [Arguments("HGETALL")]
    public async Task FireAndForgetKeepsSentinelMetadataAndRejectsUnsupportedAffinity(string operation)
    {
        await using var primary = Primary();
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { MaxInflightCommands = 2 });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var generation = client.Core.Sentinel!.Current!;
        if (operation == "EXEC")
        {
            // EXEC is connection-affine and intentionally cannot enter the discarded path.
            await Assert.That(async () => await client.ExecuteFireAndForgetAsync((RespireCommand)operation, []))
                .ThrowsExactly<NotSupportedException>();
            await Assert.That(primary.ReceivedCommands.Contains("EXEC")).IsFalse();
            await Assert.That(generation.IsRetired).IsFalse();
            return;
        }
        primary.SuppressReply = command => command == operation || command == "PING";
        Volatile.Write(ref port, promoted.Port);

        // Completion must remain write-only. Hold both replies until a following PING
        // is accepted, then use that reply as a FIFO barrier after generation observation.
        await client.ExecuteFireAndForgetAsync((RespireCommand)operation, []).AsTask().WaitAsync(Limit);
        var barrier = client.PingAsync().AsTask();
        await WaitForCommandAsync(primary, "PING");
        var reply = operation is "ROLE" or "HGETALL"
            ? "*1\r\n$5\r\nslave\r\n"
            : "*1\r\n*1\r\n-READONLY replica\r\n";
        await primary.SendRawAsync(Encoding.ASCII.GetBytes(reply + "+PONG\r\n"));
        await barrier.WaitAsync(Limit);
        await Assert.That(generation.IsRetired).IsEqualTo(operation != "HGETALL");
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        if (operation == "HGETALL")
            await Assert.That(promoted.CommandsSeen).IsEqualTo(0);
        else
            await Assert.That(promoted.ReceivedCommands).IsEquivalentTo(["ROLE", "SET next value"]);
    }

    [Test]
    public async Task DiscardedOperationMetadataDoesNotLeakAcrossRingWraparound()
    {
        var ring = new Respire.Networking.InflightRing(2);
        for (var iteration = 0; iteration < 8; iteration++)
        {
            await Assert.That(ring.TryEnqueueDiscard("ROLE", 10)).IsTrue();
            await Assert.That(ring.TryEnqueueDiscard("EVAL", 20)).IsTrue();
            await Assert.That(ring.TryEnqueueDiscard("FCALL", 30)).IsFalse();
            await Assert.That(ring.TryDequeue(out var role, out var operation)).IsTrue();
            await Assert.That(ReferenceEquals(role, Respire.Networking.InflightRing.DiscardSentinel)).IsTrue();
            await Assert.That(operation).IsEqualTo("ROLE");
            await Assert.That(ring.TryEnqueue(Respire.Networking.InflightRing.DiscardSentinel, 30)).IsTrue();
            await Assert.That(ring.TryDequeue(out _, out operation)).IsTrue();
            await Assert.That(operation).IsEqualTo("EVAL");
            await Assert.That(ring.TryDequeue(out _, out operation)).IsTrue();
            await Assert.That(operation).IsNull();
            await Assert.That(ring.CompletedWriteEnd).IsEqualTo(30L);
            await Assert.That(ring.TryDequeue(out _, out operation)).IsFalse();
            await Assert.That(operation).IsNull();
        }
    }

    [Test]
    [Arguments("%1\r\n+k\r\n-READONLY replica\r\n", true)]
    [Arguments("~1\r\n-READONLY replica\r\n", true)]
    [Arguments("*2\r\n:1\r\n%1\r\n+k\r\n~1\r\n-READONLY replica\r\n", true)]
    [Arguments("%1\r\n+k\r\n~2\r\n:1\r\n:2\r\n", false)]
    public async Task ReadOnlyTraversalIncludesResp3MapsAndSets(string reply, bool readOnly)
    {
        await using var primary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "EVAL" => Encoding.ASCII.GetBytes(reply),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with { Protocol = RespProtocol.Resp3 });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        using var response = await client.ExecuteAsync((RespireCommand)"EVAL", []);
        await Assert.That(client.IsConnected).IsEqualTo(!readOnly);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task NestedReadOnlyScanResumesAfterDeepNonErrorArrays(bool readOnly)
    {
        // Every level has a scalar sibling before and after its child. The final error
        // requires returning through all pending parents rather than stopping at a scalar.
        var reply = "*3\r\n:0\r\n" + string.Concat(Enumerable.Repeat("*3\r\n:1\r\n", 256))
            + ":2\r\n" + string.Concat(Enumerable.Repeat(":3\r\n", 256))
            + (readOnly ? "-READONLY replica\r\n" : ":4\r\n");
        await using var primary = Primary((_, command) => command == "EVAL" ? Encoding.ASCII.GetBytes(reply) : null);
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        using var response = await client.ExecuteAsync((RespireCommand)"EVAL", []);
        await Assert.That(client.IsConnected).IsEqualTo(!readOnly);
    }

    [Test]
    [Arguments("GET", false)]
    [Arguments("MGET", false)]
    [Arguments("HGET", false)]
    [Arguments("GET", true)]
    [Arguments("MGET", true)]
    [Arguments("HGET", true)]
    [Arguments("RAW MGET", false)]
    [Arguments("RAW MGET", true)]
    public async Task RetirementDuringCacheLookupRejectsTheOldValue(string operation, bool coalesce)
    {
        static byte[]? Reply(string command, string value) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET key" or "HGET key field" => Encoding.ASCII.GetBytes($"$3\r\n{value}\r\n"),
            "MGET tenant:key" => "*1\r\n$3\r\nbad\r\n"u8.ToArray(),
            "MGET key" => Encoding.ASCII.GetBytes($"*1\r\n$3\r\n{value}\r\n"),
            _ => null,
        };
        await using var primary = Primary((_, command) => Reply(command, "old"));
        await using var promoted = Primary((_, command) => Reply(command, "new"));
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new() { CoalesceConcurrentMisses = coalesce },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var prefixed = client.WithKeyPrefix("tenant:");
        await Assert.That(await ReadAsync()).IsEqualTo("old");
        var generation = client.Core.Sentinel!.Current!;
        var connection = generation.Multiplexer.GetConnection();
        var intercept = new AsyncLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.client_cache.hits")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (!intercept.Value) return;
            intercept.Value = false;
            Volatile.Write(ref port, promoted.Port);
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation.ObserveResponse(connection, "SET", in error);
        });
        listener.Start();
        intercept.Value = true;
        await Assert.That(await ReadAsync().WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(generation.IsRetired).IsTrue();
        var wireOperation = operation == "RAW MGET" ? "MGET" : operation;
        await Assert.That(promoted.ReceivedCommands.Any(command => command.StartsWith(wireOperation + " "))).IsTrue();
        if (operation == "RAW MGET")
        {
            await Assert.That(primary.ReceivedCommands.Count(command => command == "MGET key")).IsEqualTo(1);
            await Assert.That(promoted.ReceivedCommands.Count(command => command == "MGET key")).IsEqualTo(1);
            await Assert.That(promoted.ReceivedCommands.Any(command => command == "MGET tenant:key")).IsFalse();
        }

        async Task<string?> ReadAsync()
        {
            if (operation == "RAW MGET")
            {
                using var raw = await prefixed.ExecuteAsync((RespireCommand)"MGET", ["key"]);
                return raw[0].AsString();
            }
            if (operation == "GET") return await client.GetStringAsync("key");
            if (operation == "MGET") return (await client.Strings.GetManyAsync(["key"]))[0];
            using var result = await client.ExecuteAsync((RespireCommand)"HGET", ["key", "field"]);
            return result.AsString();
        }
    }

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, false, true)]
    [Arguments(false, true, false)]
    [Arguments(false, true, true)]
    [Arguments(true, false, false)]
    [Arguments(true, false, true)]
    [Arguments(true, true, false)]
    [Arguments(true, true, true)]
    public async Task RetirementDuringHashFieldLookupRefetchesEveryField(bool raw, bool partial, bool coalesce)
    {
        static byte[]? Reply(string command, string value) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "HGET tenant:key first" or "HGET tenant:key second" => Encoding.ASCII.GetBytes($"$3\r\n{value}\r\n"),
            "HMGET tenant:key first second" => Encoding.ASCII.GetBytes($"*2\r\n$3\r\n{value}\r\n$3\r\n{value}\r\n"),
            // The partial path would fetch only this field without the generation fence.
            "HMGET tenant:key second" => Encoding.ASCII.GetBytes($"*1\r\n$3\r\n{value}\r\n"),
            _ => null,
        };
        await using var primary = Primary((_, command) => Reply(command, "old"));
        await using var promoted = Primary((_, command) => Reply(command, "new"));
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { ReuseHashFields = true, CoalesceConcurrentMisses = coalesce },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var view = client.WithKeyPrefix("tenant:");
        await view.Hashes.GetStringAsync("key", "first");
        if (!partial) await view.Hashes.GetStringAsync("key", "second");
        var generation = client.Core.Sentinel!.Current!;
        var connection = generation.Multiplexer.GetConnection();
        var intercept = new AsyncLocal<bool>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.client_cache.hits")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
        {
            if (!intercept.Value) return;
            intercept.Value = false;
            Volatile.Write(ref port, promoted.Port);
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation.ObserveResponse(connection, "SET", in error);
        });
        listener.Start();
        intercept.Value = true;
        string?[] values;
        if (raw)
        {
            using var response = await view.ExecuteAsync((RespireCommand)"HMGET", ["tenant:key", "first", "second"])
                .AsTask().WaitAsync(Limit);
            values = [response[0].AsString(), response[1].AsString()];
        }
        else values = await view.Hashes.GetManyAsync("key", "first", "second").AsTask().WaitAsync(Limit);
        await Assert.That(values).IsEquivalentTo(new string?[] { "new", "new" });
        await Assert.That(generation.IsRetired).IsTrue();
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("HMGET "))).IsFalse();
        await Assert.That(promoted.ReceivedCommands.Where(command => command.StartsWith("HMGET ")))
            .IsEquivalentTo(["HMGET tenant:key first second"]);
    }

    [Test]
    public async Task RetirementDuringMGetMissFetchRefetchesCachedAndMissingKeys()
    {
        var port = 0;
        var shouldRetire = 0;
        Respire.Internal.SentinelRouter.Generation? generation = null;
        RespireConnection? connection = null;
        FakeRespServer? promotedServer = null;
        await using var primary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "GET cached" => "$3\r\nold\r\n"u8.ToArray(),
            "MGET missing" when Interlocked.Exchange(ref shouldRetire, 0) == 1 => RetireAndReply(),
            "MGET missing" => "*1\r\n$3\r\nold\r\n"u8.ToArray(),
            _ => null,
        });
        await using var promoted = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "MGET cached missing" => "*2\r\n$3\r\nnew\r\n$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        });
        promotedServer = promoted;
        port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3, ClientSideCache = new(),
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        generation = client.Core.Sentinel!.Current!;
        connection = generation.Multiplexer.GetConnection();
        await Assert.That(await client.GetStringAsync("cached")).IsEqualTo("old");
        Volatile.Write(ref shouldRetire, 1);

        var values = await client.Strings.GetManyAsync(["cached", "missing"]).AsTask().WaitAsync(Limit);

        string?[] expectedValues = ["new", "new"];
        await Assert.That(values).IsEquivalentTo(expectedValues);
        await Assert.That(generation.IsRetired).IsTrue();
        await Assert.That(promoted.ReceivedCommands.Contains("MGET cached missing")).IsTrue();

        byte[] RetireAndReply()
        {
            Volatile.Write(ref port, promotedServer!.Port);
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation!.ObserveResponse(connection!, "SET", in error);
            return "*1\r\n$3\r\nold\r\n"u8.ToArray();
        }
    }

    [Test]
    public async Task RetirementDuringHmGetMissFetchRefetchesCachedAndMissingFields()
    {
        var port = 0;
        var shouldRetire = 0;
        Respire.Internal.SentinelRouter.Generation? generation = null;
        RespireConnection? connection = null;
        FakeRespServer? promotedServer = null;
        await using var primary = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "HGET key first" => "$3\r\nold\r\n"u8.ToArray(),
            "HMGET key second" when Interlocked.Exchange(ref shouldRetire, 0) == 1 => RetireAndReply(),
            "HMGET key second" => "*1\r\n$3\r\nold\r\n"u8.ToArray(),
            _ => null,
        });
        await using var promoted = Primary((_, command) => command switch
        {
            "HELLO 3" => "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(),
            "HMGET key first second" => "*2\r\n$3\r\nnew\r\n$3\r\nnew\r\n"u8.ToArray(),
            _ => null,
        });
        promotedServer = promoted;
        port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { ReuseHashFields = true },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        generation = client.Core.Sentinel!.Current!;
        connection = generation.Multiplexer.GetConnection();
        await Assert.That(await client.Hashes.GetStringAsync("key", "first")).IsEqualTo("old");
        Volatile.Write(ref shouldRetire, 1);

        var values = await client.Hashes.GetManyAsync("key", "first", "second").AsTask().WaitAsync(Limit);

        await Assert.That(values).IsEquivalentTo(new string?[] { "new", "new" });
        await Assert.That(generation.IsRetired).IsTrue();
        await Assert.That(promoted.ReceivedCommands.Contains("HMGET key first second")).IsTrue();

        byte[] RetireAndReply()
        {
            Volatile.Write(ref port, promotedServer!.Port);
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation!.ObserveResponse(connection!, "SET", in error);
            return "*1\r\n$3\r\nold\r\n"u8.ToArray();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("batch", true)]
    [Arguments("durability", true)]
    [Arguments("transaction", true)]
    [Arguments("batch", false)]
    [Arguments("durability", false)]
    [Arguments("transaction", false)]
    [Arguments("blocking", true)]
    [Arguments("blocking", false)]
    [Arguments("script", true)]
    [Arguments("script", false)]
    [Arguments("blocking-rental", true)]
    [Arguments("blocking-rental", false)]
    public async Task FailedDiscoveryRetainsTelemetryWithoutInventingAPrimary(string kind, bool trace)
    {
        var operation = kind switch { "blocking" or "blocking-rental" => "BLPOP", "script" => "EVALSHA", _ => "SET" };
        var rental = kind == "blocking-rental";
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Primary();
        var roles = 0;
        primary.SuppressReply = command =>
        {
            if (!rental || command != "ROLE" || Interlocked.Increment(ref roles) != 2) return false;
            queried.TrySetResult();
            return true;
        };
        await using var sentinel = new FakeRespServer("*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, _) => rental ? AddressReply(primary.Port) : null,
            SuppressReply = command =>
            {
                if (rental || !command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")) return false;
                queried.TrySetResult();
                return true;
            },
        };
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => trace && source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if (Equals(activity.GetTagItem("db.operation.name"), operation)) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(activityListener);
        var measurements = new List<(double Duration, Dictionary<string, object?> Tags)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((_, duration, tags, _) =>
        {
            var captured = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
            if (Equals(captured["db.operation.name"], operation)) measurements.Add((duration, captured));
        });
        meterListener.Start();
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        _ = batch.Set("key", "value");
        _ = transaction.Set("key", "value");
        Task execution = kind switch
        {
            "blocking" or "blocking-rental" => client.Lists.LeftPopAsync("key", waitFor: Timeout.InfiniteTimeSpan).AsTask(),
            "script" => client.Scripts.ExecuteAsync(RespireScript.Create("return 1")).AsTask(),
            "transaction" => transaction.CommitAsync().AsTask(),
            "durability" => batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1)).AsTask(),
            _ => batch.ExecuteAsync().AsTask(),
        };
        await queried.Task.WaitAsync(Limit);
        var releaseTime = DateTime.UtcNow;
        if (rental) await primary.SendRawAsync("*0\r\n"u8.ToArray(), connectionId: 1);
        else await sentinel.SendRawAsync("$-1\r\n"u8.ToArray());
        await Assert.That(async () => await execution.WaitAsync(Limit)).Throws<RespireConnectionException>();
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Duration).IsGreaterThan(0);
        await Assert.That(measurements[0].Tags.ContainsKey("error.type")).IsTrue();
        await Assert.That(measurements[0].Tags.ContainsKey("server.address")).IsFalse();
        await Assert.That(measurements[0].Tags.ContainsKey("server.port")).IsFalse();
        await Assert.That(activities.Count).IsEqualTo(trace ? 1 : 0);
        if (trace)
        {
            await Assert.That(activities[0].Status).IsEqualTo(ActivityStatusCode.Error);
            await Assert.That(activities[0].StartTimeUtc <= releaseTime).IsTrue();
            await Assert.That(activities[0].GetTagItem("server.address")).IsNull();
        }
    }

    [Test]
    [NotInParallel]
    [Arguments("batch")]
    [Arguments("durability")]
    [Arguments("transaction")]
    [Arguments("blocking")]
    [Arguments("script")]
    public async Task FirstLazyOperationIsSampledWithThePrimaryEndpoint(string kind)
    {
        await using var primary = Primary((_, command) => command switch
        {
            "SET key value" when kind == "transaction" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
            "WAIT 1 1000" => ":1\r\n"u8.ToArray(),
            "BLPOP key 0" => "*2\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray(),
            _ when command.StartsWith("EVALSHA ") => ":1\r\n"u8.ToArray(),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var samples = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name == "SET" || options.Name == "BLPOP" || options.Name.StartsWith("EVALSHA")) samples.Enqueue(options.Tags!.ToDictionary(tag => tag.Key, tag => tag.Value));
                return ActivitySamplingResult.AllDataAndRecorded;
            },
        };
        ActivitySource.AddActivityListener(listener);
        if (kind == "blocking")
            await client.Lists.LeftPopAsync("key", waitFor: Timeout.InfiniteTimeSpan).AsTask().WaitAsync(Limit);
        else if (kind == "script")
        {
            using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"));
            await Assert.That(result.AsInteger()).IsEqualTo(1);
        }
        else if (kind == "transaction")
        {
            await using var transaction = client.CreateTransaction();
            _ = transaction.Set("key", "value");
            await transaction.CommitAsync();
        }
        else
        {
            using var batch = client.CreateBatch();
            _ = batch.Set("key", "value");
            if (kind == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
            else await batch.ExecuteAsync();
        }
        await Assert.That(samples.Any(tags => Equals(tags["server.port"], primary.Port))).IsTrue();
        await Assert.That(samples.Any(tags => Equals(tags["server.port"], sentinel.Port))).IsFalse();
    }

    [Test]
    [NotInParallel]
    [Arguments("blocking", false)]
    [Arguments("blocking", true)]
    [Arguments("script", false)]
    [Arguments("script", true)]
    [Arguments("batch", false)]
    [Arguments("batch", true)]
    [Arguments("durability", false)]
    [Arguments("durability", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task SuccessfulSentinelOperationIncludesDiscoveryInItsDuration(string kind, bool trace)
    {
        var operation = kind switch { "blocking" => "BLPOP", "script" => "EVALSHA", _ => "SET" };
        await using var primary = Primary((_, command) => command switch
        {
            "SET key value" when kind == "transaction" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => "*1\r\n+OK\r\n"u8.ToArray(),
            "WAIT 1 1000" => ":1\r\n"u8.ToArray(),
            "BLPOP key 0" => "*2\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray(),
            _ when command.StartsWith("EVALSHA ") => ":1\r\n"u8.ToArray(),
            _ => null,
        });
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sentinel = Sentinel(() => primary.Port);
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")) return false;
            queried.TrySetResult();
            return true;
        };
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var activities = new List<Activity>();
        var sampled = new List<Dictionary<string, object?>>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => trace && source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
            {
                if (options.Name.StartsWith(operation)) sampled.Add(options.Tags!.ToDictionary(tag => tag.Key, tag => tag.Value));
                return ActivitySamplingResult.AllDataAndRecorded;
            },
            ActivityStopped = activity => { if (Equals(activity.GetTagItem("db.operation.name"), operation)) activities.Add(activity); },
        };
        ActivitySource.AddActivityListener(activityListener);
        var measurements = new List<(double Duration, Dictionary<string, object?> Tags)>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<double>((_, duration, tags, _) =>
        {
            var captured = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
            if (Equals(captured["db.operation.name"], operation)) measurements.Add((duration, captured));
        });
        meterListener.Start();
        var execution = ExecuteAsync();
        await queried.Task.WaitAsync(Limit);
        var releaseTime = DateTime.UtcNow;
        sentinel.SuppressReply = null;
        await sentinel.SendRawAsync(AddressReply(primary.Port));
        await execution.WaitAsync(Limit);
        await Assert.That(measurements.Count).IsEqualTo(1);
        await Assert.That(measurements[0].Duration).IsGreaterThan(0);
        await Assert.That(measurements[0].Tags["server.port"]).IsEqualTo(primary.Port);
        await Assert.That(measurements[0].Tags.ContainsKey("error.type")).IsFalse();
        await Assert.That(activities.Count).IsEqualTo(trace ? 1 : 0);
        if (trace)
        {
            await Assert.That(sampled.Single()["server.port"]).IsEqualTo(primary.Port);
            await Assert.That(activities[0].StartTimeUtc <= releaseTime).IsTrue();
        }

        async Task ExecuteAsync()
        {
            if (kind == "blocking") await client.Lists.LeftPopAsync("key", waitFor: Timeout.InfiniteTimeSpan);
            else if (kind == "script")
            {
                using var result = await client.Scripts.ExecuteAsync(RespireScript.Create("return 1"));
                await Assert.That(result.AsInteger()).IsEqualTo(1);
            }
            else if (kind == "transaction")
            {
                await using var transaction = client.CreateTransaction();
                _ = transaction.Set("key", "value");
                await transaction.CommitAsync();
            }
            else
            {
                using var batch = client.CreateBatch();
                _ = batch.Set("key", "value");
                if (kind == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
                else await batch.ExecuteAsync();
            }
        }
    }

    [Test]
    [Arguments("batch")]
    [Arguments("durability")]
    [Arguments("transaction")]
    public async Task BatchDurationKeepsTheAdmittedPrimaryAfterPromotion(string kind)
    {
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var primary = Primary((_, command) => command.StartsWith("SET delayed") && kind == "transaction"
            ? "+QUEUED\r\n"u8.ToArray() : null);
        primary.SuppressReply = command =>
        {
            var last = kind switch
            {
                "transaction" => command == "EXEC",
                "durability" => command == "WAIT 1 1000",
                _ => command == "SET delayed2 value",
            };
            if (last) admitted.TrySetResult();
            return kind switch
            {
                "transaction" => command == "EXEC",
                "durability" => command == "WAIT 1 1000",
                _ => command.StartsWith("SET delayed"),
            };
        };
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Sentinel!.Current!;
        var samples = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "db.client.operation.duration")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            var values = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (Equals(values.GetValueOrDefault("db.operation.batch.size"), 2)
                && (Equals(values.GetValueOrDefault("server.port"), primary.Port)
                    || Equals(values.GetValueOrDefault("server.port"), promoted.Port))) samples.Enqueue(values);
        });
        listener.Start();
        var pending = ExecuteAsync();
        await admitted.Task.WaitAsync(Limit);
        Volatile.Write(ref port, promoted.Port);
        using (var rejection = Respire.Protocol.RespValue.Error("READONLY replica"))
            original.ObserveResponse(original.Multiplexer.GetConnection(), "SET", in rejection);
        await client.SetAsync("next", "value").AsTask().WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        var commandIndex = primary.ReceivedCommands.ToList().IndexOf("SET delayed1 value");
        var connectionId = primary.ReceivedConnectionIds[commandIndex];
        var response = kind switch
        {
            "transaction" => "*2\r\n+OK\r\n+OK\r\n",
            "durability" => ":1\r\n",
            _ => "+OK\r\n+OK\r\n",
        };
        await primary.SendRawAsync(Encoding.ASCII.GetBytes(response), connectionId);
        await pending.WaitAsync(Limit);
        await Assert.That(samples.Count).IsEqualTo(1);
        await Assert.That(samples.Single()["server.port"]).IsEqualTo(primary.Port);
        await Assert.That(samples.Single()["server.address"]).IsEqualTo("127.0.0.1");

        async Task ExecuteAsync()
        {
            if (kind == "transaction")
            {
                await using var transaction = client.CreateTransaction();
                _ = transaction.Set("delayed1", "value");
                _ = transaction.Set("delayed2", "value");
                await transaction.CommitAsync();
            }
            else
            {
                using var batch = client.CreateBatch();
                _ = batch.Set("delayed1", "value");
                _ = batch.Set("delayed2", "value");
                if (kind == "durability") await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1));
                else await batch.ExecuteAsync();
            }
        }
    }

    [Test]
    public async Task StateObserverCanSynchronouslyDisposeTheClient()
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.State != RespireConnectionState.Connected) return;
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                disposed.TrySetResult();
            }
            catch (Exception error) { disposed.TrySetException(error); }
        };
        try { await client.SetAsync("key", "value"); }
        catch (Exception) when (client.Core.Disposed) { }
        await disposed.Task.WaitAsync(Limit);
        await Assert.That(client.IsConnected).IsFalse();
    }

    [Test]
    public async Task ConsecutiveFailoversRetainAndDisposeBothDrainingGenerations()
    {
        await using var first = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var second = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        first.SuppressReply = second.SuppressReply = command => command.StartsWith("BLPOP ");
        await using var third = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var original = client.Core.Sentinel!.Current!;
        var firstRead = client.Lists.LeftPopAsync("first", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(first, "BLPOP ");
        Volatile.Write(ref port, second.Port);
        await Assert.That(async () => await client.SetAsync("retire:first", "value")).Throws<RespireServerException>();
        await client.SetAsync("second", "value");
        var replacement = client.Core.Sentinel.Current!;
        var secondRead = client.Lists.LeftPopAsync("second", waitFor: Timeout.InfiniteTimeSpan).AsTask();
        await WaitForCommandAsync(second, "BLPOP ");
        Volatile.Write(ref port, third.Port);
        await Assert.That(async () => await client.SetAsync("retire:second", "value")).Throws<RespireServerException>();
        await client.SetAsync("third", "value");
        await Assert.That(original.Retirement.IsCompleted).IsFalse();
        await Assert.That(replacement.Retirement.IsCompleted).IsFalse();
        long retained = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.generations.retired")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => retained = value);
        listener.Start();
        listener.RecordObservableInstruments();
        await Assert.That(retained).IsGreaterThanOrEqualTo(2);
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await Assert.That(async () => await firstRead.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(async () => await secondRead.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(original.Retirement.IsCompleted).IsTrue();
        await Assert.That(replacement.Retirement.IsCompleted).IsTrue();
        await Assert.That(original.CountedAsRetired).IsFalse();
        await Assert.That(replacement.CountedAsRetired).IsFalse();
        await Assert.That(third.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    public async Task DisposalDoesNotDropAnAlreadyPublishedFailoverMeasurement()
    {
        await using var primary = Primary((_, command) => command.StartsWith("SET retire")
            ? "-READONLY replica\r\n"u8.ToArray() : null);
        await using var promoted = Primary();
        var port = primary.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = RespireClient.Create(Options(sentinel.Port));
        var observerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserver = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var measured = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == "Respire" && instrument.Name == "respire.sentinel.failover")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "server.port" && Equals(tag.Value, promoted.Port)) measured.TrySetResult(value);
        });
        listener.Start();
        client.ConnectionStateChanged += change =>
        {
            if (change.Endpoint.Port != primary.Port || change.State != RespireConnectionState.Connected) return;
            observerEntered.TrySetResult();
            releaseObserver.Task.GetAwaiter().GetResult();
        };
        try
        {
            await client.SetAsync("first", "value");
            await WaitForInitialSentinelValidationAsync(client, sentinel);
            await observerEntered.Task.WaitAsync(Limit);
            Volatile.Write(ref port, promoted.Port);
            await Assert.That(async () => await client.SetAsync("retire", "value")).Throws<RespireServerException>();
            await client.SetAsync("promoted", "value");
            await client.DisposeAsync().AsTask().WaitAsync(Limit);
        }
        finally { releaseObserver.TrySetResult(); }
        await Assert.That(await measured.Task.WaitAsync(Limit)).IsEqualTo(1L);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PermanentlyFailingFenceBacksOffUntilDisposalCancelsTheDelay(bool failRetirementWait)
    {
        var rejectFences = false;
        await using var primary = Primary((_, command) => command switch
        {
            "CLIENT ID" => ":41\r\n"u8.ToArray(),
            _ when command.StartsWith("CLIENT KILL ") => Volatile.Read(ref rejectFences)
                ? "-ERR fencing unavailable\r\n"u8.ToArray() : ":0\r\n"u8.ToArray(),
            _ => null,
        });
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
        var router = client.Core.Sentinel!;
        var generation = router.Current!;
        var clock = new FenceClock();
        router.Clock = clock;
        Volatile.Write(ref rejectFences, true);
        // Lose an accepted command after capturing CLIENT ID, so retirement must fence it.
        primary.CloseConnectionAfterCommand = primary.CommandsSeen + 1;
        await Assert.That(async () => await client.SetAsync("lost", "value").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        var first = await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(1));
        await Assert.That(first.Delay).IsEqualTo(TimeSpan.FromSeconds(1));
        await Assert.That(generation.Multiplexer.HasPendingCorrectionFences).IsTrue();
        await Assert.That(generation.Retirement.IsCompleted).IsFalse();
        first.Fire();
        var second = await ReadFenceTimerAsync(clock, TimeSpan.FromSeconds(2));
        await Assert.That(second.Delay).IsEqualTo(TimeSpan.FromSeconds(2));
        await Assert.That(generation.CountedAsRetired).IsTrue();
        var actualRetirement = generation.Retirement;
        if (failRetirementWait)
        {
            generation.Retirement = Task.FromException(new InvalidOperationException("Injected disposal failure."));
            await Assert.That(async () => await client.DisposeAsync().AsTask().WaitAsync(Limit))
                .ThrowsExactly<InvalidOperationException>();
            await actualRetirement.WaitAsync(Limit);
        }
        else await client.DisposeAsync().AsTask().WaitAsync(Limit);
        await second.Disposed.Task.WaitAsync(Limit);
        await Assert.That(generation.Retirement.IsCompleted).IsTrue();
        await Assert.That(generation.CountedAsRetired).IsFalse();
        await Assert.That(clock.Timers.Reader.TryRead(out _)).IsFalse();
    }

    [Test]
    public async Task ConsecutiveFailoversKeepBothCorrectionFencesUntilClientDisposal()
    {
        var rejectFences = new int[2];
        byte[]? Reply(int primary, string command) => command switch
        {
            "CLIENT ID" => Encoding.ASCII.GetBytes($":{41 + primary}\r\n"),
            _ when command.StartsWith("CLIENT KILL ") => Volatile.Read(ref rejectFences[primary]) != 0
                ? "-ERR fencing unavailable\r\n"u8.ToArray() : ":0\r\n"u8.ToArray(),
            _ => null,
        };
        await using var first = Primary((_, command) => Reply(0, command));
        await using var second = Primary((_, command) => Reply(1, command));
        await using var third = Primary();
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port));
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        var router = client.Core.Sentinel!;
        var clock = new FenceClock();
        router.Clock = clock;
        var generations = new List<Respire.Internal.SentinelRouter.Generation>();
        var timers = new List<FenceTimer>();
        var primaries = new[] { first, second };
        for (var index = 0; index < primaries.Length; index++)
        {
            Volatile.Write(ref port, primaries[index].Port);
            await client.EnsureReliableCorrectionOrderingAsync().AsTask().WaitAsync(Limit);
            generations.Add(router.Current!);
            Volatile.Write(ref rejectFences[index], 1);
            primaries[index].CloseConnectionAfterCommand = primaries[index].CommandsSeen + 1;
            await Assert.That(async () => await client.SetAsync("lost", "value").AsTask().WaitAsync(Limit))
                .Throws<RespireConnectionException>();
            timers.Add(await clock.Timers.Reader.ReadAsync().AsTask().WaitAsync(Limit));
        }
        Volatile.Write(ref port, third.Port);
        await client.SetAsync("current", "value").AsTask().WaitAsync(Limit);
        foreach (var generation in generations)
        {
            await Assert.That(generation.Multiplexer.HasPendingCorrectionFences).IsTrue();
            await Assert.That(generation.CountedAsRetired).IsTrue();
            await Assert.That(generation.Retirement.IsCompleted).IsFalse();
        }
        await client.DisposeAsync().AsTask().WaitAsync(Limit);
        foreach (var timer in timers) await timer.Disposed.Task.WaitAsync(Limit);
        foreach (var generation in generations)
        {
            await Assert.That(generation.Retirement.IsCompleted).IsTrue();
            await Assert.That(generation.CountedAsRetired).IsFalse();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task BlockingDiscoveryTimeoutNeverNamesAnUnselectedPeer(bool rediscovery, bool cancelCaller)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port));
        if (rediscovery)
        {
            await client.PingAsync();
            await WaitForInitialSentinelValidationAsync(client, sentinel);
            var generation = client.Core.Sentinel!.Current!;
            using var error = Respire.Protocol.RespValue.Error("READONLY replica");
            generation.ObserveResponse(generation.Multiplexer.GetConnection(), "SET", in error);
        }
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")) return false;
            queried.TrySetResult();
            return true;
        };
        using var caller = new CancellationTokenSource();
        using var deadline = Respire.Internal.CommandTimeoutCancellation.Create(caller.Token, Limit);
        var pending = client.SendBlockingAsync("BLPOP", new Respire.Commands.Cmd1(Respire.Commands.Verbs.BLPop, "key"),
            deadline.Token, cancellationTimeout: Limit, callerCancellationToken: caller.Token).AsTask();
        await queried.Task.WaitAsync(Limit);
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<OperationCanceledException>();
        }
        else
        {
            deadline.Cancel();
            var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
            await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
            await Assert.That(error.Diagnostics.Endpoint).IsNull();
            await Assert.That(error.Diagnostics.ConnectionId).IsNull();
        }
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("BLPOP "))).IsFalse();
    }

    [Test]
    [Arguments(RespireClientTrackingMode.OptIn, false)]
    [Arguments(RespireClientTrackingMode.OptIn, true)]
    [Arguments(RespireClientTrackingMode.Broadcast, false)]
    [Arguments(RespireClientTrackingMode.Broadcast, true)]
    public async Task ContinuityObserverCanSynchronouslyReadFromPromotedPrimary(RespireClientTrackingMode mode, bool coalesce)
    {
        static byte[]? Reply(string command, string value) => command switch
        {
            "HELLO 3" => "%1\r\n+proto\r\n:3\r\n"u8.ToArray(),
            "GET tenant:key" => Encoding.ASCII.GetBytes($"${value.Length}\r\n{value}\r\n"),
            _ => null,
        };
        await using var first = Primary((_, command) => Reply(command, "old"));
        await using var second = Primary((_, command) => Reply(command, "new"));
        var port = first.Port;
        await using var sentinel = Sentinel(() => Volatile.Read(ref port));
        await using var client = await RespireClient.ConnectAsync(Options(sentinel.Port) with
        {
            Protocol = RespProtocol.Resp3,
            ClientSideCache = new() { TrackingMode = mode, CoalesceConcurrentMisses = coalesce },
        });
        await WaitForInitialSentinelValidationAsync(client, sentinel);
        await using var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("old");
        var observed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reading = 0;
        using var subscription = client.ClientSideCache!.SubscribeInvalidations("tenant:key", change =>
        {
            if (!change.Reasons.HasFlag(RespireClientCacheInvalidationReason.ContinuityLost)
                || Interlocked.CompareExchange(ref reading, 1, 0) != 0) return;
            try { observed.TrySetResult(view.GetStringAsync("key").AsTask().WaitAsync(Limit).GetAwaiter().GetResult()); }
            catch (Exception error) { observed.TrySetException(error); }
        });
        Volatile.Write(ref port, second.Port);
        var generation = client.Core.Sentinel!.Current!;
        using var rejection = Respire.Protocol.RespValue.Error("READONLY replica");
        generation.ObserveResponse(generation.Multiplexer.GetConnection(), "SET", in rejection);
        await Assert.That(await observed.Task.WaitAsync(Limit)).IsEqualTo("new");
        await Assert.That(subscription.LastObserverException).IsNull();
        await Assert.That(first.ReceivedCommands.Count(command => command == "GET tenant:key")).IsEqualTo(1);
        await Assert.That(second.ReceivedCommands.Count(command => command == "GET tenant:key")).IsEqualTo(1);
    }

    [Test]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    [Arguments("identity", false)]
    [Arguments("identity", true)]
    [NotInParallel]
    public async Task OtherSentinelAcquisitionTimeoutsDoNotReportDiscoveryPeers(string kind, bool rediscovery)
    {
        await using var primary = Primary();
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port) with { CommandTimeout = TimeSpan.FromSeconds(3) });
        if (rediscovery)
        {
            await client.PingAsync();
            await WaitForInitialSentinelValidationAsync(client, sentinel);
            var generation = client.Core.Sentinel!.Current!;
            using var rejection = Respire.Protocol.RespValue.Error("READONLY replica");
            generation.ObserveResponse(generation.Multiplexer.GetConnection(), "SET", in rejection);
        }
        var queried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sentinel.SuppressReply = command =>
        {
            if (!command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")) return false;
            queried.TrySetResult();
            return true;
        };
        await using var transaction = client.CreateTransaction();
        _ = transaction.Set("key", "value");
        var pending = kind == "transaction" ? transaction.CommitAsync().AsTask()
            : (Task)client.EnsureReliableCorrectionOrderingAsync().AsTask();
        await queried.Task.WaitAsync(Limit);
        var error = await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Connecting);
        await Assert.That(error.Diagnostics.Endpoint).IsNull();
        await Assert.That(error.Diagnostics.ConnectionId).IsNull();
        await Assert.That(primary.ReceivedCommands.Any(command => command is "CLIENT ID" or "MULTI")).IsFalse();
    }

    [Test]
    public async Task PrimaryRoleTimeoutKeepsSentinelConnectionFailureContext()
    {
        await using var primary = Primary();
        primary.SuppressReply = command => command == "ROLE";
        await using var sentinel = Sentinel(() => primary.Port);
        await using var client = RespireClient.Create(Options(sentinel.Port) with
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(150),
            CommandTimeout = TimeSpan.FromSeconds(1),
        });

        var error = await Assert.That(async () => await client.PingAsync().AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();

        await Assert.That(error!.InnerException).IsNotNull();
        await Assert.That(error.Message).Contains("Unable to discover and connect to Redis Sentinel service");
    }

    private sealed class FenceClock : TimeProvider
    {
        internal Channel<FenceTimer> Timers { get; } = Channel.CreateUnbounded<FenceTimer>();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FenceTimer(callback, state, dueTime);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private static async Task<FenceTimer> ReadFenceTimerAsync(FenceClock clock, TimeSpan delay)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            var timer = await clock.Timers.Reader.ReadAsync(timeout.Token);
            if (timer.Delay == delay) return timer;
        }
    }

    private sealed class FenceTimer(TimerCallback callback, object? state, TimeSpan delay) : ITimer
    {
        internal TimeSpan Delay => delay;
        internal TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Fire() { if (!Disposed.Task.IsCompleted) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Disposed.TrySetResult();
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    private static async Task WaitForCommandAsync(FakeRespServer server, string prefix)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!server.ReceivedCommands.Any(command => command.StartsWith(prefix)))
            await Task.Delay(5, timeout.Token);
    }

    private static async Task WaitForInitialSentinelValidationAsync(
        RespireClient client, FakeRespServer sentinel, bool waitForRediscovery = true, int expectedSubscriptions = 1)
    {
        var router = client.Core.Sentinel!;
        await WaitForCommandAsync(sentinel, "SUBSCRIBE +switch-master");
        using (var timeout = new CancellationTokenSource(Limit))
            await SentinelTestSetup.WaitForSubscriptionsAsync(router, expectedSubscriptions, timeout.Token);
        var rediscovery = router.NotificationRediscovery;
        if (waitForRediscovery && rediscovery is not null) await rediscovery.WaitAsync(Limit);
    }

    private static async Task WaitForCommandCountAsync(FakeRespServer server, string command, int count)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (server.ReceivedCommands.Count(value => value == command) < count)
            await Task.Delay(5, timeout.Token);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SentinelRouter, NotificationCounter> NotificationCounters = new();

    private sealed class NotificationCounter
    {
        internal int Count;
        internal void Observed() => Interlocked.Increment(ref Count);
    }

    private static int QueuedNotificationCount(SentinelRouter router)
    {
        lock (NotificationCounters)
        {
            var counter = NotificationCounters.GetValue(router, static owner =>
            {
                var created = new NotificationCounter();
                owner.NotificationQueuedObserver = created.Observed;
                return created;
            });
            return Volatile.Read(ref counter.Count);
        }
    }

    private static async Task WaitForQueuedNotificationsAsync(SentinelRouter router, int count)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (QueuedNotificationCount(router) < count)
            await Task.Delay(5, timeout.Token);
    }

    private static async Task WaitForEndpointAsync(RespireClient client, int port)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (true)
        {
            try
            {
                if (client.Endpoint.Port == port) return;
            }
            catch (InvalidOperationException) { }
            await Task.Delay(5, timeout.Token);
        }
    }

    private static Task SendSentinelMessageAsync(FakeRespServer sentinel, int connectionId, string channel, string message)
    {
        var frame = Encoding.UTF8.GetBytes($"*3\r\n$7\r\nmessage\r\n${Encoding.UTF8.GetByteCount(channel)}\r\n{channel}\r\n${Encoding.UTF8.GetByteCount(message)}\r\n{message}\r\n");
        return sentinel.SendRawAsync(frame, connectionId);
    }

    private static RespireOptions Options(int sentinelPort) => new()
    {
        Endpoints = [new("127.0.0.1", sentinelPort)], SentinelPrimaryName = "mymaster",
        Connections = 1, ConnectTimeout = Limit, CommandTimeout = Limit, Protocol = RespProtocol.Resp2,
    };

    private static FakeRespServer Primary(Func<int, string, byte[]?>? reply = null, int maxConnections = 8)
        => new(maxConnections, FakeRespServer.OkReply)
        {
            ReplyOverride = (connection, command) => reply?.Invoke(connection, command)
                ?? (command == "ROLE" ? PrimaryRole : FakeRespServer.OkReply),
        };

    private static FakeRespServer Sentinel(Func<int> primaryPort, Func<long>? configurationEpoch = null, int maxConnections = 64)
    {
        var epochs = new Dictionary<int, long>();
        byte[] Configuration()
        {
            var port = primaryPort();
            long epoch;
            lock (epochs)
            {
                if (!epochs.TryGetValue(port, out epoch)) epochs[port] = epoch = epochs.Count;
            }
            return ConfigurationReply(port, configurationEpoch?.Invoke() ?? epoch);
        }
        return new(maxConnections, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ")
                ? AddressReply(primaryPort())
                : command switch
                {
                    "SENTINEL MASTER mymaster" => Configuration(),
                    "SUBSCRIBE +switch-master" => "*3\r\n$9\r\nsubscribe\r\n$14\r\n+switch-master\r\n:1\r\n"u8.ToArray(),
                    "SUBSCRIBE +sdown" => "*3\r\n$9\r\nsubscribe\r\n$6\r\n+sdown\r\n:1\r\n"u8.ToArray(),
                    "SUBSCRIBE +odown" => "*3\r\n$9\r\nsubscribe\r\n$6\r\n+odown\r\n:1\r\n"u8.ToArray(),
                    "SUBSCRIBE +switch-master +sdown +odown" => "*3\r\n$9\r\nsubscribe\r\n$14\r\n+switch-master\r\n:1\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+sdown\r\n:2\r\n*3\r\n$9\r\nsubscribe\r\n$6\r\n+odown\r\n:3\r\n"u8.ToArray(),
                    _ => "*0\r\n"u8.ToArray(),
                },
        };
    }

    private static FakeRespServer HostnameSentinel(int primaryPort)
    {
        var sentinel = Sentinel(() => primaryPort, () => 6);
        var reply = sentinel.ReplyOverride!;
        sentinel.ReplyOverride = (id, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR")
            ? AddressReply("owner.test", primaryPort)
            : command == "SENTINEL MASTER mymaster"
                ? Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(ConfigurationReply(primaryPort, 6)).Replace("127.0.0.1", "owner.test"))
                : reply(id, command);
        return sentinel;
    }

    private static byte[] ConfigurationReply(int port, long epoch, string flags = "master")
        => Encoding.ASCII.GetBytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{port}\r\n+config-epoch\r\n+{epoch}\r\n+flags\r\n+{flags}\r\n");

    private static byte[] AddressReply(int port) => AddressReply("127.0.0.1", port);

    private static byte[] AddressReply(string host, int port)
        => Encoding.ASCII.GetBytes($"*2\r\n${host.Length}\r\n{host}\r\n${port.ToString().Length}\r\n{port}\r\n");
}
