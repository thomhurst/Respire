using System.Text;
using Respire.Infrastructure;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterNodeIdentityTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnnouncedAliasesShareMultiplexerAndDedicatedPool(bool includeNodeId)
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(Topology("localhost", target.Port, includeNodeId ? "node-1" : null));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        var hostname = new RespireEndpoint("localhost", target.Port);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname), router.GetMultiplexer(address))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(hostname),
            router.GetMultiplexer(new RespireEndpoint("LOCALHOST", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(hostname), router.GetDedicatedPool(address))).IsTrue();
        await Assert.That(router.GetMultiplexer(address).Host).IsEqualTo("localhost");
    }

    [Test]
    public async Task ConfiguredSeedAliasRetainsExistingMultiplexer()
    {
        var commandReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer
        {
            SuppressReply = _ => { commandReceived.TrySetResult(); return true; },
        };
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        var connect = router.EnsureConnectedAsync(default).AsTask();
        await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await seed.SendRawAsync(Topology("localhost", seed.Port, "seed-node"));
        await connect;

        await Assert.That(ReferenceEquals(primary,
            router.GetMultiplexer(new RespireEndpoint("localhost", seed.Port)))).IsTrue();
    }

    [Test]
    public async Task DifferentNodeIdsDoNotMergeThroughConflictingMetadata()
    {
        await using var first = new FakeRespServer();
        var firstRange = Range(0, 8191, "127.0.0.1", first.Port, "first", "shared.invalid");
        var secondRange = Range(8192, 16383, "localhost", first.Port, "second", "shared.invalid");
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + firstRange + secondRange));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", first.Port)),
            router.GetMultiplexer(new RespireEndpoint("localhost", first.Port)))).IsFalse();
    }

    [Test]
    public async Task NodeIdLinksPreferredFormsWithoutMetadataAliases()
    {
        await using var target = new FakeRespServer();
        var ranges = Range(0, 8191, "localhost", target.Port, "same-node", "", includeAliases: false)
            + Range(8192, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false);
        await using var seed = new FakeRespServer(Encoding.UTF8.GetBytes("*2\r\n" + ranges));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);

        await Assert.That(ReferenceEquals(
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task EndpointReassignedToDifferentNodeIdGetsNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", target.Port, "old-node"),
            Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var endpoint = new RespireEndpoint("localhost", target.Port);
        var old = router.GetMultiplexer(endpoint);
        await router.GetMasterConnectionsAsync(default);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(endpoint))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(endpoint),
            router.GetMultiplexer(new RespireEndpoint("127.0.0.1", target.Port)))).IsTrue();
    }

    [Test]
    public async Task ChangedPortForKnownNodeIdCreatesTransportAtNewPort()
    {
        await using var original = new FakeRespServer(FakeRespServer.PongReply);
        await using var replacement = new FakeRespServer(FakeRespServer.PongReply);
        await using var seed = new FakeRespServer(
            Topology("localhost", original.Port, "same-node"),
            Topology("localhost", replacement.Port, "same-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var connections = await router.GetMasterConnectionsAsync(default);

        await Assert.That(connections).Count().IsEqualTo(1);
        await Assert.That(connections[0].Port).IsEqualTo(replacement.Port);
    }

    [Test]
    public async Task StableNodeIdMovingHostUsesNewTransport()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.2", target.Port, "same-node", "", includeAliases: false));
        var replacement = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "127.0.0.1", target.Port, "same-node", "", includeAliases: false));
        await using var seed = new FakeRespServer(original, replacement);
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connections = await router.GetMasterConnectionsAsync(timeout.Token);

        await Assert.That(connections[0].Host).IsEqualTo("127.0.0.1");
    }

    [Test]
    public async Task ReassignedAliasResolvesToItsNewNodeId()
    {
        await using var target = new FakeRespServer(FakeRespServer.PongReply);
        var original = Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, "old.invalid", target.Port, "old-node", "old.invalid"));
        await using var seed = new FakeRespServer(original, Topology("localhost", target.Port, "new-node"));
        var options = Options(seed.Port);
        await using var primary = RespireConnectionMultiplexer.Create("127.0.0.1", seed.Port, options: options.ToConnectionOptions());
        await using var router = new ClusterRouter(options, primary);
        await router.EnsureConnectedAsync(default);
        var address = new RespireEndpoint("127.0.0.1", target.Port);
        var old = router.GetMultiplexer(address);
        await router.GetMasterConnectionsAsync(default);

        await Assert.That(ReferenceEquals(old, router.GetMultiplexer(address))).IsFalse();
        await Assert.That(ReferenceEquals(router.GetMultiplexer(address),
            router.GetMultiplexer(new RespireEndpoint("localhost", target.Port)))).IsTrue();
        await Assert.That(ReferenceEquals(router.GetDedicatedPool(address),
            router.GetDedicatedPool(new RespireEndpoint("localhost", target.Port)))).IsTrue();
    }

    internal static byte[] Topology(string preferred, int port, string? nodeId)
        => Encoding.UTF8.GetBytes("*1\r\n" + Range(0, 16383, preferred, port, nodeId, "localhost"));

    private static string Range(
        int start, int end, string preferred, int port, string? nodeId, string hostname, bool includeAliases = true)
        => $"*3\r\n:{start}\r\n:{end}\r\n*{(includeAliases ? 4 : 3)}\r\n{Bulk(preferred)}:{port}\r\n{(nodeId is null ? "$-1\r\n" : Bulk(nodeId))}"
            + (includeAliases ? $"%2\r\n+hostname\r\n{Bulk(hostname)}+ip\r\n{Bulk("127.0.0.1")}" : "");

    private static string Bulk(string value) => $"${Encoding.UTF8.GetByteCount(value)}\r\n{value}\r\n";

    private static RespireOptions Options(int seedPort) => new()
    {
        UseCluster = true,
        Endpoints = { new RespireEndpoint("127.0.0.1", seedPort) },
        Connections = 1,
    };
}
