using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterDatabaseTests
{
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
    internal static byte[] Info(string version = "9.0.0", string server = "valkey", string mode = "cluster", bool verbatim = false)
    {
        var body = $"# Server\r\nredis_version:7.2.4\r\nserver_name:{server}\r\nvalkey_version:{version}\r\nserver_mode:{mode}\r\n";
        return verbatim
            ? Encoding.UTF8.GetBytes($"={Encoding.UTF8.GetByteCount(body) + 4}\r\ntxt:{body}\r\n")
            : Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(body)}\r\n{body}\r\n");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task HandshakeValidatesVersionBeforeSelectAndTracking(bool resp3)
    {
        byte[][] replies = resp3 ? [Hello, Info(verbatim: true), FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.PongReply]
            : [Info(), FakeRespServer.OkReply, FakeRespServer.PongReply];
        await using var server = new FakeRespServer(replies);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions
            {
                Database = 2, RequireClusterDatabaseSupport = true, Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2, EnableClientTracking = resp3,
            });
        using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
        await Assert.That(pong.AsString()).IsEqualTo("PONG");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(resp3
            ? new[] { "HELLO 3", "INFO SERVER", "SELECT 2", "CLIENT TRACKING ON OPTIN", "PING" }
            : new[] { "INFO SERVER", "SELECT 2", "PING" });
    }

    [Test]
    [Arguments("8.1.0", "valkey", "cluster")]
    [Arguments("9.0.0", "redis", "cluster")]
    [Arguments("9.0.0", "valkey", "standalone")]
    [Arguments("not-a-version", "valkey", "cluster")]
    [Arguments("+9", "valkey", "cluster")]
    [Arguments("9preview", "valkey", "cluster")]
    public async Task UnsupportedServersFailBeforeSelectOrUserCommands(string version, string name, string mode)
    {
        await using var server = new FakeRespServer(Info(version, name, mode));
        var error = await Assert.That(async () => await RespireClient.ConnectAsync(Options(server.Port)))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("database 0");
        await Assert.That(error.Message).Contains($"server_name={name}");
        await Assert.That(error.Message).Contains($"valkey_version={version}");
        await Assert.That(error.Message).Contains($"server_mode={mode}");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER" });
    }

    [Test]
    [Arguments("9.0.0-rc1")]
    [Arguments("10.0.0")]
    [Arguments("9")]
    [Arguments("10")]
    public async Task CompatibleVersionsAndFutureMajorsAreAccepted(string version)
    {
        await using var server = new FakeRespServer(Info(version), FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 2, RequireClusterDatabaseSupport = true });
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER", "SELECT 2" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MissingVersionFailsBeforeSelectAndIdentifiesMissingField(bool resp3)
    {
        const string body = "# Server\r\nserver_name:valkey\r\nserver_mode:cluster";
        var info = Encoding.UTF8.GetBytes($"${Encoding.UTF8.GetByteCount(body)}\r\n{body}\r\n");
        await using var server = new FakeRespServer(resp3 ? [Hello, info] : [info]);
        var error = await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 2, RequireClusterDatabaseSupport = true, Protocol = resp3 ? RespProtocol.Resp3 : RespProtocol.Resp2 }))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(error!.Message).Contains("valkey_version=<missing>");
        await Assert.That(server.ReceivedCommands).DoesNotContain("SELECT 2");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeniedInfoOrSelectDoesNotPublishConnection(bool select)
    {
        byte[] denied = "-NOPERM denied\r\n"u8.ToArray();
        await using var server = new FakeRespServer(select ? [Info(), denied] : [denied]);
        var error = await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 2, RequireClusterDatabaseSupport = true }))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Message).Contains(select ? "SELECT failed" : "INFO SERVER failed");
        await Assert.That(server.CommandsSeen).IsEqualTo(select ? 2 : 1);
    }

    [Test]
    public async Task AuthenticationErrorRemainsTheFirstFailure()
    {
        await using var server = new FakeRespServer("-WRONGPASS invalid credentials\r\n"u8.ToArray(),
            "-NOAUTH Authentication required\r\n"u8.ToArray());
        var error = await Assert.That(async () => await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 2, RequireClusterDatabaseSupport = true, Password = "secret" }))
            .ThrowsExactly<RespireConnectionException>();
        await Assert.That(error!.Message).Contains("AUTH failed");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "AUTH secret", "INFO SERVER" });
    }

    [Test]
    public async Task IncompatibleSeedFailsBeforeTryingAnotherSeed()
    {
        await using var incompatible = new FakeRespServer(Info("8.1.0"));
        await using var compatible = new FakeRespServer(Info(), FakeRespServer.OkReply, "*0\r\n"u8.ToArray());
        var options = Options(incompatible.Port);
        options.Endpoints.Add(new RespireEndpoint("127.0.0.1", compatible.Port));
        await Assert.That(async () => await RespireClient.ConnectAsync(options))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(incompatible.ReceivedCommands).IsEquivalentTo(["INFO SERVER"]);
        await Assert.That(compatible.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task DedicatedLeaseSelectsBeforeItsFirstCommand()
    {
        await using var server = new FakeRespServer(Info(), FakeRespServer.OkReply, FakeRespServer.PongReply);
        await using var client = RespireClient.Create(Options(server.Port));
        var pool = client.Core.DedicatedPool;
        var connection = await pool.RentAsync(CancellationToken.None);
        try
        {
            using var pong = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame));
            await Assert.That(pong.AsString()).IsEqualTo("PONG");
            await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER", "SELECT 2", "PING"]);
        }
        finally
        {
            await pool.DiscardAsync(connection);
        }
    }

    [Test]
    public async Task PubSubSocketSelectsBeforeSubscribing()
    {
        byte[] subscribed = "*3\r\n$9\r\nsubscribe\r\n$2\r\nch\r\n:1\r\n"u8.ToArray();
        await using var server = new FakeRespServer(2, Info(), FakeRespServer.OkReply, subscribed);
        await using var seed = new FakeRespServer(Info(), FakeRespServer.OkReply, Topology(server.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await using var subscription = await client.SubscribeAsync("ch").AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            ["INFO SERVER", "SELECT 2", "INFO SERVER", "SELECT 2", "SUBSCRIBE ch"]);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task CancelledCapabilityDiscoveryDoesNotSendSelect()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => { arrived.TrySetResult(); return true; } };
        using var cancellation = new CancellationTokenSource();
        var connecting = RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            new RespireConnectionOptions { Database = 2, RequireClusterDatabaseSupport = true }, cancellationToken: cancellation.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await connecting).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task EveryRoutedDestinationSelectsBeforeRedirectedCommand(bool ask)
    {
        await using var target = new FakeRespServer(ask
            ? [Info(), FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply]
            : [Info(), FakeRespServer.OkReply, FakeRespServer.OkReply]);
        var slot = ClusterHash.GetSlot("key");
        await using var owner = new FakeRespServer(Info(), FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-{(ask ? "ASK" : "MOVED")} {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var seed = new FakeRespServer(Info(), FakeRespServer.OkReply, Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await client.SetAsync("key", "value");
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER", "SELECT 2", "CLUSTER SLOTS" });
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER", "SELECT 2", "SET key value" });
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(ask
            ? new[] { "INFO SERVER", "SELECT 2", "ASKING", "SET key value" }
            : new[] { "INFO SERVER", "SELECT 2", "SET key value" });
    }

    [Test]
    public async Task UnsupportedRedirectTargetDoesNotInheritSeedCapabilities()
    {
        await using var target = new FakeRespServer(Info("8.1.0"));
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(Info(), FakeRespServer.OkReply, "*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-MOVED {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await Assert.That(async () => await client.SetAsync("key", "value")).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(target.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER" });
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task IncompatibleDiscoveredOwnerSurfacesWithoutFallback(int path)
    {
        await using var owner = new FakeRespServer(Info("8.1.0"));
        await using var seed = new FakeRespServer(Info(), FakeRespServer.OkReply, Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.That(async () =>
        {
            if (path == 0) await client.SetAsync("key", "value", cancellationToken: deadline.Token);
            else if (path == 1)
                await client.Core.Cluster!.GetDedicatedPoolAsync(ClusterHash.GetSlot("key"), deadline.Token, discovery: null);
            else await client.SubscribeAsync("ch", deadline.Token);
        }).ThrowsExactly<RespireConfigurationException>();
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(["INFO SERVER"]);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["INFO SERVER", "SELECT 2", "CLUSTER SLOTS"]);
    }

    [Test]
    public async Task IncompatibleDedicatedSocketSurfacesConfigurationError()
    {
        await using var server = new FakeRespServer(Info("8.1.0"));
        await using var client = RespireClient.Create(Options(server.Port));
        await Assert.That(async () => await client.Core.DedicatedPool.RentAsync(CancellationToken.None))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER"]);
    }

    [Test]
    public async Task IncompatiblePubSubSocketDoesNotInheritCommandSocketCapability()
    {
        var info = Info();
        var requests = 0;
        await using var owner = new FakeRespServer(2, info, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command == "INFO SERVER" && Interlocked.Increment(ref requests) == 2)
                    Info("8.0.0").CopyTo(info, 0);
                return false;
            },
        };
        await using var seed = new FakeRespServer(Info(), FakeRespServer.OkReply, Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await Assert.That(async () => await client.SubscribeAsync("ch").AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireConfigurationException>();
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(["INFO SERVER", "SELECT 2", "INFO SERVER"]);
    }

    [Test]
    public async Task BackgroundReconnectReportsIncompatibleReplacement()
    {
        var info = Info();
        await using var server = new FakeRespServer(2, info, FakeRespServer.OkReply);
        await using var multiplexer = Respire.Infrastructure.RespireConnectionMultiplexer.Create(
            "127.0.0.1", server.Port, 1, Options(server.Port).ToConnectionOptions(), null);
        await multiplexer.EnsureConnectedAsync();
        var failure = new TaskCompletionSource<RespireConnectionStateChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        multiplexer.StateChanged += change =>
        {
            if (change.Error is RespireConfigurationException) failure.TrySetResult(change);
        };
        // The next physical socket sees a downgraded server at the same endpoint.
        Info("8.0.0").CopyTo(info, 0);
        await multiplexer.GetConnection().DisposeAsync();
        await Assert.That(() => multiplexer.GetConnection()).Throws<RespireConnectionException>();
        var observed = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(observed.State).IsEqualTo(RespireConnectionState.Disconnected);
        await Assert.That(observed.Error).IsTypeOf<RespireConfigurationException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER", "SELECT 2", "INFO SERVER"]);
        await Assert.That(multiplexer.IsConnected).IsFalse();
    }

    [Test]
    public async Task ServerIdentityAndModeAreCaseInsensitive()
    {
        await using var server = new FakeRespServer(Info(server: "VALKEY", mode: "CLUSTER"), FakeRespServer.OkReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port,
            Options(server.Port).ToConnectionOptions());
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["INFO SERVER", "SELECT 2"]);
    }

    [Test]
    public async Task ReconnectRepeatsCapabilityCheckAndSelect()
    {
        await using var server = new FakeRespServer(2, Info(), FakeRespServer.OkReply);
        var options = Options(server.Port).ToConnectionOptions();
        await using var multiplexer = Respire.Infrastructure.RespireConnectionMultiplexer.Create(
            "127.0.0.1", server.Port, 1, options, null);
        await multiplexer.EnsureConnectedAsync();
        await multiplexer.GetConnection().DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await multiplexer.GetHealthyConnectionAsync(timeout.Token);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(new[] { "INFO SERVER", "SELECT 2", "INFO SERVER", "SELECT 2" });
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DatabaseZeroAndStandaloneKeepTheirExistingHandshake(bool cluster)
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        var options = Options(server.Port) with { UseCluster = cluster, Database = cluster ? 0 : 2 };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, options.ToConnectionOptions());
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(cluster ? Array.Empty<string>() : ["SELECT 2"]);
    }

    private static RespireOptions Options(int port) => new()
    {
        Protocol = RespProtocol.Resp2,
        UseCluster = true, Database = 2, Connections = 1, Endpoints = { new RespireEndpoint("127.0.0.1", port) },
    };

    private static byte[] Topology(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
