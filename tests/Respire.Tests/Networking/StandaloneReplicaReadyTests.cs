using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class StandaloneReplicaReadyTests
{
    private static readonly byte[] ReplicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();

    [Test]
    [Arguments(RespireReadFrom.Replica)]
    [Arguments(RespireReadFrom.ReplicaPreferred)]
    public async Task PreparedStringReadUsesSpecializedReplySource(RespireReadFrom policy)
    {
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var view = client.WithReadFrom(policy);
        await Assert.That(await view.GetStringAsync("warm")).IsEqualTo("hello");
        var connection = await client.Core.ReadRouter.GetConnectionAsync(policy, default);
        replica.SuppressReply = command => command.StartsWith("GET", StringComparison.Ordinal);
        var pending = view.GetStringAsync("parked");
        try
        {
            await Assert.That(connection.InspectForTests().Inflight.TryPeek(out var source)).IsTrue();
            await Assert.That(source).IsTypeOf<StringPendingResponseSource>();
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!replica.ReceivedCommands.Contains("GET parked")) await Task.Delay(1, deadline.Token);
            await replica.SendRawAsync("$5\r\nhello\r\n"u8.ToArray());
            await Assert.That(await pending).IsEqualTo("hello");
        }
        await Assert.That(primary.ReceivedCommands.Any(command => command.StartsWith("GET", StringComparison.Ordinal))).IsFalse();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_replicas")]
    private static extern ref RespireEndpoint[] PublishedReplicas(ReadEndpointRouter router);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_refs")]
    private static extern ref int OwnerReferences(PendingResponse source);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_nextReplica")]
    private static extern ref int ReplicaCursor(ReadEndpointRouter router);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readyReplica")]
    private static extern ref ReadEndpointRouter.ReadyReplica? ReadyPublication(ReadEndpointRouter router);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetEndpoints")]
    private static extern void SetEndpoints(ReadEndpointRouter router, IEnumerable<RespireEndpoint> endpoints);

    [Test]
    public async Task RepeatedReadyReadsReuseOnePublication()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var router = client.Core.ReadRouter;
        var connection = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(connection);
        var publication = Volatile.Read(ref ReadyPublication(router));
        await Assert.That(publication).IsNotNull();
        for (var i = 0; i < 32; i++)
        {
            await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(connection);
            await Assert.That(Volatile.Read(ref ReadyPublication(router))).IsSameReferenceAs(publication);
        }
    }

    [Test]
    public async Task RepublishingSameEndpointRejectsOldReadyPublication()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var router = client.Core.ReadRouter;
        var connection = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(connection);
        var old = Volatile.Read(ref ReadyPublication(router))!;
        SetEndpoints(router, [old.Entry.Endpoint]);
        await Assert.That(Volatile.Read(ref ReadyPublication(router))).IsNull();
        await Assert.That(old.Entry.TryGetReadyConnection(old)).IsNull();
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(connection);
        var current = Volatile.Read(ref ReadyPublication(router))!;
        await Assert.That(current.Entry).IsSameReferenceAs(old.Entry);
        await Assert.That(current.Entry.TryGetReadyConnection(current)).IsSameReferenceAs(connection);
        await Assert.That(old.Entry.TryGetReadyConnection(old)).IsNull();
    }

    [Test]
    public async Task RemovingAndReaddingEndpointCannotReuseRetiredEntry()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var router = client.Core.ReadRouter;
        var oldConnection = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(oldConnection);
        var old = Volatile.Read(ref ReadyPublication(router))!;
        SetEndpoints(router, []);
        await Assert.That(old.Entry.TryGetReadyConnection(old)).IsNull();
        SetEndpoints(router, [old.Entry.Endpoint]);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsNull();
        var connection = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(connection).IsNotSameReferenceAs(oldConnection);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(connection);
        await Assert.That(Volatile.Read(ref ReadyPublication(router))!.Entry).IsNotSameReferenceAs(old.Entry);
        await Assert.That(old.Entry.TryGetReadyConnection(old)).IsNull();
    }

    [Test]
    public async Task ReadyReadsKeepTheCursorWhenTopologyGrows()
    {
        await using var primary = Server();
        await using var first = Server();
        await using var second = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, first));
        var router = client.Core.ReadRouter;
        var prepared = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        var before = Volatile.Read(ref ReplicaCursor(router));
        for (var i = 0; i < 4; i++)
            await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(prepared);
        await Assert.That(Volatile.Read(ref ReplicaCursor(router))).IsEqualTo(unchecked(before + 4));
        Volatile.Write(ref PublishedReplicas(router), [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)]);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsNull();
        var expected = (uint)unchecked(before + 5) % 2 == 0 ? first.Port : second.Port;
        await Assert.That((await router.GetConnectionAsync(RespireReadFrom.Replica, default)).Port).IsEqualTo(expected);
        await Assert.That((await router.GetConnectionAsync(RespireReadFrom.Replica, default)).Port)
            .IsEqualTo(expected == first.Port ? second.Port : first.Port);
    }

    [Test]
    public async Task DeadReadyProbeLeavesRecoveryAndFallbackToTheSelector()
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            ReconnectPolicy = new()
            {
                InitialDelay = TimeSpan.FromHours(1), MaxDelay = TimeSpan.FromHours(1), JitterRatio = 0,
            },
        });
        var router = client.Core.ReadRouter;
        var selected = await router.GetReplicaFromEndpointsAsync(Options(primary, replica).ReplicaEndpoints.ToArray(), default);
        var multiplexer = selected.Connection.Multiplexer!;
        await selected.Connection.DisposeAsync();
        await Assert.That(selected.Replica!.TryGetReadyConnection()).IsNull();
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.ReplicaPreferred, default)).IsNull();
        await Assert.That(multiplexer.IsReconnecting).IsFalse();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Normal selection starts demand-driven recovery and preserves ReplicaPreferred's primary fallback.
        await Assert.That((await router.GetConnectionAsync(RespireReadFrom.ReplicaPreferred, deadline.Token)).Port)
            .IsEqualTo(primary.Port);
        await Assert.That(multiplexer.IsReconnecting).IsTrue();
        await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("fallback", deadline.Token))
            .IsEqualTo("hello");
        await Assert.That(primary.ReceivedCommands).Contains("GET fallback");
        await Assert.That(replica.ReceivedCommands.Contains("GET fallback")).IsFalse();
    }

    [Test]
    public async Task PublicationBeforeRetirementRejectsPreviouslyValidatedEntry()
    {
        await using var primary = Server();
        await using var oldReplica = Server();
        await using var replacement = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, oldReplica));
        var router = client.Core.ReadRouter;
        var selected = await router.GetReplicaFromEndpointsAsync(Options(primary, oldReplica).ReplicaEndpoints.ToArray(), default);
        await Assert.That(selected.Replica!.TryGetReadyConnection()).IsSameReferenceAs(selected.Connection);
        // Pause the topology transition after publication and before the removed-entry sweep.
        Volatile.Write(ref PublishedReplicas(router), [new("127.0.0.1", replacement.Port)]);
        await Assert.That(selected.Replica.TryGetReadyConnection()).IsNull();
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsNull();
        var newConnection = await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(newConnection.Port).IsEqualTo(replacement.Port);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsSameReferenceAs(newConnection);
    }

    [Test]
    [Arguments("expired")]
    [Arguments("cooldown")]
    [Arguments("retired")]
    [Arguments("unlinked")]
    public async Task IneligiblePreparedReplicaKeepsNormalAcquisition(string reason)
    {
        await using var primary = Server();
        await using var replica = Server();
        if (reason == "unlinked") replica.ReplyOverride = (_, command) => command == "ROLE"
            ? "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$10\r\nconnecting\r\n:0\r\n"u8.ToArray()
            : command.StartsWith("GET", StringComparison.Ordinal) ? "$5\r\nhello\r\n"u8.ToArray() : null;
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var router = client.Core.ReadRouter;
        var selected = await router.GetReplicaFromEndpointsAsync(Options(primary, replica).ReplicaEndpoints.ToArray(), default);
        if (reason == "expired") router.RoleRevalidationInterval = TimeSpan.Zero;
        if (reason == "cooldown") selected.Replica!.MarkFailed();
        if (reason == "retired") await selected.Connection.Multiplexer!.RetireAsync();
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.ReplicaPreferred, default)).IsNull();
        // Cooldown falls back to primary; expired or unlinked role state still follows the selector.
        if (reason != "retired")
            await Assert.That(await client.WithReadFrom(RespireReadFrom.ReplicaPreferred).GetStringAsync("fallback")).IsEqualTo("hello");
    }

    [Test]
    [Arguments(RespireReadFrom.Primary)]
    [Arguments(RespireReadFrom.PrimaryPreferred)]
    [Arguments(RespireReadFrom.Nearest)]
    [Arguments(RespireReadFrom.AzAffinity)]
    [Arguments(RespireReadFrom.AzAffinityReplicasAndPrimary)]
    public async Task PolicySelectorsAreNotBypassed(RespireReadFrom policy)
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with { ClientAvailabilityZone = "local" });
        await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(client.Core.ReadRouter.TryAcquireReadyConnection(policy, default)).IsNull();
        await Assert.That(await client.WithReadFrom(policy).GetStringAsync("policy")).IsEqualTo("hello");
        if (policy is RespireReadFrom.Primary or RespireReadFrom.PrimaryPreferred)
            await Assert.That(primary.ReceivedCommands).Contains("GET policy");
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task MultipleReplicaEndpointsKeepRoundRobinSelection(int connections)
    {
        await using var primary = Server();
        await using var first = Server();
        await using var second = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, first, second) with { Connections = connections });
        var router = client.Core.ReadRouter;
        await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await router.GetConnectionAsync(RespireReadFrom.Replica, default);
        await Assert.That(router.TryAcquireReadyConnection(RespireReadFrom.Replica, default)).IsNull();
        var ports = new int[8];
        for (var i = 0; i < ports.Length; i++) ports[i] = (await router.GetConnectionAsync(RespireReadFrom.Replica, default)).Port;
        await Assert.That(ports.Count(port => port == first.Port)).IsEqualTo(4);
        await Assert.That(ports.Count(port => port == second.Port)).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TypedAndStreamedReadsPreserveValuesAndTelemetry(bool listen)
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        var activities = new List<Activity>();
        using var listener = listen ? new ActivityListener
        {
            ShouldListenTo = source => source.Name == RespireTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => { lock (activities) activities.Add(activity); },
        } : null;
        if (listener is not null) ActivitySource.AddActivityListener(listener);
        await Assert.That(await view.GetStringAsync("string")).IsEqualTo("hello");
        await Assert.That(await view.GetBytesAsync("bytes")).IsEquivalentTo("hello"u8.ToArray());
        await Assert.That(await view.Strings.LengthAsync("length")).IsEqualTo(5L);
        await using var stream = await view.Strings.GetStreamAsync("stream");
        using var reader = new StreamReader(stream!);
        await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("hello");
        if (listen)
        {
            await Assert.That(activities.Count).IsEqualTo(4);
            await Assert.That(activities.All(activity => Equals(activity.GetTagItem("server.port"), replica.Port))).IsTrue();
        }
    }

    [Test]
    [Arguments("cancelled-before")]
    [Arguments("cancelled-after")]
    [Arguments("timeout")]
    [Arguments("disposed")]
    public async Task PreparedReadPreservesCancellationAndDisposal(string failure)
    {
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica) with
        {
            CommandTimeout = failure == "timeout" ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(10),
        });
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        replica.SuppressReply = command => command == "GET parked";
        using var caller = new CancellationTokenSource();
        if (failure == "cancelled-before") caller.Cancel();
        var pending = view.GetStringAsync("parked", caller.Token).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (failure != "cancelled-before")
            while (!replica.ReceivedCommands.Contains("GET parked")) await Task.Delay(1, deadline.Token);
        if (failure == "cancelled-after") caller.Cancel();
        if (failure == "disposed") await client.DisposeAsync();
        if (failure == "timeout")
            await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireTimeoutException>();
        else if (failure.StartsWith("cancelled", StringComparison.Ordinal))
        {
            await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
            await Assert.That(pending.IsCanceled).IsTrue();
        }
        else await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireConnectionException>();
        if (failure == "cancelled-before")
            await Assert.That(replica.ReceivedCommands.Contains("GET parked")).IsFalse();
        if (failure is "cancelled-after" or "timeout")
        {
            // The cancelled caller does not remove its FIFO response. Drain that late reply
            // before checking the next command on this prepared replica connection.
            await replica.SendRawAsync("$5\r\nstale\r\n"u8.ToArray());
            await Assert.That(await view.GetStringAsync("next", deadline.Token)).IsEqualTo("hello");
        }
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    public async Task PreparedDispatchAllocatesNoCallerState(string shape)
    {
        await Assert.That(RespireTelemetry.IsOperationEnabled("GET")).IsFalse();
        await using var primary = Server();
        await using var replica = Server();
        await using var client = await RespireClient.ConnectAsync(Options(primary, replica));
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        await view.GetStringAsync("warm");
        _ = view.Strings;
        var connection = await client.Core.ReadRouter.GetConnectionAsync(RespireReadFrom.Replica, default);
        replica.SuppressReply = command => command.StartsWith("GET", StringComparison.Ordinal)
            || command.StartsWith("STRLEN", StringComparison.Ordinal);
        var send = shape switch
        {
            "string" => () => MeasureReplies(replica, connection, () => view.GetStringAsync("parked"), false),
            "bytes" => () => MeasureReplies(replica, connection, () => view.GetBytesAsync("parked"), false),
            _ => (Func<long>)(() => MeasureReplies(replica, connection, () => view.Strings.LengthAsync("parked"), false)),
        };
        var control = shape switch
        {
            "string" => () => MeasureReplies(replica, connection, () => view.GetStringAsync("parked"), true),
            "bytes" => () => MeasureReplies(replica, connection, () => view.GetBytesAsync("parked"), true),
            _ => (Func<long>)(() => MeasureReplies(replica, connection, () => view.Strings.LengthAsync("parked"), true)),
        };
        // Warm generic dispatch and both completion ownership transitions. Call
        // counts alone can finish before background tiered compilation removes
        // the two default-interface command boxes. Warm for a fixed duration,
        // without using allocation results to decide when measurement starts.
        var warmStart = Stopwatch.GetTimestamp();
        var warmPasses = 0;
        do
        {
            _ = send();
            warmPasses++;
        }
        while (warmPasses < 8 || Stopwatch.GetElapsedTime(warmStart) < TimeSpan.FromSeconds(2));
        _ = control();
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (send(), control()));
        Console.WriteLine($"REPLICA_DISPATCH {shape}: {measured.Item1} B; allocation control {measured.Item2} B for 32 calls.");
        await Assert.That(measured.Item2 - measured.Item1).IsGreaterThanOrEqualTo(32L * 37);
