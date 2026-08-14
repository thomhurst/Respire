using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using Respire.Internal;
using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientSideCacheTests
{
    private static readonly byte[] HelloReply = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    public async Task ClientCachingCommand_WritesExpectedFrame()
    {
        var buffer = new WriteBuffer(128);
        var writer = new RespWriter(buffer);
        new ClientCachingCommand().Write(ref writer);

        await Assert.That(buffer.WrittenMemory.ToArray())
            .IsEquivalentTo("*3\r\n$6\r\nCLIENT\r\n$7\r\nCACHING\r\n$3\r\nYES\r\n"u8.ToArray());
        buffer.Release();
    }

    [Test]
    public async Task ValidatedPrefix_WritesBothCommandsAndReturnsFinalReply()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", server.Port);
        var caching = new ClientCachingCommand();
        var get = new Cmd1(Verbs.Get, "key");

        var response = await connection.SendValidatedPrefixedAsync(
            in caching, in get, commandName: "GET");

        await Assert.That(response.AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "CLIENT CACHING YES",
            "GET key",
        ]);
        response.Dispose();
    }

    [Test]
    public async Task Get_MissIsTrackedAndSecondReadIsLocal()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        var first = await client.GetStringAsync("key");
        await Assert.That(first).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");

        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HELLO 3",
            "CLIENT TRACKING ON OPTIN",
            "CLIENT CACHING YES",
            "GET key",
        ]);
        var statistics = client.ClientSideCache!.GetStatistics();
        await Assert.That(statistics.Hits).IsEqualTo(1);
        await Assert.That(statistics.Misses).IsEqualTo(1);
        await Assert.That(statistics.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ClientCachingError_IsSurfacedAndReadIsNotCached()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "-ERR caching disabled\r\n"u8.ToArray(),
            "$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        var error = await Assert.That(async () => await client.GetStringAsync("key"))
            .ThrowsExactly<RespireServerException>();

        await Assert.That(error!.CommandName).IsEqualTo("GET");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.TakeLast(2)).IsEquivalentTo([
            "CLIENT CACHING YES", "GET key",
        ]);
    }

    [Test]
    public async Task InvalidationPush_EvictsKeyAndForcesTrackedRead()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\none\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("one");
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("two");
        await Assert.That(server.ReceivedCommands.Count(static command => command == "GET key"))
            .IsEqualTo(2);
    }

    [Test]
    public async Task NullInvalidation_FlushesAllEntries()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$1\r\na\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$1\r\nb\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        await server.SendRawAsync(">2\r\n+invalidate\r\n_\r\n"u8.ToArray());
        await WaitUntilAsync(() => client.ClientSideCache!.Count == 0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("b");
    }

    [Test]
    public async Task InvalidationRacingReadResponse_PreventsStaleInsertion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        server.DelayReply(3, 250);
        await using var client = await ConnectAsync(server);

        var read = client.GetStringAsync("key");
        await WaitUntilAsync(() => server.CommandsSeen >= 4);
        await server.SendRawAsync(">2\r\n+invalidate\r\n*1\r\n$3\r\nkey\r\n"u8.ToArray());

        await Assert.That(await read).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task LocalMutation_EagerlyInvalidatesBeforeReply()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        await client.SetAsync("key", "new");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task MutationCompletion_RejectsReadStartedAfterEagerInvalidation()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        server.MinimumCommandsBeforeReply = 3;
        var mutation = client.SetAsync("key", "new").AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await mutation;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task BroadMutationCompletion_RejectsReadStartedAfterFlush()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        server.MinimumCommandsBeforeReply = 3;
        var mutation = client.Strings.SetManyAsync(("key", "new"), ("other", "value")).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await mutation;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task BatchCompletion_RejectsReadStartedDuringExecution()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        using var batch = client.CreateBatch();
        _ = batch.Strings.Set("key", "new");
        server.MinimumCommandsBeforeReply = 3;
        var execution = batch.ExecuteAsync().AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var racingRead = client.GetStringAsync("key").AsTask();

        await execution;
        await Assert.That(await racingRead).IsEqualTo("old");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        server.MinimumCommandsBeforeReply = 1;
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task TransactionCompletion_FlushesEntriesInsertedDuringExecution()
    {
        await using var server = new FakeRespServer(
            2,
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n+OK\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);
        await using var transaction = client.CreateTransaction();
        _ = transaction.Strings.Set("key", "new");

        var commit = transaction.CommitAsync().AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        InsertCachedValue(cache, "key", "old");
        await Assert.That(cache.Count).IsEqualTo(1);

        await commit;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NativeScript_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nnew\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = client.Scripts.ExecuteAsync(script, ["key"]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("new");
    }

    [Test]
    public async Task RawStoredProcedure_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var execution = client.ExecuteAsync(
            RespireCommands.Scripting.EVAL, ["return redis.call('DEL', KEYS[1])", 1, "key"]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task BlockingMutation_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            2,
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray());
        server.DelayReply(1, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var execution = client.ExecuteAsync(RespireCommands.List.BLPOP, ["key", 0]).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 6);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CorrectionScript_FencesCacheThroughCompletion()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(4, 250);
        await using var client = await ConnectAsync(server);

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = client.ExecuteOnAllConnectionsAsync(script, ["key"], []).AsTask();
        await WaitUntilAsync(() => server.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        await execution;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TrackedScriptResponse_OwnsCompletionFence()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            ":123\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\nold\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        server.DelayReply(5, 250);
        await using var client = await ConnectAsync(server);
        await client.EnsureReliableCorrectionOrderingAsync();

        await client.GetStringAsync("key");
        var script = RespireScript.Create("return redis.call('DEL', KEYS[1])");
        var execution = await client.StartTrackedScriptExecutionAsync(
            script,
            ["key"],
            [],
            CancellationToken.None);
        await WaitUntilAsync(() => server.CommandsSeen >= 6);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "old");

        using var result = await execution.Response;

        await Assert.That(result.AsInteger()).IsEqualTo(1);
        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task MGet_CachesPerKeyAndFetchesOnlyInvalidatedMisses()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "*1\r\n$2\r\nb2\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);

        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b"]);
        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b"]);
        await client.SetAsync("b", "b2");
        await Assert.That(await client.Strings.GetManyAsync("a", "b"))
            .IsEquivalentTo((string?[])["a", "b2"]);

        await Assert.That(server.ReceivedCommands[^1]).IsEqualTo("MGET b");
    }

    [Test]
    public async Task PrefixedViews_UseResolvedWireKeyIdentity()
    {
        await using var server = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$3\r\none\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$3\r\ntwo\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server);
        var first = client.WithKeyPrefix("a:");
        var second = client.WithKeyPrefix("b:");

        await Assert.That(await first.GetStringAsync("key")).IsEqualTo("one");
        await Assert.That(await second.GetStringAsync("key")).IsEqualTo("two");
        await Assert.That(await first.GetStringAsync("key")).IsEqualTo("one");

        await Assert.That(server.ReceivedCommands).Contains("GET a:key");
        await Assert.That(server.ReceivedCommands).Contains("GET b:key");
    }

    [Test]
    public async Task ClusterAskRedirect_ReturnsReadWithoutCachingUntrackedTarget()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray())
        {
            MinimumCommandsBeforeReply = 2,
        };
        var slot = ClusterHash.GetSlot("key");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            "*0\r\n"u8.ToArray(),
            FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"),
            FakeRespServer.OkReply,
            Encoding.ASCII.GetBytes($"-ASK {slot} 127.0.0.1:{target.Port}\r\n"));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo("value");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);

        await Assert.That(target.ReceivedCommands.Take(2)).IsEquivalentTo([
            "HELLO 3", "CLIENT TRACKING ON OPTIN",
        ]);
        await Assert.That(target.ReceivedCommands.Skip(2)).IsEquivalentTo([
            "ASKING", "GET key", "ASKING", "GET key",
        ]);
    }

    [Test]
    public async Task ClusterFlush_FencesCacheThroughCompletion()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        target.DelayReply(4, 250);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            AllowAdmin = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(1);

        var flush = client.Server.FlushDatabaseAsync().AsTask();
        await WaitUntilAsync(() => target.CommandsSeen >= 5);
        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        InsertCachedValue(client.Core.ClientCache!, "key", "value");

        await flush;

        await Assert.That(client.ClientSideCache.Count).IsEqualTo(0);
        await Assert.That(target.ReceivedCommands[^1]).IsEqualTo("FLUSHDB");
    }

    [Test]
    public async Task RawClusterWideCommand_FencesCacheThroughCompletion()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        target.DelayReply(4, 250);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        var flush = client.ExecuteAsync(RespireCommands.Scripting.SCRIPT_FLUSH).AsTask();
        await WaitUntilAsync(() => target.CommandsSeen >= 5);
        var cache = client.Core.ClientCache!;
        await Assert.That(cache.Count).IsEqualTo(0);
        InsertCachedValue(cache, "key", "value");

        using var result = await flush;

        await Assert.That(cache.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RawClusterWideFireAndForget_ClearsCache()
    {
        await using var target = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            FakeRespServer.OkReply,
            "$5\r\nvalue\r\n"u8.ToArray(),
            FakeRespServer.OkReply);
        var topology = Encoding.ASCII.GetBytes(
            $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{target.Port}\r\n");
        await using var seed = new FakeRespServer(
            HelloReply,
            FakeRespServer.OkReply,
            topology);
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Endpoints = { new RespireEndpoint("127.0.0.1", seed.Port) },
            ClientSideCache = new(),
        });

        await client.GetStringAsync("key");
        await client.ExecuteFireAndForgetAsync(RespireCommands.Scripting.SCRIPT_FLUSH);
        await WaitUntilAsync(() => target.CommandsSeen >= 5);

        await Assert.That(client.ClientSideCache!.Count).IsEqualTo(0);
        await Assert.That(target.ReceivedCommands[^1]).IsEqualTo("SCRIPT FLUSH");
    }

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = new(),
        });

    private static void InsertCachedValue(
        ClientSideCacheCoordinator cache,
        RespireKey key,
        string value)
    {
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString(Encoding.UTF8.GetBytes(value));
        cache.CompleteRead(in token, in response, allowInsert: true);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
