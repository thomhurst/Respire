using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReplicaReconnectBacklogTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();
    private static readonly byte[] Value = "$5\r\nvalue\r\n"u8.ToArray();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_sharedGate")]
    private static extern ref SemaphoreSlim SharedCursorGate(ReadCursorAffinity cursors);

    [Test]
    [Arguments("healthy", false)]
    [Arguments("healthy", true)]
    [Arguments("retired", false)]
    [Arguments("retired", true)]
    [Arguments("disconnected", false)]
    [Arguments("disconnected", true)]
    public async Task QueuedFreshCursorReselectsUnavailablePrimaryPin(string state, bool publishUnderGate)
    {
        await using var primary = new FakeRespServer(16, Value)
        {
            ReplyOverride = (_, command) => command == "ROLE" ? "*3\r\n+master\r\n:0\r\n*0\r\n"u8.ToArray() : null,
        };
        await using var replica = Server();
        int[] ports = [];
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command switch
            {
                "SENTINEL GET-MASTER-ADDR-BY-NAME mymaster" => Encoding.ASCII.GetBytes($"*2\r\n+127.0.0.1\r\n+{primary.Port}\r\n"),
                "SENTINEL MASTER mymaster" => Encoding.ASCII.GetBytes($"*8\r\n+ip\r\n+127.0.0.1\r\n+port\r\n+{primary.Port}\r\n+config-epoch\r\n+1\r\n+flags\r\n+master\r\n"),
                "SENTINEL REPLICAS mymaster" => ReplicasReply(Volatile.Read(ref ports)),
                _ => null,
            },
        };
        var blockPrimary = false;
        var resumePrimary = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == primary.Port && Volatile.Read(ref blockPrimary))
                    await resumePrimary.Task.WaitAsync(token);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var router = client.Core.ReadRouter;
        await using var view = client.WithReadFrom(RespireReadFrom.ReplicaPreferred);
        using (await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"])) { }
        await Assert.That(router.Cursors.TryGetShared(RespireReadFrom.ReplicaPreferred, out var original)).IsTrue();
        await Assert.That(original!.Primary is not null).IsTrue();
        Volatile.Write(ref ports, [replica.Port]);
        await router.RefreshNowAsync(default);
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);

        var gate = SharedCursorGate(router.Cursors);
        using var cancellation = new CancellationTokenSource(Limit);
        var gateHeld = false;
        try
        {
            if (publishUnderGate)
            {
                router.Cursors.Clear();
                await gate.WaitAsync();
                gateHeld = true;
            }
            if (state != "healthy")
            {
                Volatile.Write(ref blockPrimary, true);
                sentinel.SuppressReply = command => command.StartsWith("SENTINEL GET-MASTER", StringComparison.Ordinal);
                if (state == "retired") await Assert.That(client.Core.Sentinel!.Current!.TryRetire()).IsTrue();
                else await original.Primary!.GetConnection().DisposeAsync();
                await Assert.That(client.Core.TryGetReadyPrimaryMultiplexer(out _)).IsFalse();
            }
            var pending = router.EnqueueAsync(RespireReadFrom.ReplicaPreferred,
                static (connection, _) => new ValueTask<int>(connection.Port), cancellation.Token, default,
                cursorRead: true).AsTask();
            if (publishUnderGate)
            {
                await Assert.That(pending.IsCompleted).IsFalse();
                router.Cursors.PinShared(RespireReadFrom.ReplicaPreferred, original);
                gate.Release();
                gateHeld = false;
            }
            await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo(state == "healthy" ? primary.Port : replica.Port);
            using (await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"])) { }
            using (await view.ExecuteAsync(RespireCommands.Key.SCAN, ["7"])) { }
            await Assert.That((state == "healthy" ? primary : replica).ReceivedCommands.Contains("SCAN 7")).IsTrue();
            await Assert.That(router.Cursors.TryGetShared(RespireReadFrom.ReplicaPreferred, out var current)).IsTrue();
            await Assert.That(ReferenceEquals(current, original)).IsEqualTo(state == "healthy");
        }
        finally
        {
            if (gateHeld) gate.Release();
            await cancellation.CancelAsync();
            resumePrimary.TrySetResult();
        }
    }

    [Test]
    [Arguments(RespireReadFrom.Replica)]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    public async Task QueuedFreshRawCursorPreservesHealthySharedPin(RespireReadFrom policy)
    {
        await using var primary = Server();
        await using var first = Server();
        await using var second = Server();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        await using var view = client.WithReadFrom(policy);
        using (await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"])) { }
        await Assert.That(router.Cursors.TryGetShared(policy, out var original)).IsTrue();
        var issuer = original!.Replica!.Endpoint.Port == first.Port ? first : second;
        var other = ReferenceEquals(issuer, first) ? second : first;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // All read-policy views share the queue. A primary head holds admission without
        // changing the replica selector's next candidate.
        var head = router.EnqueueAsync(RespireReadFrom.Primary, async (_, token) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(token);
            return true;
        }, default, default).AsTask();
        try
        {
            await entered.Task.WaitAsync(Limit);
            var fresh = view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"]).AsTask();
            await Assert.That(fresh.IsCompleted).IsFalse();
            resume.TrySetResult();
            await head.WaitAsync(Limit);
            using (await fresh.WaitAsync(Limit)) { }
            using (await view.ExecuteAsync(RespireCommands.Key.SCAN, ["7"])) { }
            await Assert.That(router.Cursors.TryGetShared(policy, out var current)).IsTrue();
            await Assert.That(ReferenceEquals(current, original)).IsTrue();
            await Assert.That(issuer.ReceivedCommands.Count(command => command == "SCAN 0")).IsEqualTo(2);
            await Assert.That(issuer.ReceivedCommands.Contains("SCAN 7")).IsTrue();
            await Assert.That(other.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();
        }
        finally { resume.TrySetResult(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedCursorReselectionRechecksSharedPinUnderGate(bool rejectedPin)
    {
        await using var primary = Server();
        await using var first = Server();
        await using var second = Server();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        var original = new ReadAffinity();
        await router.GetCursorConnectionAsync(RespireReadFrom.Replica, original, false, default, default);
        var replacement = new ReadAffinity();
        var expected = await router.GetCursorConnectionAsync(RespireReadFrom.Replica, replacement, false, default, default);
        if (rejectedPin)
        {
            router.Cursors.PinShared(RespireReadFrom.Replica, original);
            router.FailedReplicaCooldown = TimeSpan.FromMinutes(1);
            original.Replica!.MarkFailed();
        }
        var gate = SharedCursorGate(router.Cursors);
        await gate.WaitAsync();
        Task<Respire.Networking.RespireConnection> pending;
        try
        {
            pending = router.GetCursorConnectionAsync(RespireReadFrom.Replica, null, false, default, default,
                reselectSharedCursor: true).AsTask();
            await Assert.That(pending.IsCompleted).IsFalse();
            // Model another publisher winning the gate before this request. It has
            // already issued a cursor on the replacement, which must remain pinned.
            router.Cursors.PinShared(RespireReadFrom.Replica, replacement);
        }
        finally { gate.Release(); }
        await Assert.That(ReferenceEquals(await pending.WaitAsync(Limit), expected)).IsTrue();
        await Assert.That(router.Cursors.TryGetShared(RespireReadFrom.Replica, out var current)).IsTrue();
        await Assert.That(ReferenceEquals(current, replacement)).IsTrue();
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("raw")]
    [Arguments("stream")]
    [Arguments("function")]
    [Arguments("script")]
    [Arguments("fire-and-forget")]
    [Arguments("hedged")]
    [Arguments("cursor")]
    public async Task InitialReplicaAcquisitionDoesNotSpendCommandTimeout(string surface)
    {
        await using var primary = Server();
        await using var replica = Server();
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandTimeout = TimeSpan.FromMilliseconds(500);
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, ConnectTimeout = Limit,
            CommandTimeout = commandTimeout, RetryPolicy = null,
            HedgedReads = surface == "hedged" ? new() { Delay = TimeSpan.FromMilliseconds(1) } : null,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == replica.Port)
                {
                    connecting.TrySetResult();
                    await resume.Task.WaitAsync(token);
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        await using var view = client.WithReadFrom(RespireReadFrom.Replica);
        var pending = ExecuteAsync((RespireClient)view, surface, "first");
        try
        {
            await connecting.Task.WaitAsync(Limit);
            // Keep setup held beyond the command budget, but inside ConnectTimeout.
            await Task.Delay(commandTimeout + TimeSpan.FromMilliseconds(200));
            await Assert.That(pending.IsCompleted).IsFalse();
            await Assert.That(client.Core.ReadRouter.IsBacklogDraining).IsFalse();
        }
        finally { resume.TrySetResult(); }
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("typed")]
    [Arguments("raw")]
    [Arguments("stream")]
    [Arguments("function")]
    [Arguments("script")]
    [Arguments("fire-and-forget")]
    [Arguments("hedged")]
    public async Task UnsentReadWaitsForRoleValidatedReplacement(string surface)
    {
        await using var fixture = await Fixture.CreateAsync(hedged: surface == "hedged");
        var pending = ExecuteAsync(fixture.View, surface, "queued");
        await fixture.Connecting.Task.WaitAsync(Limit);
        await Assert.That(pending.IsCompleted).IsFalse();
        fixture.Resume.TrySetResult();
        await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await fixture.View.GetStringAsync("barrier")).IsEqualTo("value");
        await Assert.That(fixture.Replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(2);
        await Assert.That(fixture.Primary.ReceivedCommands.Any(command => command.StartsWith("GET ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task FifoCapacityAndCancellationIncludeHeadWaitingForRecovery()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 2);
        using var caller = new CancellationTokenSource();
        var first = fixture.View.GetStringAsync("first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        var cancelled = fixture.View.GetStringAsync("cancelled", caller.Token).AsTask();
        await Assert.That(async () => await fixture.View.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await caller.CancelAsync();
        var error = await Assert.That(async () => await cancelled.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(caller.Token);
        var last = fixture.View.GetStringAsync("last").AsTask();
        fixture.Resume.TrySetResult();
        await first.WaitAsync(Limit);
        await last.WaitAsync(Limit);
        await Assert.That(fixture.Replica.ReceivedCommands.Where(command => command.StartsWith("GET ", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(new[] { "GET warm", "GET first", "GET last" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadyArrivalsCannotOvertakeDispatchedBacklogHead()
    {
        await using var fixture = await Fixture.CreateAsync(capacity: 1);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Replica.SuppressReply = command =>
        {
            if (command != "GET first") return false;
            received.TrySetResult();
            return true;
        };
        var first = fixture.View.GetStringAsync("first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        fixture.Resume.TrySetResult();
        await received.Task.WaitAsync(Limit);
        var second = fixture.View.GetStringAsync("second").AsTask();
        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("GET second")).IsFalse();
        await Assert.That(async () => await fixture.View.GetStringAsync("overflow").AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await fixture.Replica.SendRawAsync(Value, 1);
        await first.WaitAsync(Limit);
        await second.WaitAsync(Limit);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TimeoutAndDisposalRemoveUnsentReads(bool dispose)
    {
        await using var fixture = await Fixture.CreateAsync(timeout: dispose ? null : TimeSpan.FromMilliseconds(200));
        var pending = fixture.View.GetStringAsync("unsent").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        if (dispose)
        {
            await fixture.Client.DisposeAsync();
            await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<ObjectDisposedException>();
        }
        else
        {
            await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireTimeoutException>();
        }
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("GET unsent")).IsFalse();
    }

    [Test]
    public async Task FailFastDoesNotEnterQueue()
    {
        await using var fixture = await Fixture.CreateAsync(policy: RespireBacklogPolicy.FailFast);
        await Assert.That(async () => await fixture.View.GetStringAsync("unsent").AsTask().WaitAsync(Limit))
            .Throws<RespireConnectionException>();
        await Assert.That(fixture.Client.Core.ReadRouter.IsBacklogDraining).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedReadsOwnBorrowedArguments(bool fireAndForget)
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.View.GetStringAsync("first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        var bytes = "borrowed"u8.ToArray();
        async Task SendAsync()
        {
            if (fireAndForget) await fixture.View.ExecuteFireAndForgetAsync(RespireCommands.String.GET, [bytes]);
            else { using var reply = await fixture.View.ExecuteAsync(RespireCommands.String.GET, [bytes]); }
        }
        var pending = SendAsync();
        Array.Fill(bytes, (byte)'x');
        fixture.Resume.TrySetResult();
        await first.WaitAsync(Limit);
        await pending.WaitAsync(Limit);
        await fixture.View.GetStringAsync("barrier");
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("GET borrowed")).IsTrue();
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("GET xxxxxxxx")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FreshCursorCanQueueButContinuationCannotMove(bool fireAndForget)
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Replica.ReplyOverride = (_, command) => command == "ROLE" ? ReplicaRole
            : command.StartsWith("SCAN ", StringComparison.Ordinal) ? "*2\r\n$1\r\n7\r\n*0\r\n"u8.ToArray() : Value;
        var pending = fixture.View.ExecuteAsync(RespireCommands.Key.SCAN, ["0"]).AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        fixture.Resume.TrySetResult();
        using (await pending.WaitAsync(Limit)) { }
        fixture.Replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : Value;
        fixture.Client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.Zero;
        if (fireAndForget)
            await Assert.That(async () => await fixture.View.ExecuteFireAndForgetAsync(RespireCommands.Key.SCAN, ["7"]).AsTask().WaitAsync(Limit))
                .Throws<RespireConnectionException>();
        else
            await Assert.That(async () => await fixture.View.ExecuteAsync(RespireCommands.Key.SCAN, ["7"]).AsTask().WaitAsync(Limit))
                .Throws<RespireConnectionException>();
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("SCAN 7")).IsFalse();
        await Assert.That(fixture.Primary.ReceivedCommands.Any(command => command.StartsWith("SCAN ", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelReadBacklogWaitsForRoleCooldownBeforeSpendingRetry(bool remainsUnavailable)
    {
        await using var replica = Server();
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                ? ReplicasReply([replica.Port]) : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero, JitterRatio = 0 },
        });
        await using var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        var router = client.Core.ReadRouter;
        router.RoleRevalidationInterval = TimeSpan.Zero;
        router.FailedReplicaCooldown = TimeSpan.FromMilliseconds(500);
        replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*3\r\n$6\r\nmaster\r\n:0\r\n*0\r\n"u8.ToArray() : Value;
        await Assert.That(async () => await router.GetConnectionAsync(RespireReadFrom.Replica, default, default))
            .Throws<RespireConnectionException>();
        if (!remainsUnavailable) replica.ReplyOverride = (_, command) => command == "ROLE" ? ReplicaRole : Value;
        router.RoleRevalidationInterval = TimeSpan.FromMinutes(1);
        var first = view.GetStringAsync("first").AsTask();
        var second = view.GetStringAsync("second").AsTask();
        if (remainsUnavailable)
        {
            await Assert.That(async () => await first.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
            await Assert.That(async () => await second.WaitAsync(Limit)).ThrowsExactly<RespireReconnectLimitException>();
            // Two heads each get an initial actual ROLE attempt and one retry.
            await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(6);
            await Assert.That(replica.ReceivedCommands.Contains("GET first")).IsFalse();
            await Assert.That(replica.ReceivedCommands.Contains("GET second")).IsFalse();
            return;
        }
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await second.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(3);
        await Assert.That(replica.ReceivedCommands.Where(command => command.StartsWith("GET ", StringComparison.Ordinal)).ToArray())
            .IsEquivalentTo(new[] { "GET warm", "GET first", "GET second" }, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedFreshRawCursorBypassesSharedPin(bool fireAndForget)
    {
        await using var primary = Server();
        await using var first = Server();
        await using var second = Server();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        var affinity = new ReadAffinity();
        var pinned = await router.GetCursorConnectionAsync(RespireReadFrom.Replica, affinity, false, default, default);
        router.Cursors.PinShared(RespireReadFrom.Replica, affinity);
        var pinnedServer = pinned.Port == first.Port ? first : second;
        var replacement = pinned.Port == first.Port ? second : first;
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pinnedServer.GateCommand("ROLE", resume.Task);
        affinity.Replica!.MarkFailed();
        router.RoleRevalidationInterval = TimeSpan.Zero;
        await using var view = client.WithReadFrom(RespireReadFrom.Replica);
        try
        {
            if (fireAndForget) await view.ExecuteFireAndForgetAsync(RespireCommands.Key.SCAN, ["0"]).AsTask().WaitAsync(Limit);
            else { using var reply = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"]).AsTask().WaitAsync(Limit); }
            await view.GetStringAsync("barrier");
            await Assert.That(replacement.ReceivedCommands.Contains("SCAN 0")).IsTrue();
            await Assert.That(pinnedServer.ReceivedCommands.Contains("SCAN 0")).IsFalse();
            await Assert.That(router.Cursors.TryGetShared(RespireReadFrom.Replica, out var selected)).IsTrue();
            // A discarded fresh cursor has no returned continuation to publish. It leaves
            // the existing shared scan pin intact, while a reply-bearing scan replaces it.
            var continuationServer = fireAndForget ? pinnedServer : replacement;
            await Assert.That(selected!.Replica!.Endpoint.Port).IsEqualTo(continuationServer.Port);
            // An existing typed continuation still owns its original pin.
            await Assert.That(affinity.Replica.Endpoint.Port).IsEqualTo(pinnedServer.Port);
            resume.TrySetResult();
            router.RoleRevalidationInterval = TimeSpan.FromMinutes(1);
            using var continuation = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["7"]).AsTask().WaitAsync(Limit);
            await Assert.That(continuationServer.ReceivedCommands.Contains("SCAN 7")).IsTrue();
            await Assert.That((fireAndForget ? replacement : pinnedServer).ReceivedCommands.Contains("SCAN 7")).IsFalse();
        }
        finally { resume.TrySetResult(); }
    }

    [Test]
    public async Task PreferredReadUsesPermittedPrimaryFallback()
    {
        await using var fixture = await Fixture.CreateAsync();
        var preferred = fixture.Client.WithReadFrom(RespireReadFrom.ReplicaPreferred);
        await Assert.That(await preferred.GetStringAsync("fallback")).IsEqualTo("value");
        await Assert.That(fixture.Primary.ReceivedCommands.Contains("GET fallback")).IsTrue();
        await Assert.That(fixture.Client.Core.ReadRouter.IsBacklogDraining).IsFalse();
    }

    [Test]
    public async Task StrictReplicaWithNoConfiguredReplicasFailsAfterPrimaryFallback()
    {
        await using var primary = Server();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null,
        });
        // The public view API already rejects an empty configured set. Exercise the
        // router boundary directly so its recovery gate cannot wait on a static empty set.
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.ReplicaPreferred, default, default);
        var strict = router.SelectForDispatchAsync(RespireReadFrom.Replica, default, default).AsTask();
        await Assert.That(async () => await strict.WaitAsync(Limit)).ThrowsExactly<RespireConnectionException>();
        await Assert.That(client.Core.ReadRouter.IsBacklogDraining).IsFalse();
        await Assert.That(primary.ReceivedCommands.Contains("GET strict")).IsFalse();
    }

    [Test]
    public async Task StrictReplicaNeverConnectedFailsAfterPrimaryFallback()
    {
        await using var primary = Server();
        using var unavailable = new ReservedUnavailablePort();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", unavailable.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = null, ConnectTimeout = Limit,
        });
        await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("fallback");
        await Assert.That(async () => await client.Core.ReadRouter.SelectForDispatchAsync(
            RespireReadFrom.Replica, default, default).AsTask().WaitAsync(Limit))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(client.Core.ReadRouter.IsBacklogDraining).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadyReplicaAfterColdCandidateAvoidsBacklog(bool requiredValidation)
    {
        await using var primary = Server();
        await using var cold = Server();
        await using var healthy = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", cold.Port), new("127.0.0.1", healthy.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        var endpoints = ReadEndpointRouter.Order(client.Core.Options.ReplicaEndpoints);
        var rotation = typeof(ReadEndpointRouter).GetField("_nextReplica",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        rotation.SetValue(router, Array.FindIndex(endpoints, endpoint => endpoint.Port == healthy.Port) - 1);
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        if (requiredValidation)
        {
            var selection = await router.GetReplicaFromEndpointsAsync([new("127.0.0.1", cold.Port)], default, default);
            var health = (ReplicaHealth<Respire.Networking.RespireConnection>)typeof(ReadEndpointRouter.Entry)
                .GetField("_health", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(selection.Replica)!;
            var position = 0;
            Respire.Protocol.RespParser.TryParseValue(ReplicaRole, ref position, out var role);
            using (role)
                health.Record(selection.Connection, System.Diagnostics.Stopwatch.GetTimestamp()
                    - 150 * System.Diagnostics.Stopwatch.Frequency, in role);
        }
        cold.SuppressReply = command => command == "ROLE";
        rotation.SetValue(router, Array.FindIndex(endpoints, endpoint => endpoint.Port == cold.Port) - 1);
        var selected = await router.SelectForDispatchAsync(RespireReadFrom.Replica, default, default).AsTask().WaitAsync(Limit);
        await Assert.That(selected?.Port).IsEqualTo(healthy.Port);
        await Assert.That(router.IsBacklogDraining).IsFalse();
    }

    [Test]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    [Arguments(RespireReadFrom.AzAffinity)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task ColdReplicaUsesPermittedReadyPrimary(RespireReadFrom policy)
    {
        await using var primary = Server();
        await using var cold = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", cold.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = Limit,
        });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Primary, default, default);
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cold.SuppressReply = command =>
        {
            if (command != "ROLE") return false;
            validating.TrySetResult();
            return true;
        };
        var selected = await router.SelectForDispatchAsync(policy, default, default).AsTask().WaitAsync(Limit);
        await Assert.That(selected?.Port).IsEqualTo(primary.Port);
        await validating.Task.WaitAsync(Limit);
        await Assert.That(router.IsBacklogDraining).IsFalse();
        await cold.SendRawAsync(ReplicaRole);
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default).AsTask().WaitAsync(Limit);
        var recovered = await router.SelectForDispatchAsync(policy, default, default);
        await Assert.That(recovered?.Port).IsEqualTo(cold.Port);
        await Assert.That(cold.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(1);
    }

    [Test]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    [Arguments(RespireReadFrom.AzAffinity)]
    public async Task BackgroundValidationChecksTheSelectedSocket(RespireReadFrom policy)
    {
        await using var primary = Server();
        await using var replica = Server();
        var validated = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        var bothSockets = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.ReplyOverride = (id, command) =>
        {
            if (command != "ROLE") return Value;
            validated.TryAdd(id, 0);
            if (validated.Count == 2) bothSockets.TrySetResult();
            return ReplicaRole;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 2, CommandTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(5),
        });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        await Assert.That(validated.Count).IsEqualTo(1);
        // Force ROLE I/O even if the background helper incorrectly reselects the first
        // socket; otherwise a synchronous fresh hit can hide the extra rotation.
        router.RoleRevalidationInterval = TimeSpan.Zero;
        var resumeValidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.GateCommand("ROLE", resumeValidation.Task);
        // This selects the other socket. Background validation must retain that selection,
        // rather than rotating back to the already validated socket.
        try { await router.SelectForDispatchAsync(policy, default, default).AsTask().WaitAsync(Limit); }
        finally { resumeValidation.TrySetResult(); }
        await bothSockets.Task.WaitAsync(Limit);
        router.RoleRevalidationInterval = TimeSpan.FromMinutes(5);
        // Wait for the ROLE response to be consumed before checking ready-only routing.
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        for (var index = 0; index < 4; index++)
            await Assert.That((await router.SelectForDispatchAsync(policy, default, default))?.Port).IsEqualTo(replica.Port);
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(2);
    }

    [Test]
    public async Task ColdPrimaryUsesPermittedReadyReplica()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = Limit,
        });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        var selected = await router.SelectForDispatchAsync(RespireReadFrom.PrimaryPreferred, default, default).AsTask().WaitAsync(Limit);
        await Assert.That(selected?.Port).IsEqualTo(replica.Port);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
        await Assert.That(router.IsBacklogDraining).IsFalse();
    }

    [Test]
    public async Task NearestPendingProbeDoesNotQueueMeasuredCandidate()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        var pending = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        router.NearestLatency = new((connection, _) => connection.Port == primary.Port
            ? new ValueTask<long>(pending.Task) : ValueTask.FromResult(10L));
        try
        {
            var selected = await router.SelectForDispatchAsync(RespireReadFrom.Nearest, default, default);
            await Assert.That(selected?.Port).IsEqualTo(replica.Port);
            await Assert.That(router.IsBacklogDraining).IsFalse();
        }
        finally { pending.TrySetResult(100L); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RoleValidationDoesNotBlockEligibleCandidates(bool required)
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
            Protocol = RespireProtocol.Resp2, Connections = 1, CommandTimeout = Limit,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
        });
        var router = client.Core.ReadRouter;
        var warm = await router.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        var entry = (await router.SelectAsync(RespireReadFrom.Replica, default, default)).Replica!;
        var health = (ReplicaHealth<Respire.Networking.RespireConnection>)typeof(ReadEndpointRouter.Entry)
            .GetField("_health", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(entry)!;
        var position = 0;
        Respire.Protocol.RespParser.TryParseValue(ReplicaRole, ref position, out var role);
        using (role)
            health.Record(warm, System.Diagnostics.Stopwatch.GetTimestamp()
                - (required ? 150 : 90) * System.Diagnostics.Stopwatch.Frequency, in role);
        if (required)
            router.NearestLatency = new((connection, _) => ValueTask.FromResult(connection.Port == replica.Port ? 10L : 100L));
        var validating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        replica.SuppressReply = command =>
        {
            if (command != "ROLE") return false;
            validating.TrySetResult();
            return true;
        };
        try
        {
            var selected = await router.SelectForDispatchAsync(
                required ? RespireReadFrom.Nearest : RespireReadFrom.Replica, default, default);
            await Assert.That(selected?.Port).IsEqualTo(required ? primary.Port : replica.Port);
            await validating.Task.WaitAsync(Limit);
            await Assert.That(router.IsBacklogDraining).IsFalse();
        }
        finally { await replica.SendRawAsync(ReplicaRole, 0); }
        await router.GetConnectionAsync(RespireReadFrom.Replica, default, default).AsTask().WaitAsync(Limit);
        if (required)
        {
            var recovered = await router.SelectForDispatchAsync(RespireReadFrom.Nearest, default, default);
            await Assert.That(recovered?.Port).IsEqualTo(replica.Port);
        }
        await Assert.That(replica.ReceivedCommands.Count(command => command == "ROLE")).IsEqualTo(2);
    }

    [Test]
    public async Task SentinelReplacementDialOwnsBoundedAdmissionBeforeWaiting()
    {
        await using var first = Server();
        await using var replacement = Server();
        int[] ports = [first.Port];
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                ? ReplicasReply(Volatile.Read(ref ports)) : "*0\r\n"u8.ToArray(),
        };
        var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacementDials = 0;
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2, Connections = 1, ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            CommandTimeout = null, ConnectTimeout = Limit, BacklogCapacity = 2,
            TestingStreamFactory = async (host, port, token) =>
            {
                if (port == replacement.Port)
                {
                    Interlocked.Increment(ref replacementDials);
                    connecting.TrySetResult();
                    await resume.Task.WaitAsync(token);
                }
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        Volatile.Write(ref ports, [replacement.Port]);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        var head = view.GetStringAsync("first").AsTask();
        await connecting.Task.WaitAsync(Limit);
        await Assert.That(client.Core.ReadRouter.IsBacklogDraining).IsTrue();
        using var caller = new CancellationTokenSource();
        var cancelled = view.GetStringAsync("cancelled", caller.Token).AsTask();
        var overflow = view.GetStringAsync("overflow").AsTask();
        await Assert.That(async () => await overflow.WaitAsync(Limit)).ThrowsExactly<RespireConnectionException>();
        await caller.CancelAsync();
        var cancellation = await Assert.That(async () => await cancelled.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(cancellation!.CancellationToken).IsEqualTo(caller.Token);
        var last = view.GetStringAsync("last").AsTask();
        await Assert.That(replacementDials).IsEqualTo(1);
        await Assert.That(replacement.ReceivedCommands.Count).IsEqualTo(0);
        resume.TrySetResult();
        await Assert.That(await head.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(await last.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(replacement.ReceivedCommands.ToArray())
            .IsEquivalentTo(new[] { "ROLE", "GET first", "GET last" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task FreshAffinitySelectedBeforeConcurrentDrainReselectsAfterReplacement()
    {
        await using var first = Server();
        await using var replacement = Server();
        int[] ports = [first.Port];
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                ? ReplicasReply(Volatile.Read(ref ports)) : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2, Connections = 1, ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            CommandTimeout = null, ConnectTimeout = Limit,
        });
        var router = client.Core.ReadRouter;
        var fresh = new ReadAffinity();
        var continuation = new ReadAffinity();
        // Model the interleaving at selection/admission: the fresh scan has selected and
        // pinned its first server, then another read starts draining before it can dispatch.
        await router.SelectForDispatchAsync(RespireReadFrom.Replica, default, default, cursorRead: true, fresh);
        await router.GetCursorConnectionAsync(RespireReadFrom.Replica, continuation, false, default, default);
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var head = router.EnqueueAsync(RespireReadFrom.Replica, async (_, token) =>
        {
            dispatched.TrySetResult();
            await resume.Task.WaitAsync(token);
            return 0;
        }, default, default).AsTask();
        await dispatched.Task.WaitAsync(Limit);
        var queued = router.EnqueueAsync(RespireReadFrom.Replica,
            (connection, _) => ValueTask.FromResult(connection.Port), default, default, cursorRead: true, fresh).AsTask();
        await Assert.That(fresh.IsPinned).IsFalse();
        Volatile.Write(ref ports, [replacement.Port]);
        await router.RefreshNowAsync(default);
        resume.TrySetResult();
        await head.WaitAsync(Limit);
        await Assert.That(await queued.WaitAsync(Limit)).IsEqualTo(replacement.Port);
        await Assert.That(fresh.Replica!.Endpoint.Port).IsEqualTo(replacement.Port);
        await Assert.That(async () => await router.GetCursorConnectionAsync(
            RespireReadFrom.Replica, continuation, true, default, default)).Throws<RespireConnectionException>();
        await Assert.That(continuation.Replica!.Endpoint.Port).IsEqualTo(first.Port);
    }

    [Test]
    public async Task AcceptedFailureIsNotReplayed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Replica.SuppressReply = command =>
        {
            if (command != "GET accepted") return false;
            received.TrySetResult();
            return true;
        };
        var pending = fixture.View.GetStringAsync("accepted").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        fixture.Resume.TrySetResult();
        await received.Task.WaitAsync(Limit);
        var connection = await fixture.Client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default, default);
        await connection.DisposeAsync();
        await Assert.That(async () => await pending.WaitAsync(Limit)).Throws<Exception>();
        await Assert.That(fixture.Replica.ReceivedCommands.Count(command => command == "GET accepted")).IsEqualTo(1);
    }

    [Test]
    public async Task TerminalReconnectExhaustionEndsQueuedReads()
    {
        await using var fixture = await Fixture.CreateAsync(exhaust: true);
        var first = fixture.View.GetStringAsync("first").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        var second = fixture.View.GetStringAsync("second").AsTask();
        fixture.Resume.TrySetResult();
        await Assert.That(async () => await first.WaitAsync(Limit)).Throws<RespireReconnectLimitException>();
        await Assert.That(async () => await second.WaitAsync(Limit)).Throws<RespireReconnectLimitException>();
        await Assert.That(fixture.Replica.ReceivedCommands.Any(command => command is "GET first" or "GET second")).IsFalse();
    }

    [Test]
    public async Task QueuedPolicyViewsKeepTheirOwnFallbackRules()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = fixture.View.GetStringAsync("replica-only").AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        var preferred = fixture.Client.WithReadFrom(RespireReadFrom.PrimaryPreferred).GetStringAsync("preferred").AsTask();
        await Assert.That(preferred.IsCompleted).IsFalse();
        fixture.Resume.TrySetResult();
        await first.WaitAsync(Limit);
        await preferred.WaitAsync(Limit);
        await Assert.That(fixture.Replica.ReceivedCommands.Contains("GET replica-only")).IsTrue();
        await Assert.That(fixture.Primary.ReceivedCommands.Contains("GET preferred")).IsTrue();
        await Assert.That(fixture.Primary.ReceivedCommands.Contains("GET replica-only")).IsFalse();
    }

    [Test]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    [Arguments(RespireReadFrom.Nearest)]
    [Arguments(RespireReadFrom.AzAffinity)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task QueuedFallbackPolicyIsReappliedAfterHeadCancellation(RespireReadFrom policy)
    {
        await using var fixture = await Fixture.CreateAsync();
        using var caller = new CancellationTokenSource();
        var first = fixture.View.GetStringAsync("replica-only", caller.Token).AsTask();
        await fixture.Connecting.Task.WaitAsync(Limit);
        await WaitForDrainAsync(fixture.Client.Core.ReadRouter);
        var fallback = fixture.Client.WithReadFrom(policy).GetStringAsync("fallback").AsTask();
        await Assert.That(fallback.IsCompleted).IsFalse();
        await caller.CancelAsync();
        await Assert.That(async () => await first.WaitAsync(Limit)).Throws<OperationCanceledException>();
        await Assert.That(await fallback.WaitAsync(Limit)).IsEqualTo("value");
        await Assert.That(fixture.Primary.ReceivedCommands.Contains("GET fallback")).IsTrue();
        await Assert.That(fixture.Primary.ReceivedCommands.Contains("GET replica-only")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SentinelMembershipReplacementReselectsQueuedHead(bool circuitOpen)
    {
        await using var first = Server();
        await using var second = Server();
        int[] ports = [first.Port];
        await using var sentinel = new FakeRespServer(64, "*0\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL REPLICAS ", StringComparison.Ordinal)
                ? ReplicasReply(Volatile.Read(ref ports)) : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "mymaster",
            Protocol = RespireProtocol.Resp2, Connections = 1, ReplicaRefreshInterval = TimeSpan.FromMinutes(1),
            CommandTimeout = Limit, ConnectTimeout = Limit,
            CircuitBreaker = circuitOpen ? new() { MinimumFailureCount = 1, MaximumSampleCount = 1, OpenDuration = Limit } : null,
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await Assert.That(await view.GetStringAsync("warm")).IsEqualTo("value");
        Volatile.Write(ref ports, []);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        var pending = view.GetStringAsync("replacement").AsTask();
        await WaitForDrainAsync(client.Core.ReadRouter);
        var endpoint = new RespireEndpoint("127.0.0.1", second.Port);
        if (circuitOpen)
        {
            var admission = client.Core.Circuits!.Acquire(endpoint, default);
            admission.Failed(new RespireConnectionException("Offline"), default);
            admission.Dispose();
        }
        Volatile.Write(ref ports, [second.Port]);
        await client.Core.ReadRouter.RefreshNowAsync(default);
        await Assert.That(first.ReceivedCommands.Contains("GET replacement")).IsFalse();
        if (circuitOpen)
        {
            await Assert.That(async () => await pending.WaitAsync(Limit)).ThrowsExactly<RespireCircuitOpenException>();
            await Assert.That(second.ReceivedCommands.ToArray()).IsEquivalentTo(new[] { "ROLE" });
            await Assert.That(client.Core.Circuits!.GetForTests(endpoint).Snapshot().ActiveProbes).IsEqualTo(0);
        }
        else
        {
            await Assert.That(await pending.WaitAsync(Limit)).IsEqualTo("value");
            await Assert.That(second.ReceivedCommands.ToArray()).IsEquivalentTo(new[] { "ROLE", "GET replacement" }, CollectionOrdering.Matching);
        }
    }

    private static async Task WaitForDrainAsync(ReadEndpointRouter router)
    {
        using var timeout = new CancellationTokenSource(Limit);
        while (!router.IsBacklogDraining) await Task.Delay(1, timeout.Token);
    }

    private static byte[] ReplicasReply(int[] ports)
    {
        var reply = new StringBuilder($"*{ports.Length}\r\n");
        foreach (var port in ports)
        {
            var text = port.ToString();
            reply.Append($"*6\r\n$2\r\nip\r\n$9\r\n127.0.0.1\r\n$4\r\nport\r\n${text.Length}\r\n{text}\r\n$5\r\nflags\r\n$5\r\nslave\r\n");
        }
        return Encoding.ASCII.GetBytes(reply.ToString());
    }

    private static async Task<string> ExecuteAsync(RespireClient view, string surface, string key)
    {
        switch (surface)
        {
            case "bytes": return Encoding.UTF8.GetString((await view.GetBytesAsync(key))!);
            case "typed": return (await view.GetAsync<string>(key))!;
            case "stream":
                await using (var stream = await view.Strings.GetStreamAsync(key))
                using (var reader = new StreamReader(stream!)) return await reader.ReadToEndAsync();
            case "raw":
                using (var result = await view.ExecuteAsync(RespireCommands.String.GET, key)) return result.AsString()!;
            case "cursor":
                using (var result = await view.ExecuteAsync(RespireCommands.Key.SCAN, ["0"])) return result.AsString()!;
            case "function":
                using (var result = await view.ExecuteAsync(RespireCommands.Scripting.FCALL_RO, ["read-function", 0])) return result.AsString()!;
            case "script":
                using (var result = await view.Scripts.ExecuteAsync(RespireScript.Create("return 'value'", readOnly: true))) return result.AsString()!;
            case "fire-and-forget":
                await view.ExecuteFireAndForgetAsync(RespireCommands.String.GET, key); return "value";
            default: return (await view.GetStringAsync(key))!;
        }
    }

    private static FakeRespServer Server() => new(8, FakeRespServer.OkReply)
    {
        ReplyOverride = (_, command) => command == "ROLE" ? ReplicaRole : command == "PING" ? FakeRespServer.PongReply : Value,
    };

    private sealed class Fixture(FakeRespServer primary, FakeRespServer replica, RespireClient client,
        TaskCompletionSource connecting, TaskCompletionSource resume) : IAsyncDisposable
    {
        internal FakeRespServer Primary => primary;
        internal FakeRespServer Replica => replica;
        internal RespireClient Client => client;
        internal RespireClient View => (RespireClient)client.WithReadFrom(RespireReadFrom.Replica);
        internal TaskCompletionSource Connecting => connecting;
        internal TaskCompletionSource Resume => resume;

        internal static async Task<Fixture> CreateAsync(int capacity = 1024, TimeSpan? timeout = null,
            RespireBacklogPolicy policy = RespireBacklogPolicy.Queue, bool hedged = false, bool exhaust = false)
        {
            var primary = Server();
            var replica = Server();
            var connecting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var dials = 0;
            var client = RespireClient.Create(new RespireOptions
            {
                Endpoints = [new("127.0.0.1", primary.Port)], ReplicaEndpoints = [new("127.0.0.1", replica.Port)],
                Protocol = RespireProtocol.Resp2, Connections = 1, BacklogCapacity = capacity, BacklogPolicy = policy,
                ConnectTimeout = Limit, CommandTimeout = timeout, RetryPolicy = null,
                ReconnectPolicy = exhaust ? new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero, MaxDelay = TimeSpan.Zero } : null,
                ReplicaRefreshInterval = TimeSpan.FromMilliseconds(5),
                ClientAvailabilityZone = "local",
                HedgedReads = hedged ? new() { Delay = TimeSpan.FromMilliseconds(1) } : null,
                TestingStreamFactory = async (host, port, token) =>
                {
                    if (port == replica.Port && Interlocked.Increment(ref dials) > 1)
                    {
                        connecting.TrySetResult();
                        await resume.Task.WaitAsync(token);
                        if (exhaust) throw new IOException("Replica is offline.");
                    }
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(host, port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                },
            });
            try
            {
                var fixture = new Fixture(primary, replica, client, connecting, resume);
                client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.FromMinutes(1);
                // Setup must not spend the deliberately short command deadline used by the
                // timeout test. The router selection still connects, validates and records history.
                await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default, default);
                if (timeout is null) await fixture.View.GetStringAsync("warm");
                var connection = await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default, default);
                await connection.DisposeAsync();
                return fixture;
            }
            catch
            {
                await client.DisposeAsync();
                await replica.DisposeAsync();
                await primary.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            await replica.DisposeAsync();
            await primary.DisposeAsync();
        }
    }
}
