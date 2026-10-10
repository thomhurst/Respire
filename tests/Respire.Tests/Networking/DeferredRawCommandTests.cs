using System.Text;
using Respire.Internal;
using TUnit.Assertions;
using TUnit.Assertions.Enums;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class DeferredRawCommandTests
{
    [Test]
    public async Task DeferredCommandsPreserveDescriptorReadAndCursorMetadata()
    {
        var read = DeferredRawCommands.CreateCommand(RespireCommands.String.GET, ["GET", "key"], 1, 1);
        var cursor = DeferredRawCommands.CreateCommand(RespireCommands.Key.SCAN, ["SCAN", "17"], -1, 1);

        await Assert.That(read.ReadKind).IsEqualTo(ReadCommandKind.Read);
        await Assert.That(read.CursorArgumentIndex).IsEqualTo(-1);
        await Assert.That(cursor.ReadKind).IsEqualTo(ReadCommandKind.CursorRead);
        await Assert.That(cursor.CursorArgumentIndex).IsEqualTo(0);
        await Assert.That(CursorCommandMetadata.IsCursorContinuation(in cursor)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ArgumentsAreSnapshotsAndResultsOwnNestedStorage(bool transactional)
    {
        byte[] reply = [.. "*3\r\n$2\r\n"u8, 255, 0, .. "\r\n$-1\r\n-ERR nested\r\n"u8];
        await using var server = new FakeRespServer(Replies(transactional, reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        byte[] key = "key"u8.ToArray();
        byte[] value = "value"u8.ToArray();
        RespireValue[] args = [key, value];
        var pending = queue.Execute("set", args);
        await Assert.That(server.ReceivedCommands).IsEmpty();
        await Assert.That(() => pending.Result).Throws<RespirePendingNotReadyException>();
        Array.Fill(key, (byte)'x');
        Array.Fill(value, (byte)'x');
        args[0] = "changed";
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        batch?.Dispose();
        if (transaction is not null) await transaction.DisposeAsync();
        var result = pending.Result;
        var nested = result[0];
        await Assert.That(nested.AsBytes()).IsEquivalentTo(new byte[] { 255, 0 }, CollectionOrdering.Matching);
        await Assert.That(result[1].IsNull).IsTrue();
        await Assert.That(result[2].IsError).IsTrue();
        await Assert.That(result[2].ErrorMessage).IsEqualTo("ERR nested");
        var copy = result;
        copy.Dispose();
        result.Dispose();
        await Assert.That(nested.IsDisposed).IsTrue();
        await Assert.That(() => nested.AsBytes()).Throws<ObjectDisposedException>();
        await Assert.That(server.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["SET tenant:key value"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ServerErrorsFaultOnlyTheirPendingAndPreserveFollowingReplies(bool transactional)
    {
        await using var server = new FakeRespServer(Replies(transactional,
            "-WRONGTYPE wrong\r\n"u8.ToArray(), "$2\r\nok\r\n"u8.ToArray()));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var batch = transactional ? null : client.CreateBatch();
        await using var transaction = transactional ? client.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var failed = queue.Execute("GET", "wrong");
        var good = queue.Execute("GET", "good");
        if (transaction is not null)
            await transaction.CommitAsync();
        else
            await Assert.That((await batch!.TryExecuteAsync()).FailureCount).IsEqualTo(1);
        await Assert.That(() => failed.Result).Throws<RespireServerException>();
        using var result = good.Result;
        await Assert.That(result.AsString()).IsEqualTo("ok");
    }

    [Test]
    [Arguments("BLPOP")]
    [Arguments("BLMOVE")]
    [Arguments("BZMPOP")]
    [Arguments("XREAD")]
    [Arguments("XREADGROUP")]
    [Arguments("WAIT")]
    [Arguments("WAITAOF")]
    [Arguments("MULTI")]
    [Arguments("EXEC")]
    [Arguments("DISCARD")]
    [Arguments("WATCH")]
    [Arguments("UNWATCH")]
    [Arguments("SUBSCRIBE")]
    [Arguments("SSUBSCRIBE")]
    [Arguments("UNSUBSCRIBE")]
    [Arguments("PSUBSCRIBE")]
    [Arguments("CLIENT REPLY")]
    [Arguments("CLIENT")]
    [Arguments("AUTH")]
    [Arguments("HELLO")]
    [Arguments("SELECT")]
    [Arguments("RESET")]
    [Arguments("ASKING")]
    [Arguments("READONLY")]
    [Arguments("QUIT")]
    [Arguments("MONITOR")]
    [Arguments("SHUTDOWN")]
    [Arguments("DEBUG")]
    [Arguments("SORT")]
    [Arguments("GEORADIUS")]
    [Arguments("JSON.GET")]
    [Arguments("UNKNOWN")]
    [Arguments("GET key")]
    [Arguments("FUNCTION LOAD")]
    public async Task UnsupportedCommandsNeverEnqueue(string name)
    {
        await using var client = RespireClient.Create("localhost:1");
        using var batch = client.CreateBatch();
        await using var transaction = client.CreateTransaction();
        foreach (IRespireCommandQueue queue in new IRespireCommandQueue[] { batch, transaction })
            await Assert.That(() => queue.Execute(name.ToLowerInvariant(), "BLOCK", 0, "STREAMS", "key", "$"))
                .Throws<NotSupportedException>();
        await Assert.That(batch.Count).IsEqualTo(0);
        await Assert.That(transaction.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("")]
    [Arguments(" GET")]
    [Arguments("GET ")]
    [Arguments("GET\r\nSET")]
    [Arguments("GET\tkey")]
    [Arguments("GET\0")]
    [Arguments("OBJECT  ENCODING")]
    [Arguments("pıng")]
    public async Task InvalidNamesFailBeforeEnqueue(string name)
    {
        await using var client = RespireClient.Create("localhost:1");
        using var batch = client.CreateBatch();
        await Assert.That(() => batch.Execute(name, "key")).Throws<ArgumentException>();
        await Assert.That(batch.Count).IsEqualTo(0);
    }

    [Test]
    public async Task InvalidLayoutsDoNotPinClusterTransaction()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("localhost", 1)],
        });
        await using var transaction = client.CreateTransaction();
        await Assert.That(() => transaction.Execute("MGET", "{a}:one", "{b}:two")).Throws<RespireServerException>();
        await Assert.That(() => transaction.Execute("EVAL", "return 1", -1)).Throws<ArgumentException>();
        await Assert.That(() => transaction.Execute("EVAL", "return 1", RespireValue.Null)).Throws<ArgumentNullException>();
        await Assert.That(() => transaction.Execute("EVAL", "return 1", long.MaxValue)).Throws<ArgumentException>();
        await Assert.That(() => transaction.Execute("EVAL", "return 1", 2, "only-one-key")).Throws<ArgumentException>();
        await Assert.That(() => transaction.Execute("ZINTERSTORE", "{a}:dest", 1, "{b}:source")).Throws<RespireServerException>();
        await Assert.That(() => transaction.Execute("MSET", "key")).Throws<ArgumentException>();
        await Assert.That(() => transaction.Execute("GET")).Throws<ArgumentException>();
        await Assert.That(() => transaction.Execute("SET", "key", RespireValue.Null)).Throws<ArgumentNullException>();
        _ = transaction.Execute("GET", "{c}:key");
        await Assert.That(() => transaction.Execute("GET", "{d}:key")).Throws<InvalidOperationException>();
        await Assert.That(transaction.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AllKeyLayoutsPrefixAndRouteOnlyKeys(bool transactional)
    {
        (RespireCommand Command, RespireValue[] Args, string Wire)[] cases =
        [
            (RespireCommands.String.GET, ["one"], "GET {tenant}:one"),
            ("COPY", ["one", "two", "REPLACE"], "COPY {tenant}:one {tenant}:two REPLACE"),
            ("MGET", ["one", "two"], "MGET {tenant}:one {tenant}:two"),
            ("MSET", ["one", "{wrong}", "two", "value"], "MSET {tenant}:one {wrong} {tenant}:two value"),
            ("BITOP", ["AND", "dest", "one", "two"], "BITOP AND {tenant}:dest {tenant}:one {tenant}:two"),
            ("FCALL", ["name", 2, "one", "two", "{wrong}"], "FCALL name 2 {tenant}:one {tenant}:two {wrong}"),
            ("LMPOP", [2, "one", "two", "LEFT"], "LMPOP 2 {tenant}:one {tenant}:two LEFT"),
            ("ZUNIONSTORE", ["dest", 2, "one", "two", "WEIGHTS", 1, 2], "ZUNIONSTORE {tenant}:dest 2 {tenant}:one {tenant}:two WEIGHTS 1 2"),
            (RespireCommands.Key.OBJECT_ENCODING, ["one"], "OBJECT ENCODING {tenant}:one"),
        ];
        var replies = cases.Select(_ => FakeRespServer.OkReply).ToArray();
        await using var owner = new FakeRespServer(Replies(transactional, replies));
        var slot = ClusterHash.GetSlot("{tenant}:one");
        var topology = Encoding.ASCII.GetBytes($"*1\r\n*3\r\n:{slot}\r\n:{slot}\r\n*2\r\n$9\r\n127.0.0.1\r\n:{owner.Port}\r\n");
        await using var seed = new FakeRespServer(topology, "-ERR wrong route\r\n"u8.ToArray());
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2,
            UseCluster = true, Connections = 1, Endpoints = [new("127.0.0.1", seed.Port)],
        });
        var view = client.WithKeyPrefix("{tenant}:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = cases.Select(item => queue.Execute(item.Command, item.Args)).ToArray();
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        foreach (var item in pending) item.Result.Dispose();
        await Assert.That(seed.ReceivedCommands).IsEquivalentTo(["CLUSTER SLOTS"]);
        await Assert.That(owner.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(cases.Select(item => item.Wire), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ZeroKeyScriptDoesNotPrefixArguments(bool transactional)
    {
        await using var server = new FakeRespServer(Replies(transactional, FakeRespServer.OkReply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transactional ? null : view.CreateBatch();
        await using var transaction = transactional ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = transaction ?? (IRespireCommandQueue)batch!;
        var pending = queue.Execute("EVAL", "return ARGV[1]", 0, "argument");
        if (transaction is not null) await transaction.CommitAsync();
        else await batch!.ExecuteAsync();
        using var result = pending.Result;
        await Assert.That(server.ReceivedCommands.Where(c => c is not "MULTI" and not "EXEC"))
            .IsEquivalentTo(["EVAL return ARGV[1] 0 argument"]);
    }

    private static byte[][] Replies(bool transaction, params byte[][] replies)
        => transaction
            ? [FakeRespServer.OkReply, .. replies.Select(_ => "+QUEUED\r\n"u8.ToArray()),
                Encoding.ASCII.GetBytes($"*{replies.Length}\r\n").Concat(replies.SelectMany(reply => reply)).ToArray()]
            : replies;

}
