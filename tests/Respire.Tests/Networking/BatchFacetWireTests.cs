using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// The deferred facets on <see cref="RespireBatch"/> and <see cref="RespireTransaction"/> must
/// put exactly the same bytes on the wire as the client facets they mirror. Each case runs the
/// client call first, then the queued call, and compares the recorded frames.
/// </summary>
public class BatchFacetWireTests
{
    [Test]
    public async Task BatchFacets_EmitTheSameFramesAsClientFacets()
    {
        // Every command below replies with an integer, which satisfies both the integer and the
        // flag readers, so one scripted reply serves the whole script.
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Strings.SetManyAsync(
            RespireTtl.In(TimeSpan.FromSeconds(1)), SetWhen.NotExists, ("a", "1"), ("b", "2"));
        await client.Keys.DeleteAsync("k1", "k2", "k3");
        await client.Keys.ExpireAtAsync("k1", DateTimeOffset.FromUnixTimeMilliseconds(987654321));
        await client.Hashes.SetAsync("h", ("f1", "v1"), ("f2", "v2"));
        await client.Hashes.SetExpireAsync("h", TimeSpan.FromSeconds(2), SetWhen.NotExists, ("a", "one"));
        await client.Lists.RightPushAsync("l", "x", "y");
        await client.Lists.RemoveAsync("l", "x", count: -1);
        await client.Sets.IntersectStoreAsync("dest", "s1", "s2");
        await client.SortedSets.AddAsync("z", new SortedSetEntry("ada", 42), new SortedSetEntry("grace", 58));
        await client.SortedSets.CountByScoreAsync("z", 10, 50);
        await client.Bitmaps.CountAsync("bits", 0, 8, BitIndexUnit.Bit);
        await client.HyperLogLog.AddAsync("hll", "one", "two");
        await client.Geo.AddAsync("cities", entries: [new GeoEntry(-0.1276, 51.5072, "london")]);

        var expected = server.ReceivedCommands;

        var batch = client.CreateBatch();
        _ = batch.Strings.SetManyAsync(
            RespireTtl.In(TimeSpan.FromSeconds(1)), SetWhen.NotExists, ("a", "1"), ("b", "2"));
        _ = batch.Keys.DeleteAsync("k1", "k2", "k3");
        _ = batch.Keys.ExpireAtAsync("k1", DateTimeOffset.FromUnixTimeMilliseconds(987654321));
        _ = batch.Hashes.SetAsync("h", ("f1", "v1"), ("f2", "v2"));
        _ = batch.Hashes.SetExpireAsync("h", TimeSpan.FromSeconds(2), SetWhen.NotExists, ("a", "one"));
        _ = batch.Lists.RightPushAsync("l", "x", "y");
        _ = batch.Lists.RemoveAsync("l", "x", count: -1);
        _ = batch.Sets.IntersectStoreAsync("dest", "s1", "s2");
        _ = batch.SortedSets.AddAsync("z", new SortedSetEntry("ada", 42), new SortedSetEntry("grace", 58));
        _ = batch.SortedSets.CountByScoreAsync("z", 10, 50);
        _ = batch.Bitmaps.CountAsync("bits", 0, 8, BitIndexUnit.Bit);
        _ = batch.HyperLogLog.AddAsync("hll", "one", "two");
        _ = batch.Geo.AddAsync("cities", entries: [new GeoEntry(-0.1276, 51.5072, "london")]);
        await batch.SendAsync();

        var queued = server.ReceivedCommands.Skip(expected.Count).ToArray();
        await Assert.That(queued).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task TransactionFacets_QueueInsideMultiExecAndCompletePendings()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "+QUEUED\r\n"u8.ToArray(),
            "+QUEUED\r\n"u8.ToArray(),
            "*3\r\n:2\r\n:1\r\n*2\r\n$1\r\na\r\n$1\r\nb\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var transaction = client.CreateTransaction();
        var pushed = transaction.Lists.RightPushAsync("audit", "a", "b");
        var stored = transaction.Hashes.SetAsync("user", "name", "Ada");
        var range = transaction.Lists.RangeAsync("audit");

        var committed = await transaction.CommitAsync();

        await Assert.That(committed).IsTrue();
        await Assert.That(pushed.Result).IsEqualTo(2);
        await Assert.That(stored.Result).IsTrue();
        await Assert.That(range.Result).IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);

