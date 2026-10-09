using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class ClusterCircuitDispatchTests
{
    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("integer")]
    [Arguments("converted")]
    [Arguments("raw")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    [Arguments("transaction")]
    [Arguments("pinned")]
    [Arguments("stream")]
    [Arguments("upload")]
    [Arguments("blocking")]
    public async Task OpenPrimaryRejectsEveryDispatchShapeWhileOtherPrimaryWorks(string shape)
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await Send(client, "string", "{open}warm");
        var router = client.Core.Cluster!;
        router.SetSlotOwner(ClusterHash.GetSlot("{healthy}key"), router.GetMultiplexer(Endpoint(target)));
        Open(client, source);
        var error = await Failure(() => Send(client, shape, "{open}rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        var rejection = (RespireCircuitOpenException)error;
        await Assert.That(rejection.Endpoint).IsEqualTo(Endpoint(source));
        await Assert.That(rejection.RetryAfter > TimeSpan.Zero).IsTrue();
        await Assert.That(source.ReceivedCommands.Any(command => command.Contains("rejected", StringComparison.Ordinal))).IsFalse();
        await Assert.That(source.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Send(client, shape, "{healthy}key");
        await Received(target, shape switch { "integer" => "STRLEN {healthy}key",
            "upload" => "SET {healthy}key value", "blocking" => "BLPOP {healthy}key 1", _ => "GET {healthy}key" });
        await Assert.That(Circuit(client, source).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(Circuit(client, target).Snapshot().FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrimaryAndReplicaAdmissionAreIndependent(bool openReplica)
    {
        await using var primary = Server();
        await using var replica = Server();
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(primary.Port, replica.Port) : Reply(command);
        await using var client = await RespireClient.ConnectAsync(Options(primary));
        var replicaClient = client.WithReadFrom(RespireReadFrom.Replica);
        await client.GetStringAsync("warm");
        await replicaClient.GetStringAsync("warm");
        Open(client, openReplica ? replica : primary);
        IRespireClient rejected = openReplica ? replicaClient : client;
        IRespireClient healthy = openReplica ? client : replicaClient;
        var error = await Failure(async () => await rejected.GetStringAsync("rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(openReplica ? replica : primary));
        await Assert.That(await healthy.GetStringAsync("healthy")).IsEqualTo("value");
        await Assert.That((openReplica ? replica : primary).ReceivedCommands.Contains("GET rejected")).IsFalse();
        await Assert.That((openReplica ? primary : replica).ReceivedCommands.Contains("GET healthy")).IsTrue();
    }

    [Test]
    [Arguments("MOVED", "string", false)]
    [Arguments("MOVED", "converted", true)]
    [Arguments("MOVED", "batch", true)]
    [Arguments("MOVED", "transaction", true)]
    [Arguments("ASK", "raw", false)]
    [Arguments("ASK", "bytes", true)]
    [Arguments("ASK", "batch", true)]
    [Arguments("MOVED", "upload", true)]
    [Arguments("ASK", "upload", true)]
    [Arguments("MOVED", "blocking", true)]
    [Arguments("ASK", "blocking", true)]
    [Arguments("ASK", "stream", true)]
    [Arguments("ASK", "upload", false)]
    [Arguments("ASK", "blocking", false)]
    public async Task RedirectAcquiresTargetAdmission(string code, string shape, bool targetOpen)
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await client.GetStringAsync("warm");
        if (targetOpen) Open(client, target);
        var key = "redirect";
        var redirect = Encoding.ASCII.GetBytes($"-{code} {ClusterHash.GetSlot(key)} 127.0.0.1:{target.Port}\r\n");
        var applicationCommand = shape switch { "upload" => "SET redirect value", "blocking" => "BLPOP redirect 1", _ => "GET redirect" };
        source.ReplyOverride = (_, command) => shape == "transaction" ? command switch
        {
            "MULTI" => FakeRespServer.OkReply,
            "GET redirect" => "+QUEUED\r\n"u8.ToArray(),
            "EXEC" => redirect,
            "CLUSTER SLOTS" => Slots(source.Port),
            _ => Reply(command),
        } : command == applicationCommand ? redirect
            : command == "CLUSTER SLOTS" ? Slots(source.Port) : Reply(command);
        if (targetOpen)
        {
            var error = await Failure(() => Send(client, shape, key));
            await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
            await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(target));
        }
        else await Send(client, shape, key);
        await Assert.That(target.ReceivedCommands.Contains(applicationCommand)).IsEqualTo(!targetOpen);
        await Assert.That(target.ReceivedCommands.Contains("ASKING")).IsEqualTo(code == "ASK" && !targetOpen);
        await Assert.That(target.ReceivedCommands.Contains("MULTI")).IsFalse();
        await Assert.That(Circuit(client, source).Snapshot().FailureCount).IsEqualTo(0);
        await Assert.That(source.ReceivedCommands.Count(command => command == applicationCommand)).IsEqualTo(1);
    }

    [Test]
    [Arguments("SCAN")]
    [Arguments("CLUSTERSCAN")]
    public async Task OpenEndpointRejectsPinnedScan(string operation)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.GetStringAsync("warm");
        var connection = await client.Core.Cluster!.GetConnectionAsync(null, default, null);
        Open(client, server);
        var error = await Failure(async () =>
        {
            using var reply = await client.SendOnPinnedConnectionAsync(operation, connection,
                new Cmd1(new Verb(operation), "0"), default);
        });
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(server.ReceivedCommands.Contains(operation + " 0")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OpenDisconnectedNodeRejectsWithoutRoleFallback(bool replicaRoute)
    {
        await using var primary = Server();
        await using var replica = Server();
        primary.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(primary.Port, replica.Port) : Reply(command);
        await using var client = await RespireClient.ConnectAsync(Options(primary));
        await client.GetStringAsync("warm");
        var endpoint = replicaRoute ? Endpoint(replica) : Endpoint(primary);
        var router = client.Core.Cluster!;
        var node = router.GetMultiplexer(endpoint);
        Open(client, replicaRoute ? replica : primary);
        // An uninitialized replica is already disconnected. Close the primary explicitly.
        if (!replicaRoute)
        {
            await primary.DisposeAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (node.IsConnected) await Task.Delay(1, timeout.Token);
        }
        var error = await Failure(async () => await router.GetReadConnectionAsync(ClusterHash.GetSlot("rejected"),
            replicaRoute ? RespireReadFrom.ReplicaPreferred : RespireReadFrom.Primary, default));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(endpoint);
        await Assert.That((replicaRoute ? replica : primary).ReceivedCommands.Any(command => command.Contains("rejected"))).IsFalse();
    }

    [Test]
    public async Task DisconnectedRouteRecordsFailureAndRejectsFurtherReconnect()
    {
        await using var server = Server();
        await using var unavailable = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.GetStringAsync("warm");
        var router = client.Core.Cluster!;
        var slot = ClusterHash.GetSlot("disconnected");
        var node = router.GetMultiplexer(Endpoint(unavailable));
        router.SetSlotOwner(slot, node);
        await unavailable.DisposeAsync();
        // Discovery may recover through the healthy seed after this candidate fails.
        try { await client.GetStringAsync("disconnected"); }
        catch (RespireConnectionException) { }
        await Assert.That(Circuit(client, unavailable).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        node = router.GetMultiplexer(Endpoint(unavailable));
        router.SetSlotOwner(slot, node);
        var commands = server.CommandsSeen;
        var error = await Failure(async () => await client.GetStringAsync("disconnected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(unavailable));
        await Assert.That(server.CommandsSeen).IsEqualTo(commands);
    }

    [Test]
    [Arguments("pinned")]
    [Arguments("upload")]
    [Arguments("blocking")]
    public async Task GuardedDataFailureOpensEndpoint(string shape)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.GetStringAsync("warm");
        await Send(client, shape, "warm-failure");
        server.CloseConnectionAfterCommand = server.CommandsSeen + 1;
        await Assert.That(await Failure(() => Send(client, shape, "failed"))).IsTypeOf<RespireConnectionException>();
        await Assert.That(Circuit(client, server).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task AskingCapacityWaitReacquiresMaintenanceDestination(bool replacementOpen, bool tracked)
    {
        await using var source = Server();
        await using var target = Server();
        await using var replacement = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source) with
        {
            Protocol = RespProtocol.Resp3, MaintenanceNotifications = RespireMaintenanceNotificationMode.Enabled,
            MaxInflightCommands = tracked ? 4 : 2, CommandTimeout = TimeSpan.FromSeconds(10),
            ClientSideCache = tracked ? new() : null,
        });
        await client.GetStringAsync("warm");
        var router = client.Core.Cluster!;
        var targetNode = router.GetMultiplexer(Endpoint(target));
        router.SetSlotOwner(ClusterHash.GetSlot("hold"), targetNode);
        if (tracked) router.SetSlotOwner(ClusterHash.GetSlot("hold2"), targetNode);
        await client.GetStringAsync("hold");
        var original = targetNode.GetConnection();
        target.SuppressReply = command => command.StartsWith("GET hold", StringComparison.Ordinal);
        var held = client.WithoutClientCache().GetStringAsync("hold").AsTask();
        var heldSecond = tracked ? client.WithoutClientCache().GetStringAsync("hold2").AsTask() : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (target.ReceivedCommands.Count(command => command == "GET hold") < 2)
            await Task.Delay(1, timeout.Token);
        if (tracked) await Received(target, "GET hold2");
        var circuit = Circuit(client, target);
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        Open(client, target);
        clock.Advance();
        if (replacementOpen) Open(client, replacement);
        source.ReplyOverride = (_, command) => command == "GET streamed"
            ? Encoding.ASCII.GetBytes($"-ASK {ClusterHash.GetSlot("streamed")} 127.0.0.1:{target.Port}\r\n")
            : command == "CLUSTER SLOTS" ? Slots(source.Port) : Reply(command);
        var pendingStream = tracked ? null : client.Strings.GetStreamAsync("streamed").AsTask();
        var pendingRead = tracked ? client.GetStringAsync("streamed").AsTask() : null;
        var pending = (Task?)pendingRead ?? pendingStream!;
        while (circuit.Snapshot().ActiveProbes != 1) await Task.Delay(1, timeout.Token);
        var announcement = targetNode.CaptureMovingAnnouncement(0, original,
            new MaintenanceNotification("MOVING", 1, 10, Endpoint(replacement)));
        typeof(RespireConnectionMultiplexer).GetMethod("QueueMovingHandoff",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(targetNode, [0, original, announcement]);
        try
        {
            if (replacementOpen)
            {
                var error = await Failure(async () => await pending);
                await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
                await Assert.That(((RespireCircuitOpenException)error).Endpoint).IsEqualTo(Endpoint(replacement));
            }
            else
            {
                if (tracked) await Assert.That(await pendingRead!).IsEqualTo("value");
                else
                {
                    await using var stream = await pendingStream!;
                    using var reader = new StreamReader(stream!);
                    await Assert.That(await reader.ReadToEndAsync()).IsEqualTo("value");
                }
            }
            await Assert.That(target.ReceivedCommands.Contains("GET streamed")).IsFalse();
            await Assert.That(replacement.ReceivedCommands.Contains("GET streamed")).IsEqualTo(!replacementOpen);
            await Assert.That(replacement.ReceivedCommands.Contains("ASKING")).IsEqualTo(!replacementOpen);
            await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
            await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(0);
        }
        finally
        {
            await target.SendRawAsync(Bulk, target.ReceivedConnectionIds[^1]);
            if (tracked) await target.SendRawAsync(Bulk, target.ReceivedConnectionIds[^1]);
            await held;
            if (tracked) await heldSecond!;
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetiredGenerationRequiresReplacementAdmission(bool targetOpen)
    {
        await using var source = Server();
        await using var target = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await client.GetStringAsync("warm");
        if (targetOpen) Open(client, target);
        var router = client.Core.Cluster!;
        var original = await router.GetConnectionAsync(ClusterHash.GetSlot("retired"), default, null);
        router.SetSlotOwner(ClusterHash.GetSlot("retired"), router.GetMultiplexer(Endpoint(target)));
        await original.Multiplexer!.RetireAsync();
        var command = new Cmd1(Verbs.Get, "retired");
        var send = client.ResumeRetiredClusterSendAsync("GET", command, original,
            new RespireConnectionRetiredException("127.0.0.1", source.Port), RespireReadFrom.Primary, default);
        if (targetOpen)
        {
            await Assert.That(await Failure(async () => { using var response = await send; })).IsTypeOf<RespireCircuitOpenException>();
        }
        else
        {
            using var response = await send;
            await Assert.That(ResponseReader.StringOrNull(in response)).IsEqualTo("value");
        }
        await Assert.That(source.ReceivedCommands.Contains("GET retired")).IsFalse();
        await Assert.That(target.ReceivedCommands.Contains("GET retired")).IsEqualTo(!targetOpen);
        await Assert.That(Circuit(client, source).Snapshot().FailureCount).IsEqualTo(0);
    }

    [Test]
    [Arguments("string", false)]
    [Arguments("batch", false)]
    [Arguments("transaction", false)]
    [Arguments("string", true)]
    [Arguments("batch", true)]
    [Arguments("transaction", true)]
    public async Task CancelledOrFailedRecoveryReleasesCapacityForFullBatch(string shape, bool timeout)
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            CommandTimeout = timeout ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(10),
        });
        await client.GetStringAsync("warm");
        var circuit = Circuit(client, server);
        var clock = new Clock();
        CircuitClock(circuit) = clock;
        Open(client, server);
        clock.Advance();
        server.SuppressReply = command => command == (shape == "transaction" ? "EXEC" : "GET probe");
        using var cancellation = new CancellationTokenSource();
        var pending = Send(client, shape, "probe", cancellation.Token);
        await Received(server, shape == "transaction" ? "EXEC" : "GET probe");
        if (!timeout) cancellation.Cancel();
        var error = await Failure(async () => await pending);
        await Assert.That(error.GetType()).IsEqualTo(timeout ? typeof(RespireTimeoutException) : typeof(OperationCanceledException));
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        // Keep the accepted response placeholder, then drain it before dispatching the next batch.
        await server.SendRawAsync(shape == "transaction" ? "*1\r\n$5\r\nvalue\r\n"u8.ToArray() : Bulk,
            server.ReceivedConnectionIds[^1]);
        // Suppressed EXEC did not invoke the fake server's transaction state transition.
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS" ? Slots(server.Port) : Reply(command);
        server.SuppressReply = null;
        if (timeout) clock.Advance();
        using var batch = client.CreateBatch();
        var first = batch.GetString("{full}first");
        var second = batch.GetString("{full}second");
        await batch.ExecuteAsync();
        await Assert.That(await first).IsEqualTo("value");
        await Assert.That(await second).IsEqualTo("value");
        await Assert.That(circuit.Snapshot().State).IsEqualTo(EndpointCircuitState.Closed);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET probe")).IsEqualTo(1);
    }

    [Test]
    public async Task TrackedAskingCapacityWaitUsesOriginalDeadline()
    {
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            MaxInflightCommands = 4, CommandTimeout = TimeSpan.FromSeconds(10),
        });
        await client.GetStringAsync("warm");
        var connection = await client.Core.Cluster!.GetConnectionAsync(null, default, null);
        server.SuppressReply = command => command.StartsWith("GET hold", StringComparison.Ordinal);
        var held = connection.SendAsync(new Cmd1(Verbs.Get, "hold")).AsTask();
        var heldSecond = connection.SendAsync(new Cmd1(Verbs.Get, "hold2")).AsTask();
        await Received(server, "GET hold");
        await Received(server, "GET hold2");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            var command = new Cmd1(Verbs.Get, "deadline");
            var error = await Failure(async () =>
            {
                using var response = await ClusterRouter.SendTrackedAskingAsync(connection, in command,
                    deadline.Token, "GET", pinToConnection: true, commandDeadline: CommandDeadline.After(50));
            });
            await Assert.That(error).IsTypeOf<RespireTimeoutException>();
            await Assert.That(server.ReceivedCommands.Contains("ASKING")).IsFalse();
            await Assert.That(server.ReceivedCommands.Contains("GET deadline")).IsFalse();
        }
        finally
        {
            await server.SendRawAsync(Bulk, server.ReceivedConnectionIds[^1]);
            await server.SendRawAsync(Bulk, server.ReceivedConnectionIds[^1]);
            using var response = await held;
            using var secondResponse = await heldSecond;
        }
    }

    [Test]
    [Arguments("blocking")]
    [Arguments("upload")]
    public async Task DedicatedConnectFailureOpensHealthyMultiplexedEndpoint(string shape)
    {
        await using var server = Server();
        var failConnect = false;
        var failedAttempts = 0;
        await using var client = await RespireClient.ConnectAsync(Options(server) with
        {
            ReconnectPolicy = new() { MaxAttempts = 1, InitialDelay = TimeSpan.Zero },
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                if (failConnect)
                {
                    Interlocked.Increment(ref failedAttempts);
                    throw new IOException("Injected dedicated connect failure.");
                }
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream,
                    System.Net.Sockets.ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, cancellationToken);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        await client.GetStringAsync("warm");
        failConnect = true;
        await Failure(() => Send(client, shape, "failed"));
        await Assert.That(Circuit(client, server).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
        await Assert.That(client.Core.Cluster!.GetMultiplexer(Endpoint(server)).IsConnected).IsTrue();
        var attempts = failedAttempts;
        var error = await Failure(() => Send(client, shape, "rejected"));
        await Assert.That(error).IsTypeOf<RespireCircuitOpenException>();
        await Assert.That(failedAttempts).IsEqualTo(attempts);
        await Assert.That(server.ReceivedCommands.Any(command => command.Contains("failed") || command.Contains("rejected"))).IsFalse();
    }

    [Test]
    public async Task SlotDiscoverySkipsOpenUnrelatedMasterAndUsesHealthyMaster()
    {
        await using var source = Server();
        await using var unrelated = Server();
        await using var healthy = Server();
        await using var client = await RespireClient.ConnectAsync(Options(source));
        await client.GetStringAsync("warm");
        var router = client.Core.Cluster!;
        var failedOwner = router.GetMultiplexer(Endpoint(source));
        var openNode = router.GetMultiplexer(Endpoint(unrelated));
        var healthyNode = router.GetMultiplexer(Endpoint(healthy));
        router.SetSlotOwner(1, openNode);
        router.SetSlotOwner(2, healthyNode);
        Open(client, unrelated);
        var method = typeof(ClusterRouter).GetMethod("TryRefreshSlotThroughKnownMastersAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var result = (ValueTask<RespireConnectionMultiplexer?>)method.Invoke(router,
            [0, failedOwner, CancellationToken.None, null, false])!;
        var owner = await result;
        await Assert.That(owner).IsSameReferenceAs(healthyNode);
        await Assert.That(healthy.ReceivedCommands.Contains("CLUSTER SLOTS")).IsTrue();
        await Assert.That(unrelated.ReceivedCommands.Count).IsEqualTo(0);
        await Assert.That(Circuit(client, unrelated).Snapshot().State).IsEqualTo(EndpointCircuitState.Open);
    }

    [Test]
    public async Task ConverterFailureKeepsHealthyOutcomeAndNeverReplays()
    {
        using var metrics = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.None });
        await using var server = Server();
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await client.GetStringAsync("warm");
        var circuit = Circuit(client, server);
        CircuitClock(circuit) = new Clock();
        Open(client, server);
        ((Clock)CircuitClock(circuit)).Advance();
        var expected = new RespireServerException($"MOVED {ClusterHash.GetSlot("convert")} 127.0.0.1:{server.Port}");
        var error = await Failure(async () => await client.ConvertResponseAsync<Cmd1, RespireServerException, int>(
            "GET", new Cmd1(Verbs.Get, "convert"), default,
            expected, static (RespireServerException error, in RespValue _) => throw error));
        await Assert.That(error).IsSameReferenceAs(expected);
        await Assert.That(circuit.Snapshot().SuccessfulProbes).IsEqualTo(1);
        await Assert.That(circuit.Snapshot().ActiveProbes).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET convert")).IsEqualTo(1);
    }

    [Test]
    public async Task LiveClusterNodesKeepOpenHistoryBeyondStandaloneIdleLimit()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            UseCluster = true, Endpoints = [new("127.0.0.1", 9000)], CircuitBreaker = new() { MinimumFailureCount = 1 },
            ThreadPoolMonitoring = false,
        });
        var circuits = client.Core.Circuits!;
        var endpoints = Enumerable.Range(0, 32).Select(i => new RespireEndpoint("127.0.0.1", 9000 + i)).ToArray();
        foreach (var endpoint in endpoints)
        {
            client.Core.Cluster!.GetMultiplexer(endpoint);
            var admission = circuits.Acquire(endpoint, default);
            try { admission.Failed(new RespireConnectionException("Injected failure."), default); }
            finally { admission.Dispose(); }
        }
        await Assert.That(circuits.CountForTests).IsEqualTo(32);
        foreach (var endpoint in endpoints)
            await Assert.That(() => circuits.Acquire(endpoint, default)).Throws<RespireCircuitOpenException>();
    }

    private static readonly byte[] Bulk = "$5\r\nvalue\r\n"u8.ToArray();
    private static byte[]? Reply(string command) => command.StartsWith("HELLO 3", StringComparison.Ordinal)
        ? "%1\r\n+proto\r\n:3\r\n"u8.ToArray()
        : command.StartsWith("GET ", StringComparison.Ordinal) ? Bulk
        : command.StartsWith("STRLEN ", StringComparison.Ordinal) ? ":5\r\n"u8.ToArray() : null;

    private static FakeRespServer Server()
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        var inTransaction = false;
        server.ReplyOverride = (_, command) =>
        {
            if (command == "CLUSTER SLOTS") return Slots(server.Port);
            if (command == "MULTI") { inTransaction = true; return FakeRespServer.OkReply; }
            if (command == "EXEC") { inTransaction = false; return "*1\r\n$5\r\nvalue\r\n"u8.ToArray(); }
            if (inTransaction) return "+QUEUED\r\n"u8.ToArray();
            return Reply(command);
        };
        return server;
    }

    private static byte[] Slots(int primary, int? replica = null) => Encoding.ASCII.GetBytes(
        $"*1\r\n*{(replica is null ? 3 : 4)}\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{primary}\r\n"
        + (replica is { } port ? $"*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n" : ""));

    private static RespireOptions Options(FakeRespServer server) => new()
    {
        Endpoints = [Endpoint(server)], UseCluster = true, Connections = 1, Protocol = RespProtocol.Resp2,
        ThreadPoolMonitoring = false, ClusterTopologyRefreshInterval = null, CommandTimeout = TimeSpan.FromSeconds(5),
        CircuitBreaker = new() { MinimumFailureCount = 1, FailureRateThreshold = 0.01,
            HalfOpenProbeCount = 2, OpenDuration = TimeSpan.FromMinutes(1) },
    };

    private static RespireEndpoint Endpoint(FakeRespServer server) => new("127.0.0.1", server.Port);
    private static EndpointCircuitBreaker Circuit(RespireClient client, FakeRespServer server)
        => client.Core.Circuits!.GetForTests(Endpoint(server));
    private static void Open(RespireClient client, FakeRespServer server)
    {
        var admission = client.Core.Circuits!.Acquire(Endpoint(server), default);
        try { admission.Failed(new RespireConnectionException("Injected node failure."), default); }
        finally { admission.Dispose(); }
    }

    private static async Task Send(RespireClient client, string shape, string key, CancellationToken cancellationToken = default)
    {
        switch (shape)
        {
            case "string": await client.GetStringAsync(key, cancellationToken); break;
            case "bytes": await client.GetBytesAsync(key, cancellationToken); break;
            case "integer": await client.Strings.LengthAsync(key, cancellationToken); break;
            case "pinned":
                var connection = await client.Core.Cluster!.GetConnectionAsync(ClusterHash.GetSlot(key), cancellationToken, null);
                using (var response = await client.SendOnPinnedConnectionAsync("GET", connection, new Cmd1(Verbs.Get, key), cancellationToken)) { }
                break;
            case "stream":
                await using (var stream = await client.Strings.GetStreamAsync(key, cancellationToken))
                    if (stream is not null) await stream.CopyToAsync(Stream.Null, cancellationToken);
                break;
            case "upload":
                using (var payload = new MemoryStream("value"u8.ToArray()))
                    await client.Strings.SetAsync(key, payload, 5, cancellationToken: cancellationToken);
                break;
            case "blocking": using (var response = await client.ExecuteAsync("BLPOP", [key, "1"], cancellationToken: cancellationToken)) { } break;
            case "converted": await client.ConvertResponseAsync("GET", new Cmd1(Verbs.Get, key), cancellationToken,
                0, static (int _, in RespValue response) => ResponseReader.StringOrNull(in response)); break;
            case "raw": using (var response = await client.ExecuteAsync("GET", [key], cancellationToken: cancellationToken)) { } break;
            case "fire-and-forget": await client.ExecuteFireAndForgetAsync("GET", [key], cancellationToken: cancellationToken); break;
            case "batch":
                using (var batch = client.CreateBatch())
                {
                    var pending = batch.GetString(key);
                    await batch.ExecuteAsync(cancellationToken);
                    await Assert.That(await pending).IsEqualTo("value");
                }
                break;
            case "transaction":
                await using (var transaction = client.CreateTransaction())
                {
                    var pending = transaction.GetString(key);
                    await transaction.CommitAsync(cancellationToken);
                    await Assert.That(await pending).IsEqualTo("value");
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    private static async Task<Exception> Failure(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error) { return error; }
        throw new InvalidOperationException("Expected dispatch failure.");
    }

    private static async Task Received(FakeRespServer server, string command)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!server.ReceivedCommands.Contains(command)) await Task.Delay(1, timeout.Token);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_clock")]
    private static extern ref TimeProvider CircuitClock(EndpointCircuitBreaker circuit);
    private sealed class Clock : TimeProvider
    {
        private long _timestamp = TimeProvider.System.GetTimestamp();
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        internal void Advance() => Interlocked.Add(ref _timestamp, TimestampFrequency * 60);
    }
}
