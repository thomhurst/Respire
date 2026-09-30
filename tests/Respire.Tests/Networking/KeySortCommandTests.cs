using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class KeySortCommandTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredSort_SnapshotsBinaryPatternsAndOwnsResults(bool transactional)
    {
        byte[] response = [.. "*3\r\n$2\r\n"u8, 0xff, 0, .. "\r\n$-1\r\n$1\r\nx\r\n"u8];
        byte[][] replies = transactional
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(response).ToArray()]
            : [response];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        byte[] pattern = "object:*->name"u8.ToArray();
        RespireKey[] patterns = [pattern, "#"];
        var pending = queue.Keys.Sort<byte[]>("items", new RespireSortOptions
        {
            ReadOnly = true, By = "weight:*", Get = patterns, Descending = true, Alpha = true, Limit = new(1, 3),
        });
        Array.Fill(pattern, (byte)'x');
        patterns[1] = "changed";
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(pending.Result[0]).IsEquivalentTo(new byte[] { 0xff, 0 }, CollectionOrdering.Matching);
        await Assert.That(pending.Result[1]).IsNull();
        await Assert.That(pending.Result[2]).IsEquivalentTo(new byte[] { (byte)'x' });
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SORT_RO tenant:items BY tenant:weight:* LIMIT 1 3 GET tenant:object:*->name GET # DESC ALPHA"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RandomReply_OwnsBinaryKeyAndNull(bool resp3)
    {
        byte[] response = [.. "$2\r\n"u8, 0xff, 0, .. "\r\n"u8];
        await using var server = new FakeRespServer(response, resp3 ? "_\r\n"u8.ToArray() : "$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var result = await client.Keys.RandomAsync();
        await Assert.That(await client.Keys.RandomAsync()).IsNull();
        await Assert.That(result).IsEqualTo((RespireKey?)new RespireKey(new byte[] { 0xff, 0 }));
        byte[] borrowed = [0xfe, 1];
        using var reply = RespValue.BulkString(borrowed);
        var owned = KeyCommands.ParseRandom(in reply);
        Array.Clear(borrowed);
        await Assert.That(owned).IsEqualTo((RespireKey?)new RespireKey(new byte[] { 0xfe, 1 }));
    }

    [Test]
    public async Task InvalidOptionsAndViewScopes_FailBeforeSendingOrEnqueueing()
    {
        await using var server = new FakeRespServer();
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        Func<Task>[] invalid =
        [
            async () => { await client.Keys.SortAsync("key", new RespireSortOptions { Limit = new(-1, 1) }); },
            async () => { await client.Keys.SortAsync("key", new RespireSortOptions { Limit = new(0, -1) }); },
            async () => { await client.Keys.SortStoreAsync("key", "target", new RespireSortOptions { ReadOnly = true }); },
            async () => { await client.Keys.MoveAsync("key", -1); },
        ];
        foreach (var call in invalid) await Assert.That(call).Throws<ArgumentException>();
        await Assert.That(async () => await view.Keys.RandomAsync()).Throws<NotSupportedException>();
        foreach (var prefix in new[] { "tenant:*:", "tenant->field:", "tenant\0:" })
        {
            var unsafeView = client.WithKeyPrefix(prefix);
            await Assert.That(async () => await unsafeView.Keys.SortAsync("key", new RespireSortOptions { By = "weight:*" }))
                .Throws<NotSupportedException>();
        }
        using var batch = view.CreateBatch();
        await using var transaction = view.CreateTransaction();
        foreach (var commands in new[] { batch.Keys, transaction.Keys })
        {
            await Assert.That(() => commands.Random()).Throws<NotSupportedException>();
            await Assert.That(() => commands.Move("key", -1)).Throws<ArgumentOutOfRangeException>();
            await Assert.That(() => commands.SortStore("key", "target", new RespireSortOptions { ReadOnly = true })).Throws<ArgumentException>();
            await Assert.That(() => commands.Sort("key", new RespireSortOptions { Limit = new(-1, 1) })).Throws<ArgumentException>();
        }
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments("sort")]
    [Arguments("sort-ro")]
    [Arguments("store")]
    [Arguments("random")]
    [Arguments("move")]
    public async Task Cancellation_ReachesPendingCommands(string operation)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer
        {
            SuppressReply = _ => { received.TrySetResult(); return true; },
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var cancellation = new CancellationTokenSource();
        Task pending = operation switch
        {
            "sort" => client.Keys.SortAsync("key", cancellationToken: cancellation.Token).AsTask(),
            "sort-ro" => client.Keys.SortAsync<byte[]>("key", new RespireSortOptions { ReadOnly = true }, cancellation.Token).AsTask(),
            "store" => client.Keys.SortStoreAsync("key", "target", cancellationToken: cancellation.Token).AsTask(),
            "random" => client.Keys.RandomAsync(cancellation.Token).AsTask(),
            _ => client.Keys.MoveAsync("key", 1, cancellation.Token).AsTask(),
        };
        await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.That(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5))).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments("immediate")]
    [Arguments("batch")]
    [Arguments("transaction")]
    public async Task Cluster_RoutesSourceAndValidatesPatternAndStoreSlots(string path)
    {
        byte[] response = ":1\r\n"u8.ToArray();
        byte[][] replies = path == "transaction"
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n"u8.ToArray().Concat(response).ToArray()]
            : [response];
        await using var owner = new FakeRespServer(replies);
        var slot = ClusterHash.GetSlot("tenant:{sort}:source");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        var options = new RespireSortOptions { By = "{sort}:weight:*", Get = new RespireKey[] { "{sort}:object:*->field", "#" } };
        if (path == "immediate") await view.Keys.SortStoreAsync("{sort}:source", "{sort}:target", options);
        else if (path == "batch")
        {
            using var batch = view.CreateBatch();
            var pending = batch.Keys.SortStore("{sort}:source", "{sort}:target", options);
            await batch.ExecuteAsync();
            await Assert.That(pending.Result).IsEqualTo(1);
        }
        else
        {
            await using var transaction = view.CreateTransaction();
            var pending = transaction.Keys.SortStore("{sort}:source", "{sort}:target", options);
            await transaction.CommitAsync();
            await Assert.That(pending.Result).IsEqualTo(1);
        }
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SORT tenant:{sort}:source BY tenant:{sort}:weight:* GET tenant:{sort}:object:*->field GET # ASC STORE tenant:{sort}:target"]);
        using var rejectedBatch = view.CreateBatch();
        await using var rejectedTransaction = view.CreateTransaction();
        foreach (var queue in new[] { rejectedBatch.Keys, rejectedTransaction.Keys })
        {
            await Assert.That(() => queue.SortStore("{a}:key", "{b}:key")).Throws<RespireServerException>();
            await Assert.That(() => queue.Sort("{a}:key", options)).Throws<RespireServerException>();
            await Assert.That(() => queue.Sort("{a}:key", new RespireSortOptions { By = "*{a}" })).Throws<NotSupportedException>();
            await Assert.That(() => queue.Move("key", 1)).Throws<NotSupportedException>();
            await Assert.That(() => queue.Random()).Throws<NotSupportedException>();
        }
        await Assert.That(async () => await view.Keys.SortStoreAsync("{a}:key", "{b}:key")).Throws<RespireServerException>();
        await Assert.That(async () => await view.Keys.SortAsync("{a}:key", options)).Throws<RespireServerException>();
        await Assert.That(async () => await view.Keys.SortAsync("{a}:key", new RespireSortOptions { By = "weight:*" })).Throws<NotSupportedException>();
        await Assert.That(async () => await view.Keys.MoveAsync("key", 1)).Throws<NotSupportedException>();
        await Assert.That(async () => await client.Keys.RandomAsync()).Throws<NotSupportedException>();
    }
}