        var commands = server.ReceivedCommands;
        await Assert.That(commands[0]).IsEqualTo("MULTI");
        await Assert.That(commands[1]).IsEqualTo("RPUSH audit a b");
        await Assert.That(commands[2]).IsEqualTo("HSET user name Ada");
        await Assert.That(commands[3]).IsEqualTo("LRANGE audit 0 -1");
        await Assert.That(commands[4]).IsEqualTo("EXEC");
    }

    [Test]
    public async Task RootDeleteAsync_TakesManyKeysLikeTheClientRoot()
    {
        await using var server = new FakeRespServer(":3\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var deleted = batch.DeleteAsync("one", "two", "three");
        await batch.SendAsync();

        await Assert.That(deleted.Result).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DEL one two three");
    }

    [Test]
    public async Task QueuedFacetResult_BeforeSend_ThrowsInsteadOfDeadlocking()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var pending = batch.Hashes.GetStringAsync("user", "name");

        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Pending);
        await Assert.That(pending.IsCompleted).IsFalse();
        await Assert.That(pending.HasResult).IsFalse();
        await Assert.That(pending.Error).IsNull();
        await Assert.That(pending.TryGetResult(out _)).IsFalse();
        await Assert.That(() => _ = pending.Result).ThrowsExactly<RespirePendingNotReadyException>();
    }

    [Test]
    public async Task DisposingUnsentBatch_FaultsEveryPending()
    {
        await using var client = RespireClient.Create("localhost:6379");
        var batch = client.CreateBatch();
        var first = batch.GetStringAsync("first");
        var second = batch.ExistsAsync("second");

        await Assert.That(batch.IsSent).IsFalse();

        batch.Dispose();
        batch.Dispose();

        await Assert.That(batch.IsSent).IsFalse();
        await Assert.That(first.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(second.Status).IsEqualTo(RespirePendingStatus.Faulted);
        await Assert.That(first.IsCompleted).IsTrue();
        await Assert.That(second.IsCompleted).IsTrue();
        await Assert.That(first.HasResult).IsFalse();
        await Assert.That(second.HasResult).IsFalse();
        await Assert.That(first.Error).IsTypeOf<RespireBatchDiscardedException>();
        await Assert.That(second.Error).IsTypeOf<RespireBatchDiscardedException>();
        await Assert.That(first.TryGetResult(out _)).IsFalse();
        await Assert.That(second.TryGetResult(out _)).IsFalse();
        await Assert.That(() => _ = first.Result).ThrowsExactly<RespireBatchDiscardedException>();
        await Assert.That(() => _ = second.Result).ThrowsExactly<RespireBatchDiscardedException>();
        await Assert.That(() => batch.GetStringAsync("third")).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(async () => await batch.SendAsync()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task SendingBatch_SetsIsSentAndDisposePreservesResult()
    {
        await using var server = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.GetStringAsync("key");

        await Assert.That(batch.IsSent).IsFalse();

        await batch.SendAsync();
        batch.Dispose();

        await Assert.That(batch.IsSent).IsTrue();
        await Assert.That(pending.Result).IsEqualTo("value");
        await Assert.That(pending.Status).IsEqualTo(RespirePendingStatus.Succeeded);
        await Assert.That(pending.IsCompleted).IsTrue();
        await Assert.That(pending.HasResult).IsTrue();
        await Assert.That(pending.Error).IsNull();
        await Assert.That(pending.TryGetResult(out var value)).IsTrue();
        await Assert.That(value).IsEqualTo("value");
    }
}
