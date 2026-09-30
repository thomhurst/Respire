using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReconnectPolicyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SeedFallbackUsesOneConfiguredBudget(bool configured)
    {
        await using var first = new FakeRespServer("-ERR first unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR second unavailable\r\n"u8.ToArray());
        await using var last = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR topology unavailable\r\n"u8.ToArray(), FakeRespServer.PongReply);
        var options = new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, Password = "test",
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port), new("127.0.0.1", last.Port)],
            ConnectTimeout = TimeSpan.FromSeconds(5), CommandTimeout = null,
            ReconnectPolicy = configured ? new()
            {
                InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1,
            } : null,
        };
        if (configured)
        {
            await Assert.That(async () =>
            {
                await using var unexpected = await RespireClient.ConnectAsync(options);
            }).ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(last.CommandsSeen).IsEqualTo(0);
        }
        else
        {
            await using var client = await RespireClient.ConnectAsync(options);
            await client.PingAsync();
            await Assert.That(last.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS", "PING"]);
        }
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ScheduledSeedFallbackStopsForCallerCancellationOrDisposal(bool disposeClient)
    {
        await using var first = new FakeRespServer("-ERR first unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(FakeRespServer.OkReply,
            "-ERR topology unavailable\r\n"u8.ToArray(), FakeRespServer.PongReply);
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, Password = "test", CommandTimeout = null,
            Endpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30),
                JitterRatio = 0, MaxAttempts = 1 },
        });
        var clock = new GatedDiscoveryClock();
        client.Core.Cluster!.DiscoveryClock = clock;
        using var caller = new CancellationTokenSource();
        var scheduled = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            if (change.NextReconnectDelay is not null) scheduled.TrySetResult(change);
            if (change.SourceState == RespireConnectionState.Disconnected) ended.TrySetResult(change);
        };
        var command = client.PingAsync(caller.Token).AsTask();
        try
        {
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposeClient) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            else caller.Cancel();
            var error = await Assert.That(async () => await command.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            if (!disposeClient)
            {
                await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
                var terminal = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(terminal.Error).IsSameReferenceAs(error);
                await Assert.That(terminal.ReconnectEpisodeId).IsEqualTo(scheduled.Task.Result.ReconnectEpisodeId);
                await Assert.That(terminal.ReconnectAttempt).IsEqualTo(1);
                await Assert.That(terminal.ReconnectExhausted).IsFalse();
            }
            await Assert.That(second.CommandsSeen).IsEqualTo(0);
        }
        finally
        {
            caller.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
    }

    [Test]
    [Arguments("command")]
    [Arguments("tracked")]
    [Arguments("dedicated")]
    [Arguments("masters")]
    [Arguments("known-masters")]
    [Arguments("pubsub")]
    public async Task NestedEntryPointsShareSeedFallbackBudget(string path)
    {
        await using var first = new FakeRespServer("-ERR first unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR second unavailable\r\n"u8.ToArray());
        await using var last = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(Options(first.Port, second.Port, last.Port));
        var router = client.Core.Cluster!;
        await Assert.That(async () =>
        {
            switch (path)
            {
                case "command": await router.GetConnectionAsync(42, default, discovery: null); break;
                case "tracked": await router.GetTrackedConnectionAsync(42, true, default, discovery: null); break;
                case "dedicated": await router.GetDedicatedPoolAsync(42, default, discovery: null); break;
                case "masters": await router.GetMasterConnectionsAsync(default, discovery: null); break;
                case "known-masters": await router.GetKnownMastersAsync(default, discovery: null); break;
                case "pubsub": await router.GetPubSubEndpointAsync(default); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(last.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    public async Task ConcurrentSeedCallersShareSuccessfulDiscovery()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var second = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = RespireClient.Create(Options(first.Port, second.Port));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.SourceState == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var callers = Enumerable.Range(0, 8).Select(_ => client.Core.Cluster!.EnsureConnectedAsync(timeout.Token, discovery: null).AsTask()).ToArray();
        await received.Task.WaitAsync(timeout.Token);
        await first.SendRawAsync("-ERR unavailable\r\n"u8.ToArray());
        await Task.WhenAll(callers).WaitAsync(timeout.Token);
        await recovered.Task.WaitAsync(timeout.Token);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
        var attempts = changes.Where(change => change.NextReconnectDelay is not null).ToArray();
        await Assert.That(attempts.Length).IsEqualTo(1);
        await Assert.That(attempts[0].Endpoint.Port).IsEqualTo(second.Port);
        await Assert.That(attempts[0].ReconnectAttempt).IsEqualTo(1);
        await Assert.That(changes.Any(change => change.ReconnectExhausted)).IsFalse();
    }

    [Test]
    [Arguments("MOVED", "command")]
    [Arguments("ASK", "command")]
    [Arguments("READONLY", "command")]
    [Arguments("MOVED", "tracked")]
    [Arguments("ASK", "tracked")]
    [Arguments("READONLY", "tracked")]
    [Arguments("MOVED", "dedicated")]
    [Arguments("ASK", "dedicated")]
    [Arguments("READONLY", "dedicated")]
    public async Task UnavailableRedirectPreservesOriginalRejection(string code, string path)
    {
        await using var target = new FakeRespServer("-ERR target unavailable\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, target.Port));
        var router = client.Core.Cluster!;
        var source = await router.GetConnectionAsync(null, default, discovery: null);
        var original = new RespireServerException(code == "READONLY" ? "READONLY demoted"
            : $"{code} 42 127.0.0.1:{target.Port}");
        var failure = await Assert.That(async () =>
        {
            switch (path)
            {
                case "command": await router.GetRedirectConnectionAsync(original, source, default, 42, discovery: null); break;
                case "tracked": await router.GetTrackedRedirectConnectionAsync(original, source, true, default, 42, discovery: null); break;
                case "dedicated": await router.GetRedirectDedicatedPoolAsync(original, source, default, 42, discovery: null); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(ReferenceEquals(original, failure)).IsTrue();
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
    }

    [Test]
    public async Task AcceptedCommandWithLostReplyIsNeverReplayed()
    {
        await using var unused = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "*0\r\n"u8.ToArray(), FakeRespServer.OkReply)
        {
            CloseConnectionAfterCommand = 3,
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, unused.Port));
        await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireConnectionException>();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS", "SET key value"]);
        await Assert.That(unused.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments("command")]
    [Arguments("tracked")]
    [Arguments("dedicated")]
    [Arguments("pubsub")]
    public async Task KnownOwnerFailuresCannotRestartBudgetAtSeeds(string path)
    {
        await using var first = new FakeRespServer("-ERR owner unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR peer unavailable\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var router = client.Core.Cluster!;
        router.SetSlotOwner(42, router.GetMultiplexer(new("127.0.0.1", first.Port)));
        router.SetSlotOwner(43, router.GetMultiplexer(new("127.0.0.1", second.Port)));
        await Assert.That(async () =>
        {
            switch (path)
            {
                case "command": await router.GetConnectionAsync(42, default, discovery: null); break;
                case "tracked": await router.GetTrackedConnectionAsync(42, true, default, discovery: null); break;
                case "dedicated": await router.GetDedicatedPoolAsync(42, default, discovery: null); break;
                case "pubsub": await router.GetPubSubEndpointAsync(default); break;
                default: throw new ArgumentOutOfRangeException(nameof(path));
            }
        }).ThrowsExactly<RespireReconnectLimitException>();
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DiscoveryMetadataPreservesPhysicalSlotHealth(bool failedPhysicalSlot)
    {
        await using var unavailable = new FakeRespServer("-ERR owner unavailable\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var router = client.Core.Cluster!;
        var endpoint = new RespireEndpoint("127.0.0.1", seed.Port);
        if (failedPhysicalSlot)
            client.Core.NotifyCommandStateChanged(router.GetMultiplexer(endpoint), 0,
                new(endpoint, RespireConnectionState.Disconnected, new RespireConnectionException("physical failure")));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.SourceState == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        router.SetSlotOwner(42, router.GetMultiplexer(new("127.0.0.1", unavailable.Port)));
        var connection = await router.GetConnectionAsync(42, default, discovery: null);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.Port).IsEqualTo(seed.Port);
        var events = changes.ToArray();
        await Assert.That(events.Length).IsEqualTo(2);
        foreach (var change in events)
        {
            await Assert.That(change.Endpoint).IsEqualTo(endpoint);
            await Assert.That(change.State).IsEqualTo(failedPhysicalSlot
                ? RespireConnectionState.Disconnected : RespireConnectionState.Connected);
            await Assert.That(change.ReconnectAttempt).IsEqualTo(1);
            await Assert.That(change.ConnectionSlot).IsNull();
            await Assert.That(change.ReconnectEpisodeId).IsEqualTo(events[0].ReconnectEpisodeId);
            await Assert.That(change.ReconnectExhausted).IsFalse();
        }
        await Assert.That(events[0].SourceState).IsEqualTo(RespireConnectionState.Reconnecting);
        await Assert.That(events[1].SourceState).IsEqualTo(RespireConnectionState.Connected);
    }

    [Test]
    public async Task FallbackAttemptsUseSharedBackoffAndEndpointMetrics()
    {
        await using var first = new FakeRespServer("-ERR first unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer("-ERR second unavailable\r\n"u8.ToArray());
        await using var last = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        using var firstMetrics = new DiscoveryMetrics(second.Port);
        using var lastMetrics = new DiscoveryMetrics(last.Port);
        var options = Options(first.Port, second.Port, last.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromMilliseconds(10), BackoffMultiplier = 3,
                JitterRatio = 0, MaxAttempts = 2 },
        };
        await using var client = RespireClient.Create(options);
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.SourceState == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        await client.Core.Cluster!.EnsureConnectedAsync(default, discovery: null);
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(firstMetrics.Attempts.ToArray()).IsEquivalentTo([1L]);
        await Assert.That(lastMetrics.Attempts.ToArray()).IsEquivalentTo([2L]);
        await Assert.That(firstMetrics.Delays.ToArray()).IsEquivalentTo([0.01]);
        await Assert.That(lastMetrics.Delays.ToArray()).IsEquivalentTo([0.03]);
        await Assert.That(firstMetrics.Exhaustions).IsEqualTo(0L);
        await Assert.That(lastMetrics.Exhaustions).IsEqualTo(0L);
        var events = changes.ToArray();
        await Assert.That(events.Length).IsEqualTo(3);
        await Assert.That(events.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(events[0].ReconnectEpisodeId is > 0).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FallbackHandshakeStopsForCallerCancellationOrDisposal(bool disposeClient)
    {
        await using var first = new FakeRespServer("-ERR unavailable\r\n"u8.ToArray());
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var second = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = _ => { connecting.TrySetResult(); return true; },
        };
        await using var client = RespireClient.Create(Options(first.Port, second.Port) with
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
        });
        using var caller = new CancellationTokenSource();
        var ended = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource == RespireReconnectSource.ClusterDiscovery
                && change.SourceState == RespireConnectionState.Disconnected) ended.TrySetResult(change);
        };
        var command = client.Core.Cluster!.EnsureConnectedAsync(caller.Token, discovery: null).AsTask();
        try
        {
            await connecting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposeClient) await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            else caller.Cancel();
            var error = await Assert.That(async () => await command.WaitAsync(TimeSpan.FromSeconds(5))).Throws<Exception>();
            await Assert.That(error is TimeoutException).IsFalse();
            if (!disposeClient)
            {
                await Assert.That(error is OperationCanceledException).IsTrue();
                await Assert.That(((OperationCanceledException)error!).CancellationToken).IsEqualTo(caller.Token);
                var terminal = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Assert.That(terminal.Error).IsSameReferenceAs(error);
                await Assert.That(terminal.ReconnectAttempt).IsEqualTo(1);
                await Assert.That(terminal.ReconnectExhausted).IsFalse();
            }
            await second.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        }
        finally
        {
            caller.Cancel();
            try { await command.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (command.IsCompleted) { }
        }
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task SuccessfulRedirectKeepsPermanentAndTemporaryRouting(string code)
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ") ? "$5\r\nvalue\r\n"u8.ToArray() : null,
        };
        var slot = ClusterHash.GetSlot("key");
        var redirect = Encoding.ASCII.GetBytes($"-{code} {slot} 127.0.0.1:{target.Port}\r\n");
        await using var seed = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? "*0\r\n"u8.ToArray()
                : command.StartsWith("GET ") ? redirect : null,
        };
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(seed.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(code == "ASK" ? 2 : 1);
        await Assert.That(target.ReceivedCommands.Count(command => command == "GET key")).IsEqualTo(2);
        await Assert.That(target.ReceivedCommands.Count(command => command == "ASKING")).IsEqualTo(code == "ASK" ? 2 : 0);
    }

    [Test]
    public async Task ReadOnlyRecoveryDeadlineBoundsConfiguredDelay()
    {
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "*0\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, target.Port) with
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(500),
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30),
                MaxAttempts = 1, JitterRatio = 0 },
        });
        var router = client.Core.Cluster!;
        var source = await router.GetConnectionAsync(null, default, discovery: null);
        var original = new RespireServerException("READONLY demoted");
        var error = await Assert.That(async () => await router.GetRedirectConnectionAsync(original, source, default, 42, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5))).ThrowsExactly<RespireServerException>();
        await Assert.That(ReferenceEquals(error, original)).IsTrue();
        await Assert.That(target.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ExhaustionRequiresUntriedCandidateAndNewRoundsStartFresh(bool remainingCandidate)
    {
        await using var first = new FakeRespServer(2, "-ERR first unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(2, "-ERR second unavailable\r\n"u8.ToArray());
        await using var last = new FakeRespServer(FakeRespServer.OkReply);
        using var metrics = new DiscoveryMetrics(second.Port);
        await using var client = RespireClient.Create(remainingCandidate
            ? Options(first.Port, second.Port, last.Port) : Options(first.Port, second.Port));
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var episodes = new ConcurrentQueue<long?>();
        var ended = 0;
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery
                || change.SourceState != RespireConnectionState.Disconnected) return;
            episodes.Enqueue(change.ReconnectEpisodeId);
            if (Interlocked.Increment(ref ended) == 2) terminal.TrySetResult();
        };
        for (var round = 0; round < 2; round++)
        {
            var error = await Assert.That(async () => await client.Core.Cluster!.EnsureConnectedAsync(default, discovery: null))
                .Throws<RespireConnectionException>();
            await Assert.That(error is RespireReconnectLimitException).IsEqualTo(remainingCandidate);
        }
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(first.CommandsSeen).IsEqualTo(2);
        await Assert.That(second.CommandsSeen).IsEqualTo(2);
        await Assert.That(last.CommandsSeen).IsEqualTo(0);
        await Assert.That(metrics.Attempts.ToArray()).IsEquivalentTo([1L, 1L]);
        await Assert.That(metrics.Exhaustions).IsEqualTo(remainingCandidate ? 2L : 0L);
        await Assert.That(episodes.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FailedSeedRefreshUsesNextDistinctMaster(bool configured)
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes("*2\r\n" + Range(0, 8191, seed.Port) + Range(8192, 16383, healthy.Port));
        var seedQueries = 0;
        seed.ReplyOverride = (_, command) =>
        {
            if (command != "CLUSTER SLOTS") return null;
            return Interlocked.Increment(ref seedQueries) == 1 ? topology : "-ERR seed topology unavailable\r\n"u8.ToArray();
        };
        healthy.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null;
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with
        {
            ReconnectPolicy = configured ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null,
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(timeout.Token, discovery: null);
        await Assert.That(connections.Select(connection => connection.Port)).IsEquivalentTo([seed.Port, healthy.Port]);
        await Assert.That(seedQueries).IsEqualTo(2);
        await Assert.That(healthy.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);

        static string Range(int start, int end, int port)
            => $"*3\r\n:{start}\r\n:{end}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n";
    }

    [Test]
    [Arguments("command", false)]
    [Arguments("command", true)]
    [Arguments("tracked", false)]
    [Arguments("tracked", true)]
    [Arguments("dedicated", false)]
    [Arguments("dedicated", true)]
    public async Task FailedCachedOwnerUsesNextDistinctMaster(string path, bool configured)
    {
        await using var failed = new FakeRespServer(2, "-ERR owner unavailable\r\n"u8.ToArray());
        await using var healthy = new FakeRespServer(2, FakeRespServer.OkReply);
        healthy.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{healthy.Port}\r\n")
            : null;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port) with
        {
            ReconnectPolicy = configured ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 } : null,
        });
        var router = client.Core.Cluster!;
        var failedOwner = router.GetMultiplexer(new("127.0.0.1", failed.Port));
        router.SetSlotOwner(42, failedOwner);
        router.SetSlotOwner(43, failedOwner); // Clearing slot 42 must leave this owner in the master list.
        router.SetSlotOwner(44, router.GetMultiplexer(new("127.0.0.1", healthy.Port)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (path == "dedicated")
        {
            var pool = await router.GetDedicatedPoolAsync(42, timeout.Token, discovery: null);
            var connection = await pool.RentAsync(timeout.Token);
            try { await Assert.That(connection.Port).IsEqualTo(healthy.Port); }
            finally { pool.Return(connection); }
        }
        else
        {
            var connection = path == "tracked"
                ? await router.GetTrackedConnectionAsync(42, true, timeout.Token, discovery: null)
                : await router.GetConnectionAsync(42, timeout.Token, discovery: null);
            await Assert.That(connection.Port).IsEqualTo(healthy.Port);
        }
        await Assert.That(failed.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(healthy.ReceivedCommands.Count(command => command == "CLUSTER SLOTS")).IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
    }

    [Test]
    [Arguments("command", 1)]
    [Arguments("command", 2)]
    [Arguments("tracked", 1)]
    [Arguments("tracked", 2)]
    [Arguments("dedicated", 1)]
    [Arguments("dedicated", 2)]
    [Arguments("unkeyed", 1)]
    [Arguments("unkeyed", 2)]
    [Arguments("pubsub", 1)]
    [Arguments("pubsub", 2)]
    [Arguments("masters", 1)]
    [Arguments("masters", 2)]
    [Arguments("known-masters", 1)]
    [Arguments("known-masters", 2)]
    public async Task ConfiguredSeedsSkipPreviouslyRejectedGenerations(string path, int failedCount)
    {
        await using var first = new FakeRespServer(4, "-ERR owner unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(4, "-ERR master unavailable\r\n"u8.ToArray());
        await using var healthy = new FakeRespServer(2, FakeRespServer.OkReply);
        healthy.ReplyOverride = (_, command) => command switch
        {
            "CLUSTER SLOTS" => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{healthy.Port}\r\n"),
            "CLIENT ID" => ":1\r\n"u8.ToArray(),
            _ => null,
        };
        await using var client = RespireClient.Create(Options(failedCount == 1
            ? [first.Port, healthy.Port] : [first.Port, second.Port, healthy.Port]) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = failedCount },
        });
        var router = client.Core.Cluster!;
        router.SetSlotOwner(42, router.GetMultiplexer(new("127.0.0.1", first.Port)));
        router.SetSlotOwner(43, router.GetMultiplexer(new("127.0.0.1", first.Port)));
        if (failedCount == 2) router.SetSlotOwner(44, router.GetMultiplexer(new("127.0.0.1", second.Port)));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DiscoveryStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.SourceState == RespireConnectionState.Connected) recovered.TrySetResult();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        int port;
        switch (path)
        {
            case "tracked": port = (await router.GetTrackedConnectionAsync(42, true, timeout.Token, discovery: null)).Port; break;
            case "dedicated":
                var pool = await router.GetDedicatedPoolAsync(42, timeout.Token, discovery: null);
                var connection = await pool.RentAsync(timeout.Token);
                try { port = connection.Port; }
                finally { pool.Return(connection); }
                break;
            case "pubsub": port = (await router.GetPubSubEndpointAsync(timeout.Token)).Port; break;
            case "masters": port = (await router.GetMasterConnectionsAsync(timeout.Token, discovery: null)).Single().Port; break;
            case "known-masters": port = (await router.GetKnownMastersAsync(timeout.Token, discovery: null)).Single().Port; break;
            default: port = (await router.GetConnectionAsync(path == "unkeyed" ? null : 42, timeout.Token, discovery: null)).Port; break;
        }
        await Assert.That(port).IsEqualTo(healthy.Port);
        await recovered.Task.WaitAsync(timeout.Token);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands.Count).IsEqualTo(failedCount == 1 ? 0 : 1);
        var attempts = changes.Where(change => change.NextReconnectDelay is not null).ToArray();
        await Assert.That(attempts.Length).IsEqualTo(failedCount);
        await Assert.That(attempts[^1].Endpoint.Port).IsEqualTo(healthy.Port);
        await Assert.That(changes.Any(change => change.ReconnectExhausted)).IsFalse();
        await Assert.That(changes.Last().Error).IsNull();
    }

    [Test]
    public async Task RejectedConnectedSeedDoesNotBypassDistinctFallback()
    {
        await using var first = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        var queries = 0;
        first.ReplyOverride = (_, command) => command != "CLUSTER SLOTS" ? null
            : Interlocked.Increment(ref queries) == 1 ? Topology(first.Port) : "-ERR topology unavailable\r\n"u8.ToArray();
        healthy.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Topology(healthy.Port) : null;
        await using var client = await RespireClient.ConnectAsync(Options(first.Port, healthy.Port));
        var router = client.Core.Cluster!;
        var connections = await router.GetMasterConnectionsAsync(default, discovery: null);
        await Assert.That(connections.Single().Port).IsEqualTo(healthy.Port);
        await Assert.That((await router.GetKnownMastersAsync(default, discovery: null)).Single().Port).IsEqualTo(healthy.Port);
        await Assert.That(queries).IsEqualTo(2);

        static byte[] Topology(int port)
            => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyCandidatesSkipEveryRejectedOwnerGeneration(bool configured)
    {
        await using var sourceServer = new FakeRespServer(FakeRespServer.OkReply);
        await using var originalOwner = new FakeRespServer(3, "-ERR original owner unavailable\r\n"u8.ToArray());
        await using var nextOwner = new FakeRespServer("-ERR discovered owner unavailable\r\n"u8.ToArray());
        await using var probe = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(FakeRespServer.OkReply);
        // Keep the rejected original generation in the refreshed topology so an address-level
        // replacement does not accidentally make the duplicate candidate a distinct generation.
        probe.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes("*2\r\n" + Range(0, 41, originalOwner.Port) + Range(42, 16383, nextOwner.Port)) : null;
        healthy.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes("*1\r\n" + Range(0, 16383, healthy.Port)) : null;
        await using var client = RespireClient.Create(Options(probe.Port, originalOwner.Port, healthy.Port) with
        {
            ReconnectPolicy = configured ? new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 3 } : null,
        });
        await using var source = await Respire.Networking.RespireConnection.ConnectAsync("127.0.0.1", sourceServer.Port);
        var router = client.Core.Cluster!;
        var originalNode = router.GetMultiplexer(new("127.0.0.1", originalOwner.Port));
        router.SetSlotOwner(42, originalNode);
        router.SetSlotOwner(43, originalNode);
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var completed = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DiscoveryStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.NextReconnectDelay is null) completed.TrySetResult(change);
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var recovered = await router.GetRedirectConnectionAsync(new("READONLY demoted"), source, timeout.Token, 42, discovery: null);
        await Assert.That(recovered.Port).IsEqualTo(healthy.Port);
        await Assert.That(originalOwner.ReceivedCommands.Count).IsEqualTo(configured ? 1 : 2);
        await Assert.That(nextOwner.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(probe.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
        await Assert.That(healthy.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
        if (configured)
        {
            var terminal = await completed.Task.WaitAsync(timeout.Token);
            await Assert.That(terminal.SourceState).IsEqualTo(RespireConnectionState.Connected);
            await Assert.That(terminal.Error).IsNull();
            await Assert.That(terminal.ReconnectAttempt).IsEqualTo(3);
            await Assert.That(changes.Any(change => change.ReconnectExhausted)).IsFalse();
            await Assert.That(changes.Where(change => change.NextReconnectDelay is not null).Select(change => change.Endpoint.Port))
                .IsEquivalentTo([originalOwner.Port, probe.Port, healthy.Port]);
        }

        static string Range(int start, int end, int port)
            => $"*3\r\n:{start}\r\n:{end}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n";
    }

    [Test]
    public async Task RejectedSeedDepletionPreservesOriginalFailure()
    {
        await using var failed = new FakeRespServer(2, "-ERR owner unavailable\r\n"u8.ToArray());
        await using var client = RespireClient.Create(Options(failed.Port));
        var router = client.Core.Cluster!;
        router.SetSlotOwner(42, router.GetMultiplexer(new("127.0.0.1", failed.Port)));
        var error = await Assert.That(async () => await router.GetConnectionAsync(42, default, discovery: null))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.InnerException is RespireConnectionException).IsTrue();
        await Assert.That(error.InnerException!.Message.Contains("AUTH failed", StringComparison.Ordinal)).IsTrue();
        await Assert.That(failed.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
    }

    [Test]
    [Arguments("MOVED", null, false, true)]
    [Arguments("ASK", null, false, true)]
    [Arguments("READONLY", 42, false, true)]
    [Arguments("READONLY", null, false, false)]
    [Arguments("WRONGTYPE", 42, false, false)]
    [Arguments("MOVED", 42, true, false)]
    [Arguments("ASK", 42, true, false)]
    [Arguments("READONLY", 42, true, false)]
    public async Task FinalRoutingRejectionRequiresRecoverableCommand(string code, int? slot, bool noRedirect, bool failed)
    {
        await using var client = RespireClient.Create(Options(1));
        var round = new ClusterRouter.DiscoveryRound(client.Core.Cluster!, new());
        var error = new RespireServerException(code + " rejected");
        round.RecordCommandFailure(error, discoveryPending: false, slot, noRedirect);
        if (failed) await Assert.That(round.TerminalError).IsSameReferenceAs(error);
        else await Assert.That(round.TerminalError).IsNull();
        round.Finish();
    }

    [Test]
    public async Task DiscoveryRoundRejectsConcurrentMutationAndReleasesGuardAfterCancellation()
    {
        await using var client = RespireClient.Create(Options(1));
        var clock = new GatedDiscoveryClock();
        client.Core.Cluster!.DiscoveryClock = clock;
        var round = new ClusterRouter.DiscoveryRound(client.Core.Cluster, new()
        {
            InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30), JitterRatio = 0,
        });
        var endpoint = new RespireEndpoint("unused.invalid");
        // Even with a long configured delay, the initial candidate completes synchronously.
        var initial = round.BeforeCandidateAsync(endpoint, default);
        await Assert.That(initial.IsCompletedSuccessfully).IsTrue();
        await initial;
        round.Failed(endpoint, new IOException("first candidate failed"));
        using var cancellation = new CancellationTokenSource();
        var pending = round.BeforeCandidateAsync(endpoint, cancellation.Token).AsTask();
        try
        {
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(async () => await round.BeforeCandidateAsync(endpoint, default))
                .ThrowsExactly<ClusterRouter.DiscoveryRoundUsageException>();
            var original = new IOException("original Redis connection failure");
            var recorded = await Assert.That(() => round.Failed(original)).ThrowsExactly<IOException>();
            await Assert.That(recorded).IsSameReferenceAs(original);
            recorded = await Assert.That(() => round.Failed(endpoint, original)).ThrowsExactly<IOException>();
            await Assert.That(recorded).IsSameReferenceAs(original);
            recorded = await Assert.That(() => round.TerminalError = original).ThrowsExactly<IOException>();
            await Assert.That(recorded).IsSameReferenceAs(original);
            await Assert.That(round.HasPendingFailure).IsFalse();
            await Assert.That(round.TerminalError).IsNull();
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) { }
        }
        // Cancellation releases the guard, allowing the owner to report its terminal outcome.
        round.TerminalError = new OperationCanceledException(cancellation.Token);
        round.Finish();
    }

    [Test]
    [NotInParallel] // Keep time available for the real READONLY phase deadline and seed fallback.
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyPhaseCancellationDoesNotPoisonSuccessfulSeedFallback(bool cachedOwner)
    {
        await using var candidate = new FakeRespServer(FakeRespServer.OkReply);
        await using var target = new FakeRespServer(FakeRespServer.OkReply);
        target.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n") : null;
        await using var seed = new FakeRespServer(FakeRespServer.OkReply, "-ERR unsupported topology\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port, target.Port) with
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30),
                JitterRatio = 0, MaxAttempts = 2 },
        });
        var router = client.Core.Cluster!;
        var source = await router.GetConnectionAsync(null, default, discovery: null);
        router.SetSlotOwner(cachedOwner ? 42 : 43, router.GetMultiplexer(new("127.0.0.1", candidate.Port)));
        var clock = new GatedDiscoveryClock(autoCompleteLaterDelays: true);
        router.DiscoveryClock = clock;
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var terminal = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.DiscoveryStateChanged += change =>
        {
            changes.Enqueue(change);
            if (change.NextReconnectDelay is null) terminal.TrySetResult(change);
        };
        // The first backoff remains gated until the primary phase expires. Later backoffs
        // complete immediately, leaving the reserved seed phase free to recover.
        var recovered = await router.GetRedirectConnectionAsync(new("READONLY demoted"), source, default, 42, discovery: null)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(recovered.Port).IsEqualTo(target.Port);
        await Assert.That(candidate.CommandsSeen).IsEqualTo(0);
        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ended.SourceState).IsEqualTo(RespireConnectionState.Connected);
        await Assert.That(ended.Error).IsNull();
        await Assert.That(ended.ReconnectExhausted).IsFalse();
        await Assert.That(ended.ReconnectAttempt).IsEqualTo(2);
        await Assert.That(changes.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(1);
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(["AUTH test", "CLUSTER SLOTS"]);
    }

    [Test]
    public async Task SuccessfulRequiredMastersDoNotConsumeFallbackAttempts()
    {
        await using var unavailable = new FakeRespServer("-ERR seed unavailable\r\n"u8.ToArray());
        await using var first = new FakeRespServer(FakeRespServer.OkReply);
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        await using var third = new FakeRespServer(FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes("*3\r\n"
            + Range(0, 5460, first.Port) + Range(5461, 10921, second.Port) + Range(10922, 16383, third.Port));
        await using var seed = new FakeRespServer(FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? topology : null,
        };
        await using var client = RespireClient.Create(Options(unavailable.Port, seed.Port));
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null);
        await Assert.That(connections.Select(connection => connection.Port).ToArray())
            .IsEquivalentTo([first.Port, second.Port, third.Port]);
        await Assert.That(unavailable.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(first.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(second.ReceivedCommands).IsEquivalentTo(["AUTH test"]);
        await Assert.That(third.ReceivedCommands).IsEquivalentTo(["AUTH test"]);

        static string Range(int start, int end, int port)
            => $"*3\r\n:{start}\r\n:{end}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n";
    }

    [Test]
    public async Task DiscoveryObserverCanSynchronouslyDisposeClient()
    {
        await using var first = new FakeRespServer("-ERR seed unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(Options(first.Port, second.Port) with
        {
            ReconnectPolicy = new() { InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30),
                JitterRatio = 0, MaxAttempts = 1 },
        });
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery || change.NextReconnectDelay is null) return;
            try
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
                disposed.TrySetResult();
            }
            catch (Exception error) { disposed.TrySetException(error); }
        };
        var operation = client.Core.Cluster!.EnsureConnectedAsync(default, discovery: null).AsTask();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(async () => await operation.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
        await Assert.That(second.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ThrowingObserversAndLoggersDoNotStopLaterDiscoveryEvents(bool throwFromMeter)
    {
        await using var first = new FakeRespServer("-ERR unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(FakeRespServer.OkReply, "-ERR no topology\r\n"u8.ToArray());
        using var logger = new ThrowingWarningLogger();
        await using var client = RespireClient.Create(Options(first.Port, second.Port) with { LoggerFactory = logger });
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (throwFromMeter && instrument.Name == "respire.connection.reconnect.attempt")
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (DiscoveryMetrics.Matches(tags, second.Port)) throw new InvalidOperationException("Test meter failure.");
        });
        listener.Start();
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = 0;
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            Interlocked.Increment(ref events);
            if (change.NextReconnectDelay is not null && !throwFromMeter)
                throw new InvalidOperationException("Test observer failure.");
            if (change.SourceState == RespireConnectionState.Connected) terminal.TrySetResult();
        };
        await client.Core.Cluster!.EnsureConnectedAsync(default, discovery: null);
        await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(events).IsEqualTo(2);
        await Assert.That(logger.Warnings).IsGreaterThan(0);
    }

    [Test]
    public async Task ConcurrentFailedCallersOwnIndependentFallbackBudgets()
    {
        await using var first = new FakeRespServer(8, "-ERR unavailable\r\n"u8.ToArray());
        await using var second = new FakeRespServer(8, "-ERR unavailable\r\n"u8.ToArray());
        await using var unused = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(Options(first.Port, second.Port, unused.Port));
        var changes = new ConcurrentQueue<RespireConnectionStateChange>();
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exhausted = 0;
        client.ConnectionStateChanged += change =>
        {
            if (change.ReconnectSource != RespireReconnectSource.ClusterDiscovery) return;
            changes.Enqueue(change);
            if (change.ReconnectExhausted && Interlocked.Increment(ref exhausted) == 4) complete.TrySetResult();
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await Assert.That(async () => await client.Core.Cluster!.EnsureConnectedAsync(timeout.Token, discovery: null))
                .ThrowsExactly<RespireReconnectLimitException>();
        }));
        await complete.Task.WaitAsync(timeout.Token);
        var attempts = changes.Where(change => change.NextReconnectDelay is not null).ToArray();
        await Assert.That(attempts.Length).IsEqualTo(4);
        await Assert.That(attempts.All(change => change.ReconnectAttempt == 1)).IsTrue();
        await Assert.That(attempts.Select(change => change.ReconnectEpisodeId).Distinct().Count()).IsEqualTo(4);
        await Assert.That(unused.CommandsSeen).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FinishDuringBackoffPreservesExceptionAndPublishesTerminalOnce(bool cancel)
    {
        await using var client = RespireClient.Create(Options(1));
        var router = client.Core.Cluster!;
        var clock = new GatedDiscoveryClock();
        router.DiscoveryClock = clock;
        var endpoint = new RespireEndpoint("unused.invalid");
        var terminal = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEpisode = 0L;
        var terminalCount = 0;
        router.DiscoveryStateChanged += change =>
        {
            if (change.NextReconnectDelay is not null)
                Interlocked.CompareExchange(ref firstEpisode, change.ReconnectEpisodeId!.Value, 0);
            else if (change.ReconnectEpisodeId == Volatile.Read(ref firstEpisode))
            {
                Interlocked.Increment(ref terminalCount);
                terminal.TrySetResult(change);
            }
            else barrier.TrySetResult();
        };
        var round = new ClusterRouter.DiscoveryRound(router, new()
        {
            InitialDelay = TimeSpan.FromSeconds(30), MaxDelay = TimeSpan.FromSeconds(30), JitterRatio = 0,
        });
        round.Failed(endpoint, new IOException("initial failure"));
        using var cancellation = new CancellationTokenSource();
        var pending = round.BeforeCandidateAsync(endpoint, cancellation.Token).AsTask();
        var timer = await clock.Created.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var original = new IOException("original exception must survive finally");
        var propagated = await Assert.That(() =>
        {
            try { throw original; }
            finally { round.Finish(); round.Finish(); }
        }).ThrowsExactly<IOException>();
        await Assert.That(propagated).IsSameReferenceAs(original);
        OperationCanceledException? failure = null;
        if (cancel)
        {
            cancellation.Cancel();
            failure = await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
        }
        else
        {
            timer.Fire();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(ended.SourceState).IsEqualTo(cancel ? RespireConnectionState.Disconnected : RespireConnectionState.Connected);
        await Assert.That(ended.Error).IsSameReferenceAs(failure);
        await Assert.That(ended.ReconnectExhausted).IsFalse();
        round.Finish();
        round.Finish();
        var lateFailure = new IOException("late failure after finish");
        var late = await Assert.That(() => round.Failed(lateFailure)).ThrowsExactly<IOException>();
        await Assert.That(late).IsSameReferenceAs(lateFailure);

        // An independently queued episode is a FIFO barrier for duplicate terminal events.
        var next = new ClusterRouter.DiscoveryRound(router, new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0 });
        next.Failed(endpoint, new IOException());
        await next.BeforeCandidateAsync(endpoint, default);
        next.Finish();
        await barrier.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(terminalCount).IsEqualTo(1);
    }

    private sealed class GatedDiscoveryClock(bool autoCompleteLaterDelays = false) : TimeProvider
    {
        internal TaskCompletionSource<GatedTimer> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new GatedTimer(callback, state);
            if (!Created.TrySetResult(timer) && autoCompleteLaterDelays)
                return TimeProvider.System.CreateTimer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            return timer;
        }
    }

    private sealed class GatedTimer(TimerCallback callback, object? state) : ITimer
    {
        private int _finished;
        internal void Fire()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period) => Volatile.Read(ref _finished) == 0;
        public void Dispose() => Interlocked.Exchange(ref _finished, 1);
        public ValueTask DisposeAsync() { Dispose(); return default; }
    }

    private sealed class ThrowingWarningLogger : Microsoft.Extensions.Logging.ILoggerFactory, Microsoft.Extensions.Logging.ILogger
    {
        internal int Warnings;
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => level == Microsoft.Extensions.Logging.LogLevel.Warning;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (level != Microsoft.Extensions.Logging.LogLevel.Warning) return;
            Interlocked.Increment(ref Warnings);
            throw new InvalidOperationException("Test logger failure.");
        }
    }

    private sealed class DiscoveryMetrics : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _exhaustions;
        internal ConcurrentQueue<long> Attempts { get; } = new();
        internal ConcurrentQueue<double> Delays { get; } = new();
        internal long Exhaustions => Interlocked.Read(ref _exhaustions);
        internal DiscoveryMetrics(int port)
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "Respire" && instrument.Name.StartsWith("respire.connection.reconnect."))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (!Matches(tags, port)) return;
                if (instrument.Name.EndsWith(".attempt")) Attempts.Enqueue(value);
                if (instrument.Name.EndsWith(".exhausted")) Interlocked.Add(ref _exhaustions, value);
            });
            _listener.SetMeasurementEventCallback<double>((_, value, tags, _) =>
            {
                if (Matches(tags, port)) Delays.Enqueue(value);
            });
            _listener.Start();
        }
        internal static bool Matches(ReadOnlySpan<KeyValuePair<string, object?>> tags, int port)
        {
            var endpoint = false;
            var scope = false;
            foreach (var tag in tags)
            {
                endpoint |= tag.Key == "server.port" && tag.Value is int value && value == port;
                scope |= tag.Key == "respire.reconnect.scope" && Equals(tag.Value, "cluster-discovery");
            }
            return endpoint && scope;
        }
        public void Dispose() => _listener.Dispose();
    }

    private static RespireOptions Options(params int[] ports) => new()
    {
        UseCluster = true, Protocol = RespProtocol.Resp2, Connections = 1, Password = "test", CommandTimeout = null,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        Endpoints = ports.Select(port => new RespireEndpoint("127.0.0.1", port)).ToArray(),
        ReconnectPolicy = new() { InitialDelay = TimeSpan.Zero, JitterRatio = 0, MaxAttempts = 1 },
    };

}
