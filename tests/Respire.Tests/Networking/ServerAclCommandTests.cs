using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerAclCommandTests
{
    [Test]
    public async Task Resp3UserMapsAndFlagSetsAreOwnedAfterReplyDisposal()
    {
        var reply = "%5\r\n$5\r\nflags\r\n~1\r\n$2\r\non\r\n$9\r\npasswords\r\n*0\r\n$8\r\ncommands\r\n$5\r\n-@all\r\n$4\r\nkeys\r\n$2\r\n~*\r\n$6\r\nfuture\r\n%1\r\n$1\r\nx\r\n$1\r\ny\r\n"u8.ToArray();
        await using var server = new FakeRespServer(reply, Bulk("replacement"u8.ToArray()));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var user = (await client.Server.AclGetUserAsync("u"))!;
        await client.Server.AclWhoAmIAsync();
        await client.DisposeAsync();
        await Assert.That(user.Flags).IsEquivalentTo(["on"]);
        await Assert.That(user.Commands).IsEqualTo("-@all");
        await Assert.That(user.Keys.RuleExpression!).IsEquivalentTo("~*"u8.ToArray());
        await Assert.That(user.AdditionalFields["future"][1].AsString()).IsEqualTo("y");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task AclMutationsFenceCachedReadsThroughSuccessOrFailure(bool allNodes, bool fail)
    {
        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (command == "HELLO 3")
            {
                _ = server.SendRawAsync("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
                return true;
            }
            if (!command.StartsWith("ACL SETUSER ", StringComparison.Ordinal)) return false;
            received.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = true, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        Insert(cache);
        var mutation = allNodes ? MutateAll() : client.Server.AclSetUserAsync("u", []).AsTask();
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert(cache);
        await server.SendRawAsync(fail ? "-ERR invalid rule\r\n"u8.ToArray() : FakeRespServer.OkReply, connection);
        if (fail && !allNodes) await Assert.That(async () => await mutation).ThrowsExactly<RespireServerException>();
        else await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);

        async Task MutateAll()
        {
            var results = await client.Server.AclSetUserOnAllNodesAsync("u", []);
            await Assert.That(results[0].IsSuccess).IsEqualTo(!fail);
        }

        static void Insert(ClientSideCacheCoordinator cache)
        {
            RespireKey key = "cached";
            var token = cache.BeginRead(in key);
            var value = RespValue.BulkString("old");
            cache.CompleteRead(in token, in value, allowInsert: true);
        }
    }

    [Test]
    public async Task MutationsRequireAdminBeforeAnyIoIncludingFanOut()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.Server.AclSetUserAsync("user", [">secret"])).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclDeleteUsersAsync(["user"])).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclLogResetAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclSetUserOnAllNodesAsync("user", [])).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclDeleteUsersOnAllNodesAsync(["user"])).ThrowsExactly<NotSupportedException>();
        await Assert.That(async () => await client.Server.AclLogResetOnAllNodesAsync()).ThrowsExactly<NotSupportedException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task TokensRemainBinarySafeAndUnprefixedAndDryRunExpandsCompoundCommand()
    {
        byte[] username = [255, 0, 13, 10];
        byte[] rule = [(byte)'>', 255, 0];
        byte[] argument = [254, 0, 32];
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply, ":2\r\n"u8.ToArray());
        await using var client = await Connect(server, true);
        var facet = client.WithKeyPrefix("ignored:").Server;
        await facet.AclSetUserAsync(username, ["reset", rule, "(+get ~*)"]);
        await Assert.That((await facet.AclDryRunAsync(username, RespireCommands.Connection.CLIENT_LIST, [argument])).IsAllowed).IsTrue();
        await Assert.That(await facet.AclDeleteUsersAsync([username, "other"])).IsEqualTo(2);
        var frames = server.ReceivedArguments;
        await Assert.That(frames[0][2]).IsEquivalentTo(username);
        await Assert.That(frames[0][4]).IsEquivalentTo(rule);
        await Assert.That(Encoding.UTF8.GetString(frames[0][5])).IsEqualTo("(+get ~*)");
        await Assert.That(Encoding.UTF8.GetString(frames[1][3])).IsEqualTo("CLIENT");
        await Assert.That(Encoding.UTF8.GetString(frames[1][4])).IsEqualTo("LIST");
        await Assert.That(frames[1][5]).IsEquivalentTo(argument);
        await Assert.That(frames[2][2]).IsEquivalentTo(username);
    }

    [Test]
    public async Task MissingUsersDenialsAndServerErrorsAreDistinct()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), Bulk("denied key"u8.ToArray()), "-ERR User not found\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(await client.Server.AclGetUserAsync("missing")).IsNull();
        var denial = await client.Server.AclDryRunAsync("user", "GET", ["key"]);
        await Assert.That(denial.IsAllowed).IsFalse();
        await Assert.That(denial.DenialReason).IsEqualTo("denied key");
        var error = await Assert.That(async () => await client.Server.AclDryRunAsync("missing", "GET", ["key"]))
            .ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).Contains("User not found");
    }

    [Test]
    public async Task InvalidArgumentsAndPreCancellationSendNothing()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await Connect(server, true);
        await Assert.That(async () => await client.Server.AclGetUserAsync(RespireValue.Null)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclSetUserAsync("u", [RespireValue.Null])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclDeleteUsersAsync([])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclCategoriesAsync(RespireValue.Null)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclLogAsync(-1)).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclDryRunAsync("u", default, [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.AclDryRunAsync("u", "GET", [RespireValue.Null])).Throws<ArgumentException>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.That(async () => await client.Server.AclWhoAmIAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.AclSetUserOnAllNodesAsync("u", [], cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task FanOutSnapshotsInputsBeforeDiscoveryAndAttributesFailures()
    {
        await using var replica = new FakeRespServer("-NOPERM denied on replica\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, FakeRespServer.OkReply);
        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command != "CLUSTER NODES") return false;
            discovering.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, AllowAdmin = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        byte[] username = [255, 0];
        byte[] rule = [(byte)'>', 254];
        var task = client.Server.AclSetUserOnAllNodesAsync(username, [rule]).AsTask();
        await discovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        username[0] = 0;
        rule[1] = 0;
        var topology = $"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected 0-16383\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n";
        await seed.SendRawAsync(Bulk(Encoding.ASCII.GetBytes(topology)));
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value).IsTrue();
        await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        foreach (var node in new[] { seed, replica })
        {
            var frame = node.ReceivedArguments.Single(x => Encoding.ASCII.GetString(x[0]) == "ACL");
            await Assert.That(frame[2]).IsEquivalentTo((byte[])[255, 0]);
            await Assert.That(frame[3]).IsEquivalentTo((byte[])[(byte)'>', 254]);
        }
    }

    [Test]
    public async Task FanOutCancellationAfterDiscoveryIsAnEndpointFailure()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => { if (command != "ACL LOG") return false; received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        var task = client.Server.AclLogOnAllNodesAsync(cancellationToken: cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Single().Endpoint.Port).IsEqualTo(server.Port);
        await Assert.That(results[0].Error).IsAssignableTo<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)results[0].Error!).CancellationToken).IsEqualTo(cancellation.Token);
    }

    private static ValueTask<RespireClient> Connect(FakeRespServer server, bool admin)
        => RespireClient.ConnectAsync(new RespireOptions { Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = admin });
    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{port}\r\n$4\r\nself\r\n");
}
