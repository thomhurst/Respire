using System.Text;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ServerMetadataCommandTests
{
    [Test]
    public async Task LookupNamesAndSimulatedTokensUseDifferentBoundariesWithoutPrefixing()
    {
        byte[] key = [255, 0, 32];
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray(), "%0\r\n"u8.ToArray(),
            [.. "*1\r\n"u8, .. Bulk(key)], "*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var facet = client.WithKeyPrefix("ignored:").Server;
        await facet.CommandInfoAsync([" CONFIG  GET ", "GET"]);
        await facet.CommandDocsAsync(["CLIENT ID"]);
        var keys = await facet.CommandGetKeysAsync(" XGROUP  CREATE ", [key, "group with spaces", "0"]);
        await facet.ModuleListAsync();
        await client.DisposeAsync();
        await Assert.That(server.ReceivedCommands.Take(2)).IsEquivalentTo(["COMMAND INFO CONFIG|GET GET", "COMMAND DOCS CLIENT|ID"]);
        var frame = server.ReceivedArguments[2];
        await Assert.That(frame.Length).IsEqualTo(7);
        await Assert.That(Encoding.ASCII.GetString(frame[2])).IsEqualTo("XGROUP");
        await Assert.That(Encoding.ASCII.GetString(frame[3])).IsEqualTo("CREATE");
        await Assert.That(frame[4]).IsEquivalentTo(key);
        await Assert.That(Encoding.ASCII.GetString(frame[5])).IsEqualTo("group with spaces");
        await Assert.That(keys.Single()).IsEquivalentTo(key);
    }

    [Test]
    public async Task Resp3MapsAndSetsAreParsedAndOwned()
    {
        var info = "*1\r\n*10\r\n$3\r\nget\r\n:2\r\n~1\r\n+readonly\r\n:1\r\n:1\r\n:1\r\n~1\r\n+@read\r\n~0\r\n*0\r\n*0\r\n"u8.ToArray();
        var docs = "%1\r\n+get\r\n%2\r\n+summary\r\n+description\r\n+doc_flags\r\n~1\r\n+future\r\n"u8.ToArray();
        var modules = "*1\r\n%3\r\n+name\r\n+mod\r\n+ver\r\n:1\r\n+future\r\n%1\r\n+x\r\n+y\r\n"u8.ToArray();
        await using var server = new FakeRespServer(info, docs, modules);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var commands = await client.Server.CommandInfoAsync(["GET"]);
        var descriptions = await client.Server.CommandDocsAsync(["GET"]);
        var loaded = await client.Server.ModuleListAsync();
        await client.DisposeAsync();
        await Assert.That(commands[0]!.Flags).IsEquivalentTo(["readonly"]);
        await Assert.That(commands[0]!.AclCategories).IsEquivalentTo(["@read"]);
        await Assert.That(descriptions[0].Flags).IsEquivalentTo(["future"]);
        await Assert.That(loaded[0].AdditionalFields["future"][1].AsString()).IsEqualTo("y");
    }

    [Test]
    public async Task AdminGateAndPreCancellationRunBeforeIo()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        foreach (var operation in Mutations(client.Server))
            await Assert.That(operation).ThrowsExactly<InvalidOperationException>();
        await using var admin = RespireClient.Create(new RespireOptions { Endpoints = [new("127.0.0.1", server.Port)], AllowAdmin = true });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        foreach (var operation in Mutations(admin.Server, cancellation.Token))
            await Assert.That(operation).Throws<OperationCanceledException>();
        await Assert.That(async () => await admin.Server.CommandInfoAsync(cancellationToken: cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await admin.Server.ModuleListOnAllNodesAsync(cancellation.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await client.Server.CommandInfoAsync([default])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.CommandGetKeysAsync(default, [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.Server.CommandGetKeysAsync("GET", [RespireValue.Null])).Throws<ArgumentException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task MutationWireAndBackgroundAcceptanceRemainDistinctFromServerErrors()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply, FakeRespServer.OkReply, FakeRespServer.OkReply,
            "+Background saving started\r\n"u8.ToArray(), "+Background saving scheduled\r\n"u8.ToArray(),
            "+Background append only file rewriting scheduled\r\n"u8.ToArray(), "-ERR Background save already in progress\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions { Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = true });
        await client.Server.ConfigRewriteAsync();
        await client.Server.ConfigResetStatisticsAsync();
        await client.Server.SaveAsync();
        await Assert.That((await client.Server.BackgroundSaveAsync()).State).IsEqualTo(RespireBackgroundPersistenceState.Started);
        await Assert.That((await client.Server.BackgroundSaveAsync(true)).State).IsEqualTo(RespireBackgroundPersistenceState.Scheduled);
        await Assert.That((await client.Server.BackgroundRewriteAofAsync()).State).IsEqualTo(RespireBackgroundPersistenceState.Scheduled);
        var error = await Assert.That(async () => await client.Server.BackgroundSaveAsync()).ThrowsExactly<RespireServerException>();
        await Assert.That(error!.Message).Contains("already in progress");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["CONFIG REWRITE", "CONFIG RESETSTAT", "SAVE", "BGSAVE", "BGSAVE SCHEDULE", "BGREWRITEAOF", "BGSAVE"]);
    }

    [Test]
    public async Task FanOutSnapshotsBinaryInputsBeforeDiscoveryAndPreservesFailures()
    {
        byte[] key = [255, 0];
        await using var replica = new FakeRespServer("-NOPERM replica denied\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(2, [.. "*1\r\n"u8, .. Bulk(key)]);
        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = command =>
        {
            if (command == "CLUSTER SLOTS") { _ = seed.SendRawAsync(Slots(seed.Port)); return true; }
            if (command != "CLUSTER NODES") return false;
            discovering.TrySetResult();
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions { UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)] });
        var task = client.WithKeyPrefix("ignored:").Server.CommandGetKeysOnAllNodesAsync("GET", [key]).AsTask();
        await discovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        key.AsSpan().Clear();
        await seed.SendRawAsync(Bulk(Encoding.ASCII.GetBytes($"self 127.0.0.1:{seed.Port}@2 myself,master - 0 0 1 connected 0-16383\nreplica 127.0.0.1:{replica.Port}@2 slave self 0 0 1 connected\n")));
        var results = await task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(results.Length).IsEqualTo(2);
        await Assert.That(results.Single(x => x.Endpoint.Port == seed.Port).Value.Single()).IsEquivalentTo((byte[])[255, 0]);
        await Assert.That(results.Single(x => x.Endpoint.Port == replica.Port).Error).IsTypeOf<RespireServerException>();
        foreach (var node in new[] { seed, replica })
            await Assert.That(node.ReceivedArguments.Single(frame => Encoding.ASCII.GetString(frame[0]) == "COMMAND")[3]).IsEquivalentTo((byte[])[255, 0]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancellationAfterSendPreservesSingleAndPerNodeSemantics(bool allNodes)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            SuppressReply = command => { if (command != "SAVE") return false; received.TrySetResult(); return true; },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions { Connections = 1, AllowAdmin = true, Endpoints = [new("127.0.0.1", server.Port)] });
        using var cancellation = new CancellationTokenSource();
        var task = allNodes ? CheckAll() : client.Server.SaveAsync(cancellation.Token).AsTask();
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        if (allNodes) await task.WaitAsync(TimeSpan.FromSeconds(5));
        else await Assert.That(async () => await task.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();

        async Task CheckAll()
        {
            var result = (await client.Server.SaveOnAllNodesAsync(cancellation.Token)).Single();
            await Assert.That(result.Endpoint.Port).IsEqualTo(server.Port);
            await Assert.That(result.Error).IsAssignableTo<OperationCanceledException>();
        }
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ConfigurationMutationsFenceCachedReadsThroughSuccessOrFailure(bool allNodes, bool fail)
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
            if (!command.Equals("CONFIG RESETSTAT", StringComparison.Ordinal)) return false;
            received.TrySetResult(server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, AllowAdmin = true, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        Insert(cache);
        var mutation = allNodes ? MutateAll() : client.Server.ConfigResetStatisticsAsync().AsTask();
        var connection = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);
        Insert(cache);
        await server.SendRawAsync(fail ? "-ERR invalid rule\r\n"u8.ToArray() : FakeRespServer.OkReply, connection);
        if (fail && !allNodes) await Assert.That(async () => await mutation).ThrowsExactly<RespireServerException>();
        else await mutation.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(cache.Count).IsEqualTo(0);

        async Task MutateAll()
        {
            var results = await client.Server.ConfigResetStatisticsOnAllNodesAsync();
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
    public async Task ReadOnlyMetadataPreservesCachedEntriesForBothTargets()
    {
        await using var server = new FakeRespServer(5, "*0\r\n"u8.ToArray());
        server.SuppressReply = command =>
        {
            if (command != "HELLO 3") return false;
            _ = server.SendRawAsync("%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), server.ReceivedConnectionIds[^1]);
            return true;
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1, ClientSideCache = new(),
        });
        var cache = client.Core.ClientCache!;
        RespireKey key = "cached";
        var token = cache.BeginRead(in key);
        var value = RespValue.BulkString("retained");
        cache.CompleteRead(in token, in value, allowInsert: true);
        await client.Server.CommandInfoAsync(["GET"]);
        await client.Server.CommandDocsAsync(["GET"]);
        await client.Server.CommandGetKeysAsync("GET", ["key"]);
        await client.Server.ModuleListAsync();
        await client.Server.CommandInfoOnAllNodesAsync(["GET"]);
        await client.Server.CommandDocsOnAllNodesAsync(["GET"]);
        await client.Server.CommandGetKeysOnAllNodesAsync("GET", ["key"]);
        await client.Server.ModuleListOnAllNodesAsync();
        await Assert.That(cache.Count).IsEqualTo(1);
    }

    private static Func<Task>[] Mutations(IServerCommands server, CancellationToken token = default) =>
    [
        async () => await server.ConfigRewriteAsync(token), async () => await server.ConfigResetStatisticsAsync(token),
        async () => await server.SaveAsync(token), async () => await server.BackgroundSaveAsync(cancellationToken: token),
        async () => await server.BackgroundRewriteAofAsync(token), async () => await server.ConfigRewriteOnAllNodesAsync(token),
        async () => await server.ConfigResetStatisticsOnAllNodesAsync(token), async () => await server.SaveOnAllNodesAsync(token),
        async () => await server.BackgroundSaveOnAllNodesAsync(cancellationToken: token), async () => await server.BackgroundRewriteAofOnAllNodesAsync(token),
    ];

    private static byte[] Bulk(byte[] value) => [.. Encoding.ASCII.GetBytes($"${value.Length}\r\n"), .. value, 13, 10];
    private static byte[] Slots(int port) => Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:0\r\n:16383\r\n*3\r\n$9\r\n127.0.0.1\r\n:{port}\r\n$4\r\nself\r\n");
}
