using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public partial class ErrorMetricsTests
{
    [Test]
    public async Task CircuitRejectionCarriesTheRejectedEndpointBeforeTransportDispatch()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer();
        var endpoint = new RespireEndpoint("127.0.0.1", server.Port);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [endpoint],
            CircuitBreaker = new() { MinimumFailureCount = 1, OpenDuration = TimeSpan.FromMinutes(1) },
        });
        var admission = client.Core.Circuits!.Acquire(endpoint, default);
        try { admission.Failed(new RespireConnectionException("Injected endpoint failure."), default); }
        finally { admission.Dispose(); }
        using var capture = new Capture(serverPort: server.Port);
        await Assert.That(async () => await client.Strings.GetStringAsync("key")).Throws<RespireCircuitOpenException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo(endpoint.Host);
        await Assert.That(item.Tags["server.port"]).IsEqualTo(endpoint.Port);
        await Assert.That(server.ReceivedCommands.Contains("GET key")).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CircuitRejectionOverridesAnEarlierSelectedEndpoint(bool observed)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        // Nearest can retain the primary's endpoint and then pick a replica whose circuit is open.
        var primary = new RespireEndpoint("primary.example", 6380);
        var rejected = new RespireCircuitOpenException(new RespireEndpoint("replica.example", 6381), retryAfter: null);
        if (observed)
        {
            using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            observation.UseEndpoint(primary);
            observation.Final(rejected);
        }
        else RespireTelemetry.RecordError(rejected, internallyHandled: false, endpoint: primary);
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo("replica.example");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(6381);
    }

    [Test]
    public async Task ClusterWideFireAndForgetNamesTheRejectingMaster()
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.Command });
        await using var first = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        var refusedPort = 0;
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                if (port == Volatile.Read(ref refusedPort)) throw new SocketException((int)SocketError.ConnectionRefused);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(host, port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        });
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null, observation: default);
        // The first target rejects during its send (retired, replacement refused); the later target
        // accepts and is the last send to touch the caller's observation, so it must not own the error.
        var rejecting = connections[0];
        var healthy = rejecting.Port == first.Port ? second : first;
        Task? retirement = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.GetTagItem("db.operation.name")?.ToString() != "SCRIPT"
                    || (int)activity.GetTagItem("server.port")! != rejecting.Port || retirement is not null) return;
                Volatile.Write(ref refusedPort, rejecting.Port);
                retirement = rejecting.RetireAsync();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new Capture();
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("SCRIPT", ["FLUSH"])).Throws<Exception>();
        if (retirement is not null) await retirement;
        await Assert.That(healthy.ReceivedCommands).Contains("SCRIPT FLUSH");
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.port"]).IsEqualTo(rejecting.Port);
    }
    [Test]
    public async Task ClusterWideFireAndForgetCancellationNamesNoOtherMaster()
    {
        using var configuration = new MetricConfigurationScope(new()
            { Groups = RespireMetricGroups.Resiliency | RespireMetricGroups.Command });
        await using var first = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var cancellation = new CancellationTokenSource();
        // Discovery visits every master; cancellation then wins before the first target dispatches.
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Respire",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity =>
            {
                if (activity.GetTagItem("db.operation.name")?.ToString() == "SCRIPT") cancellation.Cancel();
            },
        };
        ActivitySource.AddActivityListener(listener);
        using var capture = new Capture();
        await Assert.That(async () => await client.ExecuteFireAndForgetAsync("SCRIPT", ["FLUSH"], cancellation.Token))
            .Throws<OperationCanceledException>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    [Arguments("transaction")]
    [Arguments("durability")]
    public async Task QueuedCommandsShareTheAttemptedEndpoint(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", port)], ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        using var capture = new Capture();
        if (route == "transaction")
        {
            await using var transaction = client.CreateTransaction();
            var first = transaction.Set("first", "1");
            var second = transaction.Set("second", "2");
            await Assert.That(async () => await transaction.CommitAsync()).Throws<Exception>();
            await Assert.That(async () => await first).Throws<Exception>();
            await Assert.That(async () => await second).Throws<Exception>();
        }
        else
        {
            using var batch = client.CreateBatch();
            var first = batch.Set("first", "1");
            var second = batch.Set("second", "2");
            await Assert.That(async () => await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.FromSeconds(1)))
                .Throws<Exception>();
            await Assert.That(async () => await first).Throws<Exception>();
            await Assert.That(async () => await second).Throws<Exception>();
        }
        var finals = capture.Items.Where(item => !(bool)item.Tags["redis.client.errors.internal"]!).ToArray();
        await Assert.That(finals.Length).IsGreaterThanOrEqualTo(2);
        foreach (var item in finals) await Assert.That(item.Tags["server.port"]).IsEqualTo(port);
    }

    [Test]
    [Arguments("send")]
    [Arguments("pinned")]
    public async Task TransportCancellationClearsARetainedEndpoint(string route)
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        // Routing selected another node earlier; cancellation then won before dispatch.
        observation.UseEndpoint(new("routed.example", 6390));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var command = new Commands.RawCommand(FakeRespServer.PingFrame);
        await Assert.That(async () =>
        {
            if (route == "send") using (await connection.SendAsync(command, cancelled.Token, observation: observation)) { }
            else _ = await connection.EnqueuePinnedAsync(command, cancelled.Token, "PING", observation);
        }).Throws<Exception>();
        await Assert.That(observation.Endpoint).IsNull();
    }

    [Test]
    public async Task CapacityWaitCancellationNamesNoNode()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer(FakeRespServer.OkReply)
        {
            SuppressReply = command => command.StartsWith("GET held", StringComparison.Ordinal),
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        // The unanswered GET fills the only in-flight slot, so the next command waits for capacity.
        var held = client.Strings.GetStringAsync("held");
        using var capture = new Capture();
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.That(async () => await client.Strings.GetStringAsync("queued", cancelled.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(held.IsCompleted).IsFalse();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["redis.client.errors.category"]).IsEqualTo("cancelled");
        await Assert.That(item.Tags.ContainsKey("server.address")).IsFalse();
        await Assert.That(item.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    public async Task TransactionCapacityPreflightNamesNoNode()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var connection = client.Core.Multiplexer.GetConnection();
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        // Routing or pool acquisition can already have named a node, such as an ASK target.
        observation.UseEndpoint(new("retained.example", 1));
        // More queued commands than the connection can hold fail locally before any frame is sent.
        await Assert.That(async () => await connection.SendTransactionAsync(ReadOnlyMemory<byte>.Empty,
            int.MaxValue - 1, observation: observation)).Throws<Exception>();
        await Assert.That(observation.Endpoint).IsNull();
    }

    [Test]
    [Arguments("prefixed")]
    [Arguments("validated")]
    [Arguments("validated-blocking")]
    [Arguments("doubly-validated")]
    public async Task PrefixedCapacityPreflightNamesNoNode(string path)
    {
        await using var server = new FakeRespServer();
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, MaxInflightCommands = 1,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var connection = client.Core.Multiplexer.GetConnection();
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        // Routing or pool acquisition can already have named a node, such as an ASK target.
        observation.UseEndpoint(new("retained.example", 1));
        var command = new Cmd(Verbs.RandomKey);
        // A one-slot connection cannot hold the prelude and command, so nothing is sent.
        await Assert.That(async () => _ = path switch
        {
            "prefixed" => await connection.SendPrefixedAsync(command, command, throwOnError: true, observation: observation),
            "validated" => await connection.SendValidatedPrefixedAsync(command, command, observation: observation),
            "validated-blocking" => await connection.SendValidatedPrefixedWithoutResponseTimeoutAsync(command, command,
                CancellationToken.None, observation: observation),
            _ => await connection.SendValidatedPrefixedAsync(command, command, command, observation: observation),
        }).Throws<InvalidOperationException>();
        await Assert.That(observation.Endpoint).IsNull();
    }

    [Test]
    public async Task CancelledReadyRequestNamesNoNode()
    {
        using var caller = RespireTelemetry.ErrorObservation.Rent(force: true);
        using var cancelled = new CancellationTokenSource();
        caller.UseEndpoint(new("selected.example", 6380));
        // Cancellation observed before dispatch clears any routing selection.
        cancelled.Cancel();
        caller.UseEndpoint(new("primary.example", 6381), cancelled.Token);
        await Assert.That(caller.Endpoint).IsNull();
        caller.UseEndpoint(new("primary.example", 6381), CancellationToken.None);
        await Assert.That(caller.Endpoint).IsEqualTo(new RespireEndpoint("primary.example", 6381));
    }

    [Test]
    public async Task HedgeLegWithoutEndpointClearsTheCallersSelection()
    {
        using var caller = RespireTelemetry.ErrorObservation.Rent(force: true);
        using var leg = RespireTelemetry.ErrorObservation.Rent(force: true);
        // Routing selected a server, but the winning leg was cancelled before it dispatched.
        caller.UseEndpoint(new("selected.example", 6380));
        caller.CopyCompletedAttempts(0, leg);
        await Assert.That(caller.Endpoint).IsNull();
    }

    [Test]
    public async Task CancelledColdRequestNamesNoNode()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var capture = new Capture();
        await Assert.That(async () => await client.Strings.GetStringAsync("key", cancelled.Token))
            .Throws<OperationCanceledException>();
        // A request cancelled before connecting never contacted Redis.
        foreach (var item in capture.Items)
        {
            await Assert.That(item.Tags.ContainsKey("server.address")).IsFalse();
            await Assert.That(item.Tags.ContainsKey("server.port")).IsFalse();
        }
    }

    [Test]
    public async Task FanOutJoinNamesTheLegWhoseExceptionPropagates()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var caller = RespireTelemetry.ErrorObservation.Rent(force: true);
        var legs = RespireTelemetry.FanOutLegObservation.Create(2, caller);
        var first = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The first leg's transport rerouted from its original node to a replacement.
        legs[0].Observation.UseEndpoint(new("original.example", 6380));
        legs[0].Observation.UseEndpoint(new("replacement.example", 6381));
        legs[1].Observation.UseEndpoint(new("second.example", 6382));
        var join = RespireTelemetry.FanOutLegObservation.JoinAsync([first.Task, second.Task], legs, caller);
        // The later leg faults first; whichever exception propagates must name its own leg.
        var secondFailure = new IOException("second");
        var firstFailure = new IOException("first");
        second.SetException(secondFailure);
        first.SetException(firstFailure);
        var propagated = await Assert.That(async () => await join).Throws<IOException>();
        var expected = ReferenceEquals(propagated, firstFailure)
            ? new RespireEndpoint("replacement.example", 6381) : new RespireEndpoint("second.example", 6382);
        await Assert.That(caller.Endpoint).IsEqualTo(expected);
        // Handled errors inside a leg name that leg, not the caller's retained endpoint.
        using var capture = new Capture();
        legs[1].Observation.Handled(new RespireServerException("MOVED 1 other.example:1"));
        await Assert.That(capture.Items.Single().Tags["server.port"]).IsEqualTo(6382);
    }

    [Test]
    public async Task SentinelPrimaryConnectFailureNamesTheResolvedPrimary()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // Sentinel resolves a primary that refuses connections.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var sentinel = new FakeRespServer(16)
        {
            ReplyOverride = (_, command) => command.StartsWith("SENTINEL GET-MASTER-ADDR-BY-NAME ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes($"*2\r\n$9\r\n127.0.0.1\r\n${port.ToString().Length}\r\n{port}\r\n")
                : "*0\r\n"u8.ToArray(),
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ThreadPoolMonitoring = false,
            Endpoints = [new("127.0.0.1", sentinel.Port)], SentinelPrimaryName = "primary",
            ConnectTimeout = TimeSpan.FromSeconds(2),
        });
        using var capture = new Capture();
        await Assert.That(async () => await client.Strings.GetStringAsync("key")).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(final.Tags["server.port"]).IsEqualTo(port);
    }

    [Test]
    [Arguments("function")]
    [Arguments("script")]
    public async Task ParallelFanOutFinalErrorNamesTheFailingLeg(string route)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(16, FakeRespServer.OkReply);
        await using var second = new FakeRespServer(16, FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var connections = await client.Core.Cluster!.GetMasterConnectionsAsync(default, discovery: null, observation: default);
        // Task.WhenAll surfaces the first leg's failure. The healthy leg answers later, so it
        // is the last to touch the shared observation and must not own the final error.
        var failingPort = connections[0].Port;
        var failing = failingPort == first.Port ? first : second;
        var healthy = ReferenceEquals(failing, first) ? second : first;
        var operation = route == "function" ? "FUNCTION FLUSH" : "SCRIPT FLUSH";
        failing.ReplyOverride = (_, command) => command.StartsWith(operation, StringComparison.Ordinal)
            ? "-NOPERM failing leg\r\n"u8.ToArray() : null;
        healthy.DelayCommand(operation, 300);
        using var capture = new Capture();
        await Assert.That(async () =>
        {
            if (route == "function") await client.Functions.FlushAsync();
            else await client.Scripts.FlushAsync();
        }).Throws<RespireServerException>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.port"]).IsEqualTo(failingPort);
    }

    [Test]
    public async Task FanOutValidationFailureNamesNoNode()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        await using var first = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SCRIPT EXISTS", StringComparison.Ordinal)
                ? "*1\r\n:1\r\n"u8.ToArray() : null,
        };
        await using var second = new FakeRespServer(16, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("SCRIPT EXISTS", StringComparison.Ordinal)
                ? "*2\r\n:1\r\n:1\r\n"u8.ToArray() : null,
        };
        var topology = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:8191\r\n*2\r\n$9\r\n127.0.0.1\r\n:{first.Port}\r\n" +
            $"*3\r\n:8192\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{second.Port}\r\n");
        await using var seed = new FakeRespServer(16, topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, UseCluster = true,
            ClusterTopologyRefreshInterval = null, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        using var capture = new Capture();
        // Both legs succeed; only the cross-primary comparison fails, so no single node owns it.
        await Assert.That(async () => await client.Scripts.ExistsAsync("0123456789abcdef0123456789abcdef01234567"))
            .Throws<RespireProtocolException>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags.ContainsKey("server.address")).IsFalse();
        await Assert.That(final.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    public async Task EmptyWatchedCommitFailureNamesTheWatchConnection()
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // WATCH and MULTI/EXEC run on a second, dedicated connection.
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("EXEC", StringComparison.Ordinal)
                ? "-ERR transactions disabled\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        await using var transaction = await client.CreateTransactionAsync(["balance"]);
        using var capture = new Capture();
        // A read-only retry callback still validates WATCH with an empty MULTI/EXEC. No command is
        // queued, so the final publication has no pending owner to name the node.
        await Assert.That(async () => await transaction.CommitWithWatchValidationAsync(CancellationToken.None))
            .Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(final.Tags["server.port"]).IsEqualTo(server.Port);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ColdClusterSeedFailureNamesTheSeed(bool clusterWide)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // Reserve then release a port so the seed refuses connections.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, UseCluster = true, Connections = 1, ThreadPoolMonitoring = false,
            ClusterTopologyRefreshInterval = null, ConnectTimeout = TimeSpan.FromSeconds(2),
            Endpoints = [new("127.0.0.1", port)],
        });
        using var capture = new Capture();
        await Assert.That(async () =>
        {
            // SCRIPT FLUSH discovers every master before it sends anything.
            if (clusterWide) using (await client.ExecuteAsync("SCRIPT", ["FLUSH"])) { }
            else await client.Strings.GetStringAsync("key");
        }).Throws<Exception>();
        var final = capture.Items.Single(item => !(bool)item.Tags["redis.client.errors.internal"]!);
        await Assert.That(final.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(final.Tags["server.port"]).IsEqualTo(port);
    }

    [Test]
    [Arguments(false, "-ERR replication unavailable\r\n")]
    [Arguments(true, "-ERR replication unavailable\r\n")]
    [Arguments(true, "*1\r\n:1\r\n")]
    public async Task DurabilityAcknowledgementFailureNamesTheDedicatedConnection(bool aof, string reply)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        // WAIT runs on a second, dedicated connection.
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith(aof ? "WAITAOF " : "WAIT ", StringComparison.Ordinal)
                ? Encoding.ASCII.GetBytes(reply) : null,
        };
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var batch = client.CreateBatch();
        var queued = batch.Set("key", "value");
        using var capture = new Capture(serverPort: server.Port);
        // Every queued command succeeds, so only the final publication reports the acknowledgement failure.
        await Assert.That(async () =>
        {
            if (aof) await batch.ExecuteAndWaitForAofAsync(true, 1, TimeSpan.Zero);
            else await batch.ExecuteAndWaitForReplicationAsync(1, TimeSpan.Zero);
        }).Throws<Exception>();
        await Assert.That(queued.Result).IsTrue();
        var item = capture.Items.Single();
        await Assert.That((bool)item.Tags["redis.client.errors.internal"]!).IsFalse();
        await Assert.That(item.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(server.Port);
    }

    [Test]
    [MatrixDataSource]
    public async Task CommandFailuresCarryTheirOwnServerPort(
        [Matrix("string", "bytes", "raw")] string surface, [Matrix(false, true)] bool commandMetrics)
    {
        using var configuration = new MetricConfigurationScope(new()
        {
            Groups = RespireMetricGroups.Resiliency | (commandMetrics ? RespireMetricGroups.Command : RespireMetricGroups.None),
        });
        await using var server = new FakeRespServer
        {
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? "-WRONGTYPE private-key\r\n"u8.ToArray() : null,
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Endpoints = [new("127.0.0.1", server.Port)],
        });
        using var capture = new Capture(serverPort: server.Port);
        // A listener for one client must ignore another node's measurement.
        RespireTelemetry.RecordError(new IOException(), false, endpoint: new("unrelated.example", -1));
        await Assert.That(async () =>
        {
            switch (surface)
            {
                case "string": await client.Strings.GetStringAsync("key"); break;
                case "bytes": await client.Strings.GetBytesAsync("key"); break;
                default: using (await client.ExecuteAsync("GET", ["key"])) break;
            }
        }).Throws<RespireServerException>();
        var item = capture.Items.Single();
        await Assert.That(item.Tags["server.address"]).IsEqualTo("127.0.0.1");
        await Assert.That(item.Tags["server.port"]).IsEqualTo(server.Port);
        await Assert.That(item.Tags["redis.client.errors.internal"]).IsEqualTo(false);
    }

    [Test]
    [MatrixDataSource]
    public async Task ErrorOwnersRetainLastAttemptedEndpoint([Matrix("pooled", "dispatch", "deferred")] string ownerKind)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var dispatch = ownerKind == "dispatch" ? DispatchResponseSource<int>.Start() : null;
        var pending = ownerKind == "deferred" ? new RespirePending<int>() : null;
        var observation = dispatch?.Observation ?? pending?.Observation ?? RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            observation.UseEndpoint(new("first.example", 6380));
            observation.Handled(new RespireServerException("MOVED 42 second.example:6381"));
            observation.UseEndpoint(new("second.example", 6381));
            var failure = new IOException("connection lost");
            if (dispatch is not null) dispatch.Fail(failure);
            else if (pending is not null) { pending.Fail(failure); pending.ReportError(); }
            else observation.Final(failure);
            var items = capture.Items.ToArray();
            await Assert.That(items.Length).IsEqualTo(2);
            await Assert.That(items[0].Tags["server.address"]).IsEqualTo("first.example");
            await Assert.That(items[0].Tags["server.port"]).IsEqualTo(6380);
            await Assert.That(items[1].Tags["server.address"]).IsEqualTo("second.example");
            await Assert.That(items[1].Tags["server.port"]).IsEqualTo(6381);
            await Assert.That(items[1].Tags["redis.client.operation.retry_attempts"]).IsEqualTo(1);
        }
        finally { observation.Dispose(); }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReturnedOwnersDoNotLeakEndpointToUnroutedFailures(bool dispatchOwner)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        if (dispatchOwner)
        {
            var first = DispatchResponseSource<int>.Start();
            first.Observation.UseEndpoint(new("old.example", 6380));
            first.CompleteInternal();
            DispatchResponseSource<int>.Start().Fail(new OperationCanceledException());
        }
        else
        {
            var first = RespireTelemetry.ErrorObservation.Rent(force: true);
            first.UseEndpoint(new("old.example", 6380));
            first.Dispose();
            using var next = RespireTelemetry.ErrorObservation.Rent(force: true);
            next.Final(new OperationCanceledException());
        }
        var item = capture.Items.Single();
        await Assert.That(item.Tags.ContainsKey("server.address")).IsFalse();
        await Assert.That(item.Tags.ContainsKey("server.port")).IsFalse();
    }

    [Test]
    [MatrixDataSource]
    public async Task ParallelRetryBorrowersKeepTheirOwnEndpoint(
        [Matrix("pooled", "dispatch", "deferred")] string ownerKind)
    {
        using var configuration = new MetricConfigurationScope(new() { Groups = RespireMetricGroups.Resiliency });
        using var capture = new Capture();
        var dispatch = ownerKind == "dispatch" ? DispatchResponseSource<int>.Start() : null;
        var pending = ownerKind == "deferred" ? new RespirePending<int>() : null;
        var observation = dispatch?.Observation ?? pending?.Observation ?? RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            observation.UseEndpoint(new("last.example", 6399));
            Parallel.For(0, 64, index => observation.Handled(
                index % 2 == 0 ? new IOException() : new RespireServerException("WRONGTYPE private-key"),
                index % 2 == 0 ? new("network.example", 6380) : new("server.example", 6381)));
            await Assert.That(capture.Items.Count).IsEqualTo(64);
            foreach (var item in capture.Items)
                await Assert.That(item.Tags["server.port"]).IsEqualTo(
                    Equals(item.Tags["redis.client.errors.category"], "network") ? 6380 : 6381);
            // An older borrower's late report must not replace the last selected endpoint.
            await Assert.That(observation.Endpoint).IsEqualTo(new RespireEndpoint("last.example", 6399));
        }
        finally
        {
            if (dispatch is not null) dispatch.CompleteInternal();
            else if (pending is not null) { pending.Fail(new IOException()); pending.ReportError(); }
            observation.Dispose();
        }
    }
}
