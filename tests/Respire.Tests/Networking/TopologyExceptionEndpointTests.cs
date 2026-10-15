using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[NotInParallel]
public class TopologyExceptionEndpointTests
{
    [Test]
    [Arguments("MOVED", true)]
    [Arguments("ASK", true)]
    [Arguments("MOVED", false)]
    [Arguments("ASK", false)]
    public async Task RedirectFailureEndpointNamesTheServerThatReturnedTheError(string code, bool noRedirect)
    {
        await using var target = ClusterNode((command, _) => command == "GET key"
            ? "-WRONGTYPE target rejected\r\n"u8.ToArray() : null);
        await using var source = ClusterNode((command, _) => command == "GET key"
            ? Encoding.ASCII.GetBytes($"-{code} {ClusterHash.GetSlot("key")} 127.0.0.1:{target.Port}\r\n") : null);
        await using var client = await RespireClient.ConnectAsync(ClusterOptions(source));
        var error = await Assert.ThrowsAsync<RespireServerException>(async () =>
        {
            using var result = await client.ExecuteAsync(RespireCommands.String.GET, ["key"],
                noRedirect ? RespireCommandFlags.NoRedirect : RespireCommandFlags.None);
        });
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1",
            noRedirect ? source.Port : target.Port));
        await Assert.That(error.Code).IsEqualTo(noRedirect ? code : "WRONGTYPE");
    }

    [Test]
    [Arguments("string")]
    [Arguments("bytes")]
    [Arguments("converted")]
    [Arguments("stream")]
    public async Task ExhaustedPhysicalRepliesRetainTheFinalAttemptEndpoint(string shape)
    {
        var reads = 0;
        await using var server = Standalone((command, _) => command == "GET key"
            ? Count(ref reads, "-TRYAGAIN busy\r\n"u8.ToArray()) : null);
        await using var client = await RespireClient.ConnectAsync(StandaloneOptions(server));
        var error = await Assert.ThrowsAsync<RespireServerException>(async () =>
        {
            switch (shape)
            {
                case "string": await client.GetStringAsync("key"); break;
                case "bytes": await client.GetBytesAsync("key"); break;
                case "converted": await client.GetAsync<string>("key"); break;
                default: using (await client.Strings.GetStreamAsync("key")) { } break;
            }
        });
        await Assert.That(error!.Endpoint).IsEqualTo((RespireEndpoint?)new RespireEndpoint("127.0.0.1", server.Port));
        await Assert.That(error.Code).IsEqualTo("TRYAGAIN");
        // Stream GET has its own recovery policy; every shape must at least reach the server.
        await Assert.That(reads).IsGreaterThanOrEqualTo(1);
    }

    private static byte[] Count(ref int count, byte[] reply)
    {
        Interlocked.Increment(ref count);
        return reply;
    }

    private static FakeRespServer Standalone(Func<string, FakeRespServer, byte[]?> reply)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLIENT ID" ? ":1\r\n"u8.ToArray() : reply(command, server);
        return server;
    }

    private static FakeRespServer ClusterNode(Func<string, FakeRespServer, byte[]?> reply)
    {
        var server = new FakeRespServer(32, FakeRespServer.OkReply);
        server.ReplyOverride = (_, command) => command == "CLUSTER SLOTS"
            ? Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server.Port}\r\n")
            : reply(command, server);
        return server;
    }

    private static RespireOptions StandaloneOptions(FakeRespServer server) => new()
    {
        Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, Protocol = RespireProtocol.Resp2,
        ThreadPoolMonitoring = false, CommandTimeout = TimeSpan.FromSeconds(5),
        RetryPolicy = new() { InitialBackoff = TimeSpan.Zero },
    };

    private static RespireOptions ClusterOptions(FakeRespServer server)
        => StandaloneOptions(server) with { UseCluster = true, ClusterTopologyRefreshInterval = null };
}
