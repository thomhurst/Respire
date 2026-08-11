using System.Net;
using System.Net.Sockets;
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
    public async Task ConditionalHashSetAndStringSetRange_MatchDeferredFacets()
    {
        await using var server = new FakeRespServer(
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":5\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray(), ":5\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Hashes.SetAsync("hash", "new", "one", SetWhen.NotExists);
        await client.Hashes.SetAsync("hash", "existing", (RespireValue)"two", SetWhen.Exists);
        await client.Strings.SetRangeAsync("text", 2, "xyz");

        var batch = client.CreateBatch();
        var created = batch.Hashes.Set("hash", "new", "one", SetWhen.NotExists);
        var updated = batch.Hashes.Set("hash", "existing", "two", SetWhen.Exists);
        var length = batch.Strings.SetRange("text", 2, "xyz");
        await batch.ExecuteAsync();

        await Assert.That(created.Result).IsTrue();
        await Assert.That(updated.Result).IsTrue();
        await Assert.That(length.Result).IsEqualTo(5);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "HSETNX hash new one",
            "HSETEX hash FXX FIELDS 1 existing two",
            "SETRANGE text 2 xyz",
            "HSETNX hash new one",
            "HSETEX hash FXX FIELDS 1 existing two",
            "SETRANGE text 2 xyz",
        ], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ExecuteAsync_ReturnsPerCommandFailureSummary()
    {
        await using var server = new FakeRespServer(
            "-WRONGTYPE bad value\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var failed = batch.GetString("wrong-type");
        var succeeded = batch.Exists("present");

        var result = await batch.ExecuteAsync();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.FailureCount).IsEqualTo(1);
        await Assert.That(result.FirstError).IsSameReferenceAs(failed.Error);
        await Assert.That(result.FirstError).IsTypeOf<RespireServerException>();
        await Assert.That(result.Failures.Count).IsEqualTo(1);
        await Assert.That(result.Failures[0].Index).IsEqualTo(0);
        await Assert.That(result.Failures[0].Operation).IsEqualTo("GET");
        await Assert.That(result.Failures[0].Error).IsSameReferenceAs(failed.Error);
        await Assert.That(succeeded.Result).IsTrue();
        await Assert.That(() => result.ThrowIfAnyFailed()).ThrowsExactly<RespireServerException>();
    }

    [Test]
    public async Task ExecuteAsync_CommandTimeout_CarriesOperationName()
    {
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        server.SuppressReply = static command => command == "GET key";
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(100)
        });

        var batch = client.CreateBatch();
        var timedOut = batch.GetString("key");

        var result = await batch.ExecuteAsync();

        await Assert.That(result.FailureCount).IsEqualTo(1);
        var error = await Assert.That(timedOut.Error).IsTypeOf<RespireTimeoutException>();
        await Assert.That(error!.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task ExecuteAsync_EmptyBatchReturnsEmptySummary()
    {
        await using var client = RespireClient.Create("localhost:6379");

        var result = await client.CreateBatch().ExecuteAsync();

        await Assert.That(result.Count).IsEqualTo(0);
        await Assert.That(result.FailureCount).IsEqualTo(0);
        await Assert.That(result.FirstError).IsNull();
        await Assert.That(result.Failures).IsEmpty();
        await Assert.That(ReferenceEquals(result.Failures, default(RespireBatchResult).Failures)).IsTrue();
        result.ThrowIfAnyFailed();
    }

    [Test]
    public async Task ExecuteAsync_ConnectionFailureReturnsSummaryAndFaultsPendings()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var unavailablePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new RespireEndpoint("127.0.0.1", unavailablePort) },
            ConnectTimeout = TimeSpan.FromMilliseconds(100),
        });
        var batch = client.CreateBatch();
        var first = batch.GetString("first");
        var second = batch.Exists("second");

        var result = await batch.ExecuteAsync();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.FailureCount).IsEqualTo(2);
        await Assert.That(result.FirstError).IsNotNull();
        await Assert.That(first.Error).IsSameReferenceAs(result.FirstError);
        await Assert.That(second.Error).IsSameReferenceAs(result.FirstError);
        await Assert.That(result.Failures.Count).IsEqualTo(2);
        await Assert.That(result.Failures[0].Index).IsEqualTo(0);
        await Assert.That(result.Failures[0].Operation).IsEqualTo("GET");
        await Assert.That(result.Failures[1].Index).IsEqualTo(1);
        await Assert.That(result.Failures[1].Operation).IsEqualTo("EXISTS");
        await Assert.That(result.Failures.All(failure => ReferenceEquals(failure.Error, result.FirstError)))
            .IsTrue();
    }

    [Test]
    public async Task BatchCommandError_PreservesCommandName()
    {
        await using var server = new FakeRespServer("-WRONGTYPE bad value\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var pending = batch.GetString("key");

        await batch.ExecuteAsync();
        var error = Assert.Throws<RespireServerException>(() => _ = pending.Result);
        await Assert.That(error.CommandName).IsEqualTo("GET");
    }

    [Test]
    public async Task BatchFacets_EmitTheSameFramesAsClientFacets()
    {
        // Every command below replies with an integer, which satisfies both the integer and the
        // flag readers, so one scripted reply serves the whole script.
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await client.Strings.SetManyExpireAsync(
            RespireExpiry.In(TimeSpan.FromSeconds(1)), SetWhen.NotExists, ("a", "1"), ("b", "2"));
        await client.Keys.DeleteAsync("k1", "k2", "k3");
        await client.Keys.ExpireAsync(
            "k1", RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(987654321)));
        await client.Hashes.SetAsync("h", ("f1", "v1"), ("f2", "v2"));
        await client.Hashes.SetExpireAsync(
            "h", RespireExpiry.In(TimeSpan.FromSeconds(2)), SetWhen.NotExists, ("a", "one"));
        await client.Hashes.CountAsync("h");
        await client.Lists.RightPushAsync("l", "x", "y");
        await client.Lists.RemoveAsync("l", "x", count: -1);
        await client.Lists.CountAsync("l");
        await client.Sets.IntersectStoreAsync("dest", "s1", "s2");
        await client.SortedSets.AddAsync("z", new SortedSetEntry("ada", 42), new SortedSetEntry("grace", 58));
        await client.SortedSets.CountByScoreAsync("z", 10, 50);
        await client.Bitmaps.CountAsync("bits", 0, 8, BitIndexUnit.Bit);
        await client.HyperLogLog.AddAsync("hll", "one", "two");
        await client.Geo.AddAsync("cities", new GeoEntry(-0.1276, 51.5072, "london"));

        var expected = server.ReceivedCommands;

        var batch = client.CreateBatch();
        _ = batch.Strings.SetManyExpire(
            RespireExpiry.In(TimeSpan.FromSeconds(1)), SetWhen.NotExists, ("a", "1"), ("b", "2"));
        _ = batch.Keys.Delete("k1", "k2", "k3");
        _ = batch.Keys.Expire("k1", RespireExpiry.At(DateTimeOffset.FromUnixTimeMilliseconds(987654321)));
        _ = batch.Hashes.Set("h", ("f1", "v1"), ("f2", "v2"));
        _ = batch.Hashes.SetExpire(
            "h", RespireExpiry.In(TimeSpan.FromSeconds(2)), SetWhen.NotExists, ("a", "one"));
        _ = batch.Hashes.Count("h");
        _ = batch.Lists.RightPush("l", "x", "y");
        _ = batch.Lists.Remove("l", "x", count: -1);
        _ = batch.Lists.Count("l");
        _ = batch.Sets.IntersectStore("dest", "s1", "s2");
        _ = batch.SortedSets.Add("z", new SortedSetEntry("ada", 42), new SortedSetEntry("grace", 58));
        _ = batch.SortedSets.CountByScore("z", 10, 50);
        _ = batch.Bitmaps.Count("bits", 0, 8, BitIndexUnit.Bit);
        _ = batch.HyperLogLog.Add("hll", "one", "two");
        _ = batch.Geo.Add("cities", new GeoEntry(-0.1276, 51.5072, "london"));
        await batch.ExecuteAsync();

        var queued = server.ReceivedCommands.Skip(expected.Count).ToArray();
        await Assert.That(queued).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task UnifiedExpiryFacets_EmitTheSameFramesWhenDeferred()
    {
        var flag = ":1\r\n"u8.ToArray();
        var value = "$5\r\nvalue\r\n"u8.ToArray();
        var fieldResult = "*1\r\n:1\r\n"u8.ToArray();
        var fieldValue = "*1\r\n$5\r\nvalue\r\n"u8.ToArray();
        await using var server = new FakeRespServer(
            flag, value, fieldResult, fieldValue, flag,
            flag, value, fieldResult, fieldValue, flag);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var instant = DateTimeOffset.FromUnixTimeMilliseconds(987654321);

        await client.Keys.ExpireAsync("key", RespireExpiry.In(TimeSpan.FromSeconds(2)), ExpireWhen.GreaterThan);
        await client.Strings.GetAndExpireAsync("key", RespireExpiry.At(instant));
        await client.Hashes.ExpireAsync("hash", RespireExpiry.Persist, "field");
        await client.Hashes.GetAndExpireAsync("hash", RespireExpiry.At(instant), "field");
        await client.Hashes.SetExpireAsync("hash", RespireExpiry.Keep, ("field", "value"));
        var expected = server.ReceivedCommands.ToArray();

        var batch = client.CreateBatch();
        _ = batch.Keys.Expire("key", RespireExpiry.In(TimeSpan.FromSeconds(2)), ExpireWhen.GreaterThan);
        _ = batch.Strings.GetAndExpire("key", RespireExpiry.At(instant));
        _ = batch.Hashes.Expire("hash", RespireExpiry.Persist, "field");
        _ = batch.Hashes.GetAndExpire("hash", RespireExpiry.At(instant), "field");
        _ = batch.Hashes.SetExpire("hash", RespireExpiry.Keep, ("field", "value"));
        await batch.ExecuteAsync();

        var queued = server.ReceivedCommands.Skip(expected.Length).ToArray();
        await Assert.That(queued).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DeferredTryGet_SeparatesMissingKeysFromStoredDefaults()
    {
        await using var server = new FakeRespServer(
            "$1\r\n0\r\n"u8.ToArray(),
            "$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var stored = batch.TryGet<int>("stored");
        var missing = batch.Strings.TryGet<int>("missing");
        await batch.ExecuteAsync();

        await Assert.That(stored.Result).IsEqualTo(new RespireGet<int>(true, 0));
        await Assert.That(missing.Result).IsEqualTo(default(RespireGet<int>));
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            new[] { "GET stored", "GET missing" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task DeferredScripts_PrefixKeysAndOwnTheirResults()
    {
        var reply = "*2\r\n$10\r\ntenant:key\r\n$3\r\narg\r\n"u8.ToArray();
        await using var server = new FakeRespServer(reply);
        await using var owner = await FakeRespServer.ConnectClientAsync(server.Port);
        var client = owner.WithKeyPrefix("tenant:");
        var script = RespireScript.Create("return {KEYS[1], ARGV[1]}");

        using var batch = client.CreateBatch();
        var pending = batch.Scripts.Evaluate(script, ["key"], ["arg"]);
        await batch.ExecuteAsync();
        batch.Dispose();

        using var result = pending.Result;
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].AsString()).IsEqualTo("tenant:key");
        await Assert.That(result[1].AsString()).IsEqualTo("arg");
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo(
            "EVAL return {KEYS[1], ARGV[1]} 1 tenant:key arg");
    }

    [Test]
    public async Task TransactionScripts_OwnNestedExecResults()
    {
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply,
            "+QUEUED\r\n"u8.ToArray(),
            "*1\r\n*2\r\n:7\r\n$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var script = RespireScript.Create("return {7, ARGV[1]}");

        var transaction = client.CreateTransaction();
        var pending = transaction.Scripts.Evaluate(script, args: ["value"]);
        await transaction.CommitAsync();

        using var result = pending.Result;
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].AsInteger()).IsEqualTo(7);
        await Assert.That(result[1].AsString()).IsEqualTo("value");
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(
            new[] { "MULTI", "EVAL return {7, ARGV[1]} 0 value", "EXEC" },
            CollectionOrdering.Matching);
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
        var pushed = transaction.Lists.RightPush("audit", "a", "b");
        var stored = transaction.Hashes.Set("user", "name", "Ada");
        var range = transaction.Lists.Range("audit");

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
        var deleted = batch.Delete("one", "two", "three");
        await batch.ExecuteAsync();

        await Assert.That(deleted.Result).IsEqualTo(3);
        await Assert.That(server.ReceivedCommands[0]).IsEqualTo("DEL one two three");
    }

    [Test]
    public async Task QueuedFacetResult_BeforeSend_ThrowsInsteadOfDeadlocking()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        var batch = client.CreateBatch();
        var pending = batch.Hashes.GetString("user", "name");

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
        var first = batch.GetString("first");
        var second = batch.Exists("second");

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
        await Assert.That(() => batch.GetString("third")).ThrowsExactly<ObjectDisposedException>();
        await Assert.That(async () => await batch.ExecuteAsync()).ThrowsExactly<ObjectDisposedException>();
    }

    [Test]
    public async Task SendingBatch_SetsIsSentAndDisposePreservesResult()
    {
        await using var server = new FakeRespServer("$5\r\nvalue\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = client.CreateBatch();
        var pending = batch.GetString("key");

        await Assert.That(batch.IsSent).IsFalse();

        await batch.ExecuteAsync();
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
