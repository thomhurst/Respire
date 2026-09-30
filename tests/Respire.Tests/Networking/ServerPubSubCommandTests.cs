using System.Text;
using Microsoft.Extensions.Logging;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerPubSubCommandTests
{
    [Test]
    public async Task FanOutClosesConnectionsWhenReplicaMembershipChanges()
    {
        await using var firstReplica = new FakeRespServer(Integer(7));
        await using var nextReplica = new FakeRespServer(Integer(9));
        await using var seed = new FakeRespServer(3, Integer(3));
        using var logger = new DisconnectLogger();
        var replicaPort = firstReplica.Port;
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command != "CLUSTER NODES") return false;
            var topology = $"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected 0-16383\n" +
                $"replica{replicaPort} 127.0.0.1:{replicaPort}@2 slave self 0 0 1 connected\n";
            _ = seed.SendRawAsync(Bulk(Encoding.ASCII.GetBytes(topology)));
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)], LoggerFactory = logger,
        });
        var first = await client.Server.PubSubPatternCountOnAllNodesAsync();
        await Assert.That(first.Single(x => x.Endpoint.Port == firstReplica.Port).Value).IsEqualTo(7);
        await Assert.That(logger.DisconnectCount).IsEqualTo(2);
        replicaPort = nextReplica.Port;
        var next = await client.Server.PubSubPatternCountOnAllNodesAsync();
        await Assert.That(next.Single(x => x.Endpoint.Port == nextReplica.Port).Value).IsEqualTo(9);
        await Assert.That(next.Any(x => x.Endpoint.Port == firstReplica.Port)).IsFalse();
        await Assert.That(logger.DisconnectCount).IsEqualTo(4);
        await Assert.That(seed.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientDisposalAbortsAnInFlightFanOut(bool duringHandshake)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selects = 0;
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command == "SELECT 1" && Interlocked.Increment(ref selects) == 2 && duringHandshake)
                {
                    received.TrySetResult();
                    return true;
                }
                if (duringHandshake || command != "PUBSUB NUMPAT") return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Connections = 1, Database = 1, Endpoints = [new("127.0.0.1", server.Port)],
        });
        var execution = client.Server.PubSubPatternCountOnAllNodesAsync().AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var results = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].IsSuccess).IsFalse();
        if (duringHandshake) await Assert.That(results[0].Error).IsAssignableTo<OperationCanceledException>();
        else await Assert.That(results[0].Error).IsTypeOf<RespireConnectionException>();
        await Assert.That(selects).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ChannelsAndCountsPreserveBinaryNamesWithoutKeyPrefixes(bool sharded)
    {
        byte[] name = [255, 0, 13, 10];
        await using var server = new FakeRespServer(
            Array(Bulk(name), Bulk([])), Array(Bulk(name), Integer(2), Bulk([]), Integer(0)), Integer(3));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("not-a-channel-prefix:");
        RespireChannel pattern = name;
        var channels = await view.Server.PubSubChannelsAsync(pattern, sharded);
        var counts = await view.Server.PubSubSubscriberCountsAsync([pattern, default], sharded);
        await Assert.That(await view.Server.PubSubPatternCountAsync()).IsEqualTo(3);
        await client.DisposeAsync();
        await Assert.That(channels[0].Bytes.ToArray()).IsEquivalentTo(name);
        await Assert.That(channels[1].Bytes.IsEmpty).IsTrue();
        await Assert.That(channels[0].Kind).IsEqualTo(sharded ? SubscriptionKind.Sharded : SubscriptionKind.Channel);
        await Assert.That(counts[0].Channel.Bytes.ToArray()).IsEquivalentTo(name);
        await Assert.That(counts[0].Subscribers).IsEqualTo(2);
        await Assert.That(counts[1].Subscribers).IsEqualTo(0);
        var frames = server.ReceivedArguments;
        await Assert.That(Encoding.ASCII.GetString(frames[0][1])).IsEqualTo(sharded ? "SHARDCHANNELS" : "CHANNELS");
        await Assert.That(frames[0][2]).IsEquivalentTo(name);
        await Assert.That(Encoding.ASCII.GetString(frames[1][1])).IsEqualTo(sharded ? "SHARDNUMSUB" : "NUMSUB");
        await Assert.That(frames[1][2]).IsEquivalentTo(name);
        await Assert.That(frames[1][3]).IsEmpty();
    }

    [Test]
    public async Task EmptyArgumentsRemainValidAndStandaloneFanOutHasOneEndpoint()
    {
        await using var server = new FakeRespServer(2, Array());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Server.PubSubChannelsAsync()).IsEmpty();
        await Assert.That(await client.Server.PubSubSubscriberCountsAsync([])).IsEmpty();
        var results = await client.Server.PubSubChannelsOnAllNodesAsync(sharded: true);
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].IsSuccess).IsTrue();
        await Assert.That(results[0].Value).IsEmpty();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["PUBSUB CHANNELS", "PUBSUB NUMSUB", "PUBSUB SHARDCHANNELS"]);
    }

    [Test]
    [Arguments("channels", "+OK\r\n")]
    [Arguments("channels", "*1\r\n$-1\r\n")]
    [Arguments("counts", "*1\r\n$1\r\nx\r\n")]
    [Arguments("counts", "*2\r\n$1\r\nx\r\n:-1\r\n")]
    [Arguments("patterns", ":-1\r\n")]
    public async Task MalformedRepliesFail(string operation, string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () =>
        {
            if (operation == "channels") await client.Server.PubSubChannelsAsync();
            else if (operation == "counts") await client.Server.PubSubSubscriberCountsAsync(["x"]);
            else await client.Server.PubSubPatternCountAsync();
        }).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task ClusterFanOutIncludesReplicaAndPreservesNodeErrors()
    {
        await using var replica = new FakeRespServer(Integer(7));
        await using var failed = new FakeRespServer("-ERR unavailable\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, Integer(3));
        var topology = $"self 10.0.0.1:1@2 myself,master - 0 0 1 connected 0-16383\n" +
            $"replica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n" +
            $"other 127.0.0.1:{failed.Port}@2 master,fail - 0 0 2 disconnected\n";
        var step = 0;
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command == "CLUSTER NODES") { Interlocked.Increment(ref step); _ = seed.SendRawAsync(Bulk(Encoding.ASCII.GetBytes(topology))); return true; }
            return false;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var results = await client.Server.PubSubPatternCountOnAllNodesAsync();
        await Assert.That(results.Length).IsEqualTo(3);
        await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value).IsEqualTo(3);
        await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Value).IsEqualTo(7);
        var failure = results.Single(x => x.Endpoint.Port == failed.Port);
        await Assert.That(failure.Error).IsTypeOf<RespireServerException>();
        await Assert.That(() => failure.Value).Throws<InvalidOperationException>();
        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["PUBSUB NUMPAT"]);
        await Assert.That(failed.ReceivedCommands).IsEquivalentTo(["PUBSUB NUMPAT"]);
        await Assert.That(step).IsEqualTo(1);
    }

    [Test]
    public async Task CancellationAfterDiscoveryIsAttributedToTheEndpoint()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, Integer(0))
        {
            SuppressReply = command =>
            {
                if (command != "PUBSUB NUMPAT") return false;
                received.TrySetResult();
                return true;
            },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var execution = client.Server.PubSubPatternCountOnAllNodesAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var results = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Length).IsEqualTo(1);
        await Assert.That(results[0].Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].IsSuccess).IsFalse();
        await Assert.That(results[0].Error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)results[0].Error!).CancellationToken).IsEqualTo(cancellation.Token);
    }

    [Test]
    public async Task CancellationBeforeDiscoveryDoesNotSend()
    {
        await using var server = new FakeRespServer();
        await using var client = RespireClient.Create($"localhost:{server.Port}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Server.PubSubPatternCountOnAllNodesAsync(cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task TopologyParsingPreservesIpv6HostnamesAndConfiguredSelfAddress()
    {
        var source = new RespireEndpoint("mapped", 9000);
        var endpoints = ServerCommands.ParseServerEndpoints(
            "a :0@0 myself,master - 0 0 0 connected\n" +
            "b ::1:6380@16380 slave a 0 0 0 connected\n" +
            "c 10.0.0.2:6381@16381,replica.example slave a 0 0 0 connected\n" +
            "d :0@0 handshake - 0 0 0 disconnected\n", source);
        await Assert.That(endpoints).IsEquivalentTo([source, new RespireEndpoint("::1", 6380), new RespireEndpoint("replica.example", 6381)]);
        await Assert.That(() => ServerCommands.ParseServerEndpoints("a :0@0 noaddr,master - 0 0 0 disconnected", source))
            .Throws<RespireConnectionException>();
    }

    private sealed class DisconnectLogger : ILoggerFactory, ILogger
    {
        internal int DisconnectCount;
        public ILogger CreateLogger(string categoryName) => this;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Debug && formatter(state, exception).StartsWith("Disconnected from", StringComparison.Ordinal))
                Interlocked.Increment(ref DisconnectCount);
        }
    }

    private static byte[] Integer(long value) => Encoding.ASCII.GetBytes($":{value}\r\n");
    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, .. "\r\n"u8];
    private static byte[] Array(params byte[][] values) => [.. Encoding.ASCII.GetBytes($"*{values.Length}\r\n"), .. values.SelectMany(x => x)];
    private static byte[] Slots(int port) => Array(Array(Integer(0), Integer(16383), Array(Bulk("127.0.0.1"u8.ToArray()), Integer(port))));
}
