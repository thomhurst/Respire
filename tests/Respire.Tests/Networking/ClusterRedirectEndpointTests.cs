using System.Collections;
using System.Reflection;
using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Infrastructure;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterRedirectEndpointTests
{
    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task Parser_RejectsUnknownEndpoint(string code)
    {
        var error = new RespireServerException($"{code} 123 ?:6379");
        await Assert.That(ClusterRouter.TryParseRedirect(error, "source", out _, out _)).IsFalse();
    }

    [Test]
    [Arguments("redis.example:6379", "redis.example")]
    [Arguments("127.0.0.1:6379", "127.0.0.1")]
    [Arguments("[::1]:6379", "::1")]
    [Arguments("::1:6379", "::1")]
    [Arguments(":6379", "source")]
    public async Task Parser_PreservesRoutableEndpointForms(string address, string expectedHost)
    {
        foreach (var code in new[] { "MOVED", "ASK" })
        {
            var error = new RespireServerException($"{code} 123 {address}");
            await Assert.That(ClusterRouter.TryParseRedirect(error, "source", out var slot, out var endpoint)).IsTrue();
            await Assert.That(slot).IsEqualTo(123);
            await Assert.That(endpoint).IsEqualTo(new RespireEndpoint(expectedHost, 6379));
        }
    }

    [Test]
    [Arguments("MOVED", "immediate")]
    [Arguments("ASK", "immediate")]
    [Arguments("MOVED", "fire-and-forget")]
    [Arguments("ASK", "fire-and-forget")]
    public async Task UnknownEndpoint_PreservesServerErrorWithoutRegisteringTarget(string code, string path)
    {
        var message = $"{code} {ClusterHash.GetSlot("key")} ?:6379";
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray(), Encoding.ASCII.GetBytes($"-{message}\r\n"));
        await using var client = await RespireClient.ConnectAsync(Options(server));

        async Task Execute()
        {
            switch (path)
            {
                case "fire-and-forget":
                    await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "value");
                    break;
                default:
                    _ = await client.GetStringAsync("key");
                    break;
            }
        }
        var error = await Assert.That(async () => await Execute().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(2);
        await AssertNoUnknownTarget(client);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task Blocking_RejectsUnknownEndpointWithoutRegisteringPool(string code)
    {
        var slot = ClusterHash.GetSlot("key");
        var message = $"{code} {slot} ?:6379";
        await using var target = new FakeRespServer(2, Encoding.ASCII.GetBytes($"-{message}\r\n"));
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(Options(seed));

        var error = await Assert.That(async () =>
        {
            var response = await client.SendBlockingAsync("BLPOP", new Cmd1(Verbs.BLPop, "key"), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            response.Dispose();
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(1);
        await AssertNoUnknownTarget(client);
    }

    [Test]
    public async Task FireAndForget_StillDiscardsOrdinaryServerErrors()
    {
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(), "-WRONGTYPE ordinary error\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(seed));
        await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "value");
        await Assert.That(seed.ReceivedCommands[^1]).IsEqualTo("SET key value");
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task Transaction_RejectsUnknownEndpointBeforeMigrationHandling(string code)
    {
        var message = $"{code} {ClusterHash.GetSlot("key")} ?:6379";
        await using var server = new FakeRespServer(
            "*0\r\n"u8.ToArray(), FakeRespServer.OkReply, Encoding.ASCII.GetBytes($"-{message}\r\n"),
            "-EXECABORT Transaction discarded because of previous errors.\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(Options(server));
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value");

        var error = await Assert.That(async () => await transaction.CommitAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(pending.Error).IsSameReferenceAs(error);
        await AssertNoUnknownTarget(client);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task TrackedCorrection_RejectsUnknownEndpointBeforeIdentitySetup(string code)
    {
        var slot = ClusterHash.GetSlot("key");
        var message = $"{code} {slot} ?:6379";
        await using var target = new FakeRespServer(
            ":41\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), Encoding.ASCII.GetBytes($"-{message}\r\n"));
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(topology);
        await using var client = await RespireClient.ConnectAsync(Options(seed));
        var execution = await client.StartTrackedScriptExecutionAsync(
            RespireScript.Create("return redis.call('GET', KEYS[1])"), ["key"], [], CancellationToken.None);

        var error = await Assert.That(async () =>
        {
            using var result = await execution.Response.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).IsEqualTo(message);
        await Assert.That(target.ReceivedCommands.Count).IsEqualTo(3);
        await AssertNoUnknownTarget(client);
    }

    [Test]
    [Arguments("MOVED")]
    [Arguments("ASK")]
    public async Task EmptyEndpoint_UsesSourceHostWithRedirectedPort(string code)
    {
        byte[][] replies = code == "ASK"
            ? [FakeRespServer.OkReply, "$5\r\nvalue\r\n"u8.ToArray()]
            : ["$5\r\nvalue\r\n"u8.ToArray()];
        await using var target = new FakeRespServer(replies);
        await using var seed = new FakeRespServer("*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes($"-{code} {ClusterHash.GetSlot("key")} :{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(Options(seed));

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(target.ReceivedCommands[^1]).IsEqualTo("GET key");
    }

    private static RespireOptions Options(FakeRespServer seed) => new()
    {
        UseCluster = true,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
    };

    private static async Task AssertNoUnknownTarget(RespireClient client)
    {
        var router = client.Core.Cluster!;
        var nodesField = typeof(ClusterRouter).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var endpoints = ((IDictionary)nodesField.GetValue(router)!).Keys.Cast<RespireEndpoint>();
        await Assert.That(endpoints.Any(endpoint => endpoint.Host == "?")).IsFalse();

        var poolsField = typeof(ClusterRouter).GetField("_dedicatedPools", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var nodes = ((IDictionary)poolsField.GetValue(router)!).Keys.Cast<RespireConnectionMultiplexer>();
        await Assert.That(nodes.Any(node => node.Host == "?")).IsFalse();
    }
}
