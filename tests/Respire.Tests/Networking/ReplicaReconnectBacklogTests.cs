using System.Net.Sockets;
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
                await fixture.View.GetStringAsync("warm");
                client.Core.ReadRouter.RoleRevalidationInterval = TimeSpan.FromMinutes(1);
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
