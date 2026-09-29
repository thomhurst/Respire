using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterReadOnlyTests
{
    private static readonly byte[] ReadOnlyReply = "-READONLY You can't write against a read only replica.\r\n"u8.ToArray();

    [Test]
    [Arguments("facet")]
    [Arguments("raw")]
    [Arguments("catalog")]
    [Arguments("fire-and-forget")]
    [Arguments("batch")]
    public async Task Write_RefreshesOwnerAndCachesReplacement(string path)
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);

        switch (path)
        {
            case "facet":
                await client.SetAsync("key", "first");
                break;
            case "raw":
                using (await client.ExecuteAsync("SET", "key", "first")) { }
                break;
            case "catalog":
                using (await client.ExecuteAsync(RespireCommands.String.SET, "key", "first")) { }
                break;
            case "fire-and-forget":
                await client.ExecuteFireAndForgetAsync(RespireCommands.String.SET, "key", "first");
                break;
            case "batch":
                using (var batch = client.CreateBatch())
                {
                    var pending = batch.Set("key", "first");
                    await batch.ExecuteAsync();
                    await Assert.That(pending.Result).IsTrue();
                }
                break;
        }

        await client.SetAsync("key", "second");

        await Assert.That(replica.ReceivedCommands).IsEquivalentTo(["SET key first"]);
        await Assert.That(replacement.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"], CollectionOrdering.Matching);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS", "CLUSTER SLOTS"]);
    }

    [Test]
    public async Task StaleTopology_PreservesReadOnlyWithoutResendingWrite()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);

        var error = await Assert.That(async () => await client.SetAsync("key", "value").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
        await Assert.That(error.CommandName).IsEqualTo("SET");
        await Assert.That(replica.ReceivedCommands.Count(command => command.StartsWith("SET "))).IsEqualTo(1);
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task RefreshTimeout_PreservesReadOnly()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port, TimeSpan.FromMilliseconds(200));
        seed.SuppressReply = _ => true;

        var error = await Assert.That(async () => await client.SetAsync("key", "value").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5))).Throws<RespireServerException>();

        await Assert.That(error!.Code).IsEqualTo(RespireErrorCodes.ReadOnly);
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
    }

    [Test]
    public async Task BlockingCommand_RefreshesDedicatedPool()
    {
        await using var replacement = new FakeRespServer(2,
            "*2\r\n$3\r\nkey\r\n$5\r\nvalue\r\n"u8.ToArray());
        await using var replica = new FakeRespServer(2, ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(await client.Lists.LeftPopAsync("key", waitFor: TimeSpan.FromSeconds(1)))
            .IsEqualTo("value");
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["BLPOP key 1"]);
    }

    [Test]
    public async Task Transaction_RefreshesAfterQueueErrorAbortsExec()
    {
        await using var replacement = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "*1\r\n+OK\r\n"u8.ToArray());
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply, ReadOnlyReply, "-EXECABORT discarded\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        await using var transaction = client.CreateTransaction();
        var pending = transaction.Set("key", "value");

        await transaction.CommitAsync();

        await Assert.That(pending.Result).IsTrue();
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(["MULTI", "SET key value", "EXEC"]);
    }

    [Test]
    public async Task Transaction_DoesNotReplayErrorsInsideExecutedResults()
    {
        await using var replica = new FakeRespServer(
            FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
            [.. "*2\r\n+OK\r\n"u8, .. ReadOnlyReply]);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);
        await using var transaction = client.CreateTransaction();
        var succeeded = transaction.Set("key", "first");
        var pending = transaction.Set("key", "second");

        await transaction.CommitAsync();

        await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        await Assert.That(succeeded.Result).IsTrue();
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
        await Assert.That(replica.CommandsSeen).IsEqualTo(4);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ChangingReadOnlyOwners_RespectsRetryLimit(bool batched)
    {
        await using var first = new FakeRespServer(ReadOnlyReply);
        await using var second = new FakeRespServer(ReadOnlyReply);
        var topologies = Enumerable.Range(0, ClusterRouter.RedirectLimit + 1)
            .Select(index => Topology(index % 2 == 0 ? first.Port : second.Port)).ToArray();
        await using var seed = new FakeRespServer(topologies);
        await using var client = await ConnectAsync(seed.Port);

        if (batched)
        {
            using var batch = client.CreateBatch();
            var pending = batch.Set("key", "value");
            await batch.ExecuteAsync();
            await Assert.That(pending.Error).IsTypeOf<RespireServerException>();
        }
        else
        {
            await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireServerException>();
        }

        await Assert.That(first.CommandsSeen + second.CommandsSeen).IsEqualTo(ClusterRouter.RedirectLimit + 1);
        await Assert.That(seed.CommandsSeen).IsEqualTo(ClusterRouter.RedirectLimit + 1);
    }

    [Test]
    public async Task Batch_UsesAlreadyRefreshedOwnerForLaterQueuedErrors()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        using var batch = client.CreateBatch();
        var first = batch.Set("key", "first");
        var second = batch.Set("key", "second");

        await batch.ExecuteAsync();

        await Assert.That(first.Result).IsTrue();
        await Assert.That(second.Result).IsTrue();
        await Assert.That(seed.CommandsSeen).IsEqualTo(2);
        await Assert.That(replacement.ReceivedCommands)
            .IsEquivalentTo(["SET key first", "SET key second"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Refresh_UsesOtherDiscoveredPrimaryBeforeSeed()
    {
        await using var replacement = new FakeRespServer(FakeRespServer.OkReply);
        await using var healthy = new FakeRespServer(Topology(replacement.Port));
        await using var replica = new FakeRespServer(ReadOnlyReply);
        var initial = Encoding.ASCII.GetBytes(
            $"*2\r\n*3\r\n:0\r\n:0\r\n*2\r\n$9\r\n127.0.0.1\r\n:{healthy.Port}\r\n" +
            $"*3\r\n:1\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{replica.Port}\r\n");
        await using var seed = new FakeRespServer(initial, "-NOPERM discovery denied\r\n"u8.ToArray());
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(await client.SetAsync("key", "value")).IsTrue();
        await Assert.That(healthy.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task Refresh_PropagatesCallerCancellation()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        seed.SuppressReply = _ => { refreshing.TrySetResult(); return true; };
        using var cancellation = new CancellationTokenSource();

        var pending = client.SetAsync("key", "value", cancellationToken: cancellation.Token).AsTask();
        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.That(async () => await pending).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task NoRedirect_PreservesReadOnlyWithoutRefresh()
    {
        await using var replica = new FakeRespServer(ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port));
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(async () =>
        {
            using var result = await client.ExecuteAsync(
                RespireCommands.String.SET, ["key", "value"], RespireCommandFlags.NoRedirect);
        }).Throws<RespireServerException>();
        await Assert.That(seed.CommandsSeen).IsEqualTo(1);
    }

    [Test]
    public async Task TrackedScript_RefreshesAndRetainsReplacementIdentity()
    {
        await using var replacement = new FakeRespServer(":42\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), FakeRespServer.OkReply);
        await using var replica = new FakeRespServer(":41\r\n"u8.ToArray(), ":0\r\n"u8.ToArray(), ReadOnlyReply);
        await using var seed = new FakeRespServer(Topology(replica.Port), Topology(replacement.Port));
        await using var client = await ConnectAsync(seed.Port);
        var script = RespireScript.Create("return redis.call('SET', KEYS[1], ARGV[1])");

        var execution = await client.StartTrackedScriptExecutionAsync(
            script, ["key"], ["value"], CancellationToken.None, requireReliableCorrectionOrdering: true);
        using var result = await execution.Response;

        await Assert.That(result.AsString()).IsEqualTo("OK");
        await Assert.That(execution.ConnectionIdentity.Endpoint.Port).IsEqualTo(replacement.Port);
        await Assert.That(execution.ConnectionIdentity.ServerClientId).IsEqualTo(42);
    }

    [Test]
    [Arguments("READONLY", false)]
    [Arguments("ERR", true)]
    public async Task UnrecoverableErrors_DoNotRefresh(string code, bool keyed)
    {
        var reply = Encoding.ASCII.GetBytes($"-{code} command failed\r\n");
        await using var replica = new FakeRespServer(reply);
        await using var seed = new FakeRespServer(Topology(replica.Port), reply);
        await using var client = await ConnectAsync(seed.Port);

        await Assert.That(async () =>
        {
            if (keyed)
            {
                await client.SetAsync("key", "value");
            }
            else
            {
                await client.PingAsync();
            }
        }).Throws<RespireServerException>();
        await Assert.That(seed.CommandsSeen).IsEqualTo(keyed ? 1 : 2);
    }

    [Test]
    public async Task Standalone_PreservesReadOnlyWithoutTopologyCommands()
    {
        await using var server = new FakeRespServer(ReadOnlyReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.SetAsync("key", "value")).Throws<RespireServerException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(["SET key value"]);
    }

    private static ValueTask<RespireClient> ConnectAsync(int port, TimeSpan? connectTimeout = null)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = true,
            Connections = 1,
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(1),
            CommandTimeout = null,
            Endpoints = { new RespireEndpoint("127.0.0.1", port) },
        });

    private static byte[] Topology(int port)
    {
        var slot = ClusterHash.GetSlot("key");
        return Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
    }
}
