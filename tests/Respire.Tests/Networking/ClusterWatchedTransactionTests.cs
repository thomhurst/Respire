using System.Text;
using Respire.Internal;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClusterWatchedTransactionTests
{
    private static readonly byte[] Queued = "+QUEUED\r\n"u8.ToArray();
    private static readonly byte[] Committed = "*1\r\n+OK\r\n"u8.ToArray();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchInvalidatesCachedValuesAndFencesEarlierReads(bool cluster)
    {
        byte[] hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();
        await using var owner = new FakeRespServer(2, hello, FakeRespServer.OkReply,
            FakeRespServer.OkReply, "$3\r\nold\r\n"u8.ToArray(),
            FakeRespServer.OkReply, "$5\r\nfresh\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(hello, FakeRespServer.OkReply, Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            UseCluster = cluster, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", cluster ? seed.Port : owner.Port)],
        });
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(await view.GetStringAsync("{a}:watched")).IsEqualTo("old");
        await Assert.That(await view.GetStringAsync("{a}:watched")).IsEqualTo("old");
        var cache = client.Core.ClientCache!;
        var other = cache.BeginRead("tenant:{a}:other");
        using var untouched = RespValue.BulkString("untouched"u8.ToArray());
        cache.CompleteRead(other, untouched, allowInsert: true);
        var earlierRead = cache.BeginRead("tenant:{a}:watched");

        // No tracking invalidation is sent: a pre-WATCH write's push can still be delayed.
        await using var transaction = await view.CreateTransactionAsync(["{a}:watched"]);
        await Assert.That(cache.Count).IsEqualTo(1);
        using var lateReply = RespValue.BulkString("old"u8.ToArray());
        cache.CompleteRead(earlierRead, lateReply, allowInsert: true);
        await Assert.That(cache.Count).IsEqualTo(1);
        await Assert.That(await view.GetStringAsync("{a}:watched")).IsEqualTo("fresh");
        await Assert.That(await view.GetStringAsync("{a}:other")).IsEqualTo("untouched");
        await Assert.That(owner.ReceivedCommands.Count(command => command == "GET tenant:{a}:watched"))
            .IsEqualTo(2);
        await Assert.That(owner.ReceivedCommands).Contains("WATCH tenant:{a}:watched");
    }

    [Test]
    public async Task CrossSlotWatchFailsBeforeDiscovery()
    {
        await using var seed = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = RespireClient.Create(Options(seed.Port));
        var view = client.WithKeyPrefix("tenant:");
        await Assert.That(async () => await view.CreateTransactionAsync(["{a}:one", "{b}:two"]))
            .ThrowsExactly<InvalidOperationException>();
        await Assert.That(seed.ReceivedCommands).IsEmpty();
    }

    [Test]
    public async Task SlotIsPinnedByWatchAndCompletedLeasesReturnToTheirNodePool()
    {
        await using var owner = new FakeRespServer(2,
            FakeRespServer.OkReply, FakeRespServer.OkReply, Queued, Committed,
            FakeRespServer.OkReply, FakeRespServer.OkReply, Queued, "*-1\r\n"u8.ToArray(),
            FakeRespServer.OkReply, FakeRespServer.OkReply, Queued, Committed);
        await using var seed = new FakeRespServer(Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        var view = client.WithKeyPrefix("tenant:");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var transaction = await view.CreateTransactionAsync(["{a}:watched", "{a}:other"]);
            await Assert.That(() => transaction.Set("{b}:wrong", "value")).ThrowsExactly<InvalidOperationException>();
            await Assert.That(() => transaction.Keys.Rename("{a}:watched", "{b}:wrong"))
                .ThrowsExactly<InvalidOperationException>();
            await Assert.That(transaction.Count).IsEqualTo(0);
            var pending = transaction.Set("{a}:watched", "value");
            await Assert.That(await transaction.CommitAsync()).IsEqualTo(attempt != 1);
            await Assert.That(pending.Status).IsEqualTo(attempt == 1 ? RespirePendingStatus.Aborted : RespirePendingStatus.Succeeded);
        }
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(Enumerable.Range(0, 3).SelectMany(_ => new[]
        {
            "WATCH tenant:{a}:watched tenant:{a}:other", "MULTI", "SET tenant:{a}:watched value", "EXEC",
        }), CollectionOrdering.Matching);
        await Assert.That(owner.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task BinaryWatchKeysAreOwnedBeforeDiscoveryAwaits()
    {
        await using var owner = new FakeRespServer(2, FakeRespServer.OkReply);
        var discovering = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var seed = new FakeRespServer(Topology(owner.Port))
        {
            SuppressReply = _ => { discovering.TrySetResult(); return true; },
        };
        await using var client = RespireClient.Create(Options(seed.Port));
        byte[] bytes = "{a}:watched"u8.ToArray();
        RespireKey[] keys = [bytes];
        var creating = client.CreateTransactionAsync(keys).AsTask();
        await discovering.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Array.Fill(bytes, (byte)'x');
        keys[0] = "{b}:changed";
        await seed.SendRawAsync(Topology(owner.Port));
        await using var transaction = await creating.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(() => transaction.Set("{b}:wrong", "value")).ThrowsExactly<InvalidOperationException>();
        await Assert.That(owner.ReceivedCommands).IsEquivalentTo(["WATCH {a}:watched"]);
    }

    [Test]
    [Arguments("MOVED", false)]
    [Arguments("ASK", false)]
    [Arguments("READONLY", false)]
    [Arguments("MOVED", true)]
    [Arguments("ASK", true)]
    [Arguments("READONLY", true)]
    public async Task RejectionsRequireANewWatchAttemptWithoutReplay(string code, bool duringCommit)
    {
        await using var replacement = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply, Queued, Committed);
        var slot = ClusterHash.GetSlot("{a}:watched");
        var rejection = Encoding.ASCII.GetBytes(code == "READONLY" ? "-READONLY replica\r\n"
            : $"-{code} {slot} 127.0.0.1:{replacement.Port}\r\n");
        byte[][] replies = duringCommit
            ? [FakeRespServer.OkReply, FakeRespServer.OkReply, rejection, "-EXECABORT discarded\r\n"u8.ToArray()]
            : [rejection];
        await using var owner = new FakeRespServer(2, replies);
        await using var seed = new FakeRespServer(Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        RespirePending<bool>? pending = null;
        var error = await Assert.That(async () =>
        {
            await using var transaction = await client.CreateTransactionAsync(["{a}:watched"]);
            pending = transaction.Set("{a}:watched", "value");
            await transaction.CommitAsync();
        }).ThrowsExactly<RespireTransactionRetryException>();
        await Assert.That(error!.ServerError.Code).IsEqualTo(code);
        await Assert.That(error.InnerException).IsSameReferenceAs(error.ServerError);
        if (pending is not null) await Assert.That(pending.Error).IsSameReferenceAs(error);
        await Assert.That(replacement.ReceivedCommands).IsEmpty();
        await Assert.That(owner.ReceivedCommands.Count(command => command == "WATCH {a}:watched")).IsEqualTo(1);
        if (code == "MOVED")
        {
            // The route is learned, but a new attempt must establish its own WATCH before writing.
            await using var fresh = await client.CreateTransactionAsync(["{a}:watched"]);
            _ = fresh.Set("{a}:watched", "fresh");
            await Assert.That(await fresh.CommitAsync()).IsTrue();
            await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(
                ["WATCH {a}:watched", "MULTI", "SET {a}:watched fresh", "EXEC"], CollectionOrdering.Matching);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadOnlyFromPreviousOwnerPreservesTheNewRoute(bool duringCommit)
    {
        await using var replacement = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply, Queued, Committed);
        byte[] readOnly = "-READONLY replica\r\n"u8.ToArray();
        byte[][] replies = duringCommit
            ? [FakeRespServer.OkReply, FakeRespServer.OkReply, readOnly, "-EXECABORT discarded\r\n"u8.ToArray()]
            : [readOnly];
        await using var owner = new FakeRespServer(2, replies);
        await using var seed = new FakeRespServer(Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        owner.SuppressReply = command =>
        {
            if (command == (duringCommit ? "SET {a}:watched old" : "WATCH {a}:watched"))
            {
                var router = client.Core.Cluster!;
                router.SetSlotOwner(ClusterHash.GetSlot("{a}:watched"),
                    router.GetMultiplexer(new RespireEndpoint("127.0.0.1", replacement.Port)));
            }
            return false;
        };
        var error = await Assert.That(async () =>
        {
            await using var transaction = await client.CreateTransactionAsync(["{a}:watched"]);
            _ = transaction.Set("{a}:watched", "old");
            await transaction.CommitAsync();
        }).ThrowsExactly<RespireTransactionRetryException>();
        await Assert.That(error!.ServerError.Code).IsEqualTo("READONLY");
        await Assert.That(replacement.ReceivedCommands).IsEmpty();
        await using var fresh = await client.CreateTransactionAsync(["{a}:watched"]);
        _ = fresh.Set("{a}:watched", "fresh");
        await Assert.That(await fresh.CommitAsync()).IsTrue();
        await Assert.That(replacement.ReceivedCommands).IsEquivalentTo(
            ["WATCH {a}:watched", "MULTI", "SET {a}:watched fresh", "EXEC"], CollectionOrdering.Matching);
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
    }

    [Test]
    public async Task ExecutedElementErrorsAreNotRetried()
    {
        await using var owner = new FakeRespServer(2, FakeRespServer.OkReply, FakeRespServer.OkReply,
            Queued, Queued, "*2\r\n+OK\r\n-MOVED 15495 127.0.0.1:1\r\n"u8.ToArray());
        await using var seed = new FakeRespServer(Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        await using var transaction = await client.CreateTransactionAsync(["{a}:watched"]);
        var success = transaction.Set("{a}:watched", "value");
        var failure = transaction.Set("{a}:other", "value");
        await Assert.That(await transaction.CommitAsync()).IsTrue();
        await Assert.That(success.Result).IsTrue();
        await Assert.That(failure.Error).IsTypeOf<RespireServerException>();
        await Assert.That(owner.ReceivedCommands.Count(command => command == "EXEC")).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task UncommittedOrCancelledWatchLeaseIsDiscarded(bool cancelWatch)
    {
        var watching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var owner = new FakeRespServer(3, FakeRespServer.OkReply)
        {
            SuppressReply = command =>
            {
                if (command != "WATCH {a}:watched") return false;
                watching.TrySetResult();
                return cancelWatch;
            },
        };
        await using var seed = new FakeRespServer(Topology(owner.Port));
        await using var client = await RespireClient.ConnectAsync(Options(seed.Port));
        using var cancellation = new CancellationTokenSource();
        var creating = client.CreateTransactionAsync(["{a}:watched"], cancellation.Token).AsTask();
        await watching.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancelWatch)
        {
            cancellation.Cancel();
            await Assert.That(async () => await creating).Throws<OperationCanceledException>();
        }
        else await (await creating).DisposeAsync();
        owner.SuppressReply = null;
        await using var fresh = await client.CreateTransactionAsync(["{a}:watched"]);
        await Assert.That(owner.ReceivedCommands.Count(command => command == "WATCH {a}:watched")).IsEqualTo(2);
        await Assert.That(owner.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(2);
    }

    private static RespireOptions Options(int seedPort) => new()
    {
        UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seedPort)],
    };

    private static byte[] Topology(int port) => Encoding.ASCII.GetBytes(
        $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{port}\r\n");
}