#if !DEBUG
        await Assert.That(measured.Item1).IsEqualTo(0L);
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureReplies<T>(FakeRespServer server, RespireConnection connection, Func<ValueTask<T>> send, bool allocate)
    {
        long total = 0;
        for (var i = 0; i < 32; i++)
        {
            var commands = server.CommandsSeen;
            var dispatchBytes = MeasureDispatch(send, allocate, out var pending);
            total += dispatchBytes;
            if (!connection.InspectForTests().Inflight.TryPeek(out var source))
                throw new InvalidOperationException("The measured read did not publish a reply source.");
            if (!SpinWait.SpinUntil(() => server.CommandsSeen > commands, TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Replica did not receive the measured command.");
            var response = typeof(T) == typeof(long) ? ":5\r\n"u8.ToArray() : "$5\r\nhello\r\n"u8.ToArray();
            server.SendRawAsync(response).GetAwaiter().GetResult();
            // Complete both ownership transitions before the next measured rental.
            if (!SpinWait.SpinUntil(() => pending.IsCompleted && Volatile.Read(ref OwnerReferences(source!)) <= 1,
                TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Replica completion did not release receive ownership.");
            GC.KeepAlive(pending.GetAwaiter().GetResult());
        }
        return total;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDispatch<T>(Func<ValueTask<T>> send, bool allocate, out ValueTask<T> pending)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        pending = send();
        if (allocate) GC.KeepAlive(new byte[37]);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    internal static FakeRespServer Server()
        => new(32, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "ROLE" => ReplicaRole,
                _ when command.StartsWith("GET", StringComparison.Ordinal) => "$5\r\nhello\r\n"u8.ToArray(),
                _ when command.StartsWith("STRLEN", StringComparison.Ordinal) => ":5\r\n"u8.ToArray(),
                _ => null,
            },
        };

    internal static RespireOptions Options(FakeRespServer primary, params FakeRespServer[] replicas)
        => new()
        {
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = replicas.Select(server => new RespireEndpoint("127.0.0.1", server.Port)).ToArray(),
            Connections = 1, Protocol = RespProtocol.Resp2, ThreadPoolMonitoring = false,
            ReplicaRefreshInterval = TimeSpan.FromMinutes(10),
        };
}
