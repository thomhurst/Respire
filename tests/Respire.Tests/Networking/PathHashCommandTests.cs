using System.Text;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class PathHashCommandTests
{
    private const string Fields = "*2\r\n$1\r\nf\r\n$1\r\nv\r\n";
    private const string Map = "%1\r\n$1\r\nf\r\n$1\r\nv\r\n";
    private const string Path = "$2\r\n/a\r\n";

    [Test]
    public async Task SingleFieldOverloadsPreserveArrayResultsAndCancellation()
    {
        await using var server = new FakeRespServer(
            "*1\r\n$1\r\nv\r\n"u8.ToArray(), "*1\r\n$-1\r\n"u8.ToArray(), ":1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var paths = client.WithKeyPrefix("tenant:").PathHashes;
        using var cancellation = new CancellationTokenSource();
        var found = await paths.GetFieldsAsync("tree", "/a", "f", cancellation.Token);
        await Assert.That(found.Length).IsEqualTo(1);
        await Assert.That(found[0]!).IsEquivalentTo("v"u8.ToArray());
        var missing = await paths.GetFieldsAsync("tree", "/a", "missing", cancellation.Token);
        await Assert.That(missing.Length).IsEqualTo(1);
        await Assert.That(missing[0]).IsNull();
        await Assert.That(await paths.RemoveAsync("tree", "/a", "f", cancellation.Token)).IsEqualTo(1);
        cancellation.Cancel();
        await Assert.That(async () => await paths.GetFieldsAsync("tree", "/a", "f", cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(async () => await paths.RemoveAsync("tree", "/a", "f", cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "PHGET tenant:tree /a f", "PHGET tenant:tree /a missing", "PHDEL tenant:tree /a f"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImmediateRepliesAndWireShapes(bool resp3)
    {
        var payload = resp3 ? Map : Fields;
        var entry = "*2\r\n" + Path + payload;
        await using var server = new FakeRespServer(
            FakeRespServer.OkReply, "$-1\r\n"u8.ToArray(), FakeRespServer.OkReply,
            "*1\r\n$1\r\nv\r\n"u8.ToArray(), "*1\r\n$-1\r\n"u8.ToArray(),
            "*2\r\n$1\r\nv\r\n$-1\r\n"u8.ToArray(), "*2\r\n$1\r\nv\r\n$-1\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes(payload), ":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(),
            ":1\r\n"u8.ToArray(), ":2\r\n"u8.ToArray(), Encoding.ASCII.GetBytes(entry),
            Encoding.ASCII.GetBytes("*1\r\n" + entry), Encoding.ASCII.GetBytes("*2\r\n$1\r\n0\r\n*1\r\n" + entry));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var paths = client.WithKeyPrefix("tenant:").PathHashes;
        await Assert.That(await paths.SetAsync("tree", "/a", "f", "v")).IsTrue();
        await Assert.That(await paths.SetFieldsAsync("tree", "/a", [("f", "v")], RespireSetWhen.NotExists)).IsFalse();
        await Assert.That(await paths.SetManyAsync("tree", [new("/a", [("f", "v")]), new("/b", [("g", "w")])])).IsTrue();
        var value = await paths.GetBytesAsync("tree", "/a", "f");
        await Assert.That(value!).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(await paths.GetStringAsync("tree", "/a", "missing")).IsNull();
        var fields = await paths.GetFieldsAsync("tree", "/a", ["f", "missing"]);
        await Assert.That(fields[1]).IsNull();
        var many = await paths.GetManyAsync("tree", [new("/a", ["f"]), new("/b", ["missing"])]);
        await Assert.That(many[0]!).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(many[1]).IsNull();
        var all = await paths.GetAllAsync("tree", "/a");
        await Assert.That(all[0].Field).IsEquivalentTo("f"u8.ToArray());
        await Assert.That(all[0].Value).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(await paths.ExistsAsync("tree", "/a")).IsTrue();
        await Assert.That(await paths.CountAsync("tree")).IsEqualTo(2);
        await Assert.That(await paths.RemoveAsync("tree", "/a", ["f"])).IsEqualTo(1);
        await Assert.That(await paths.RemovePrefixAsync("tree", "/")).IsEqualTo(2);
        var longest = await paths.LongestPrefixAsync("tree", "/abc");
        await Assert.That(longest!.Path).IsEquivalentTo("/a"u8.ToArray());
        var prefixes = await paths.PrefixesAsync("tree", "/abc", count: 2, maxPathLength: 3);
        await Assert.That(prefixes[0].Fields[0].Value).IsEquivalentTo("v"u8.ToArray());
        var page = await paths.ScanPageAsync("tree", prefix: "/a", count: 2);
        await Assert.That(page.IsComplete).IsTrue();
        await Assert.That(page.Entries[0].Path).IsEquivalentTo(longest.Path);
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "PHSET tenant:tree /a FIELDS 1 f v", "PHSET tenant:tree /a FNX FIELDS 1 f v",
            "PHMSET tenant:tree /a FIELDS 1 f v /b FIELDS 1 g w", "PHGET tenant:tree /a f",
            "PHGET tenant:tree /a missing", "PHGET tenant:tree /a f missing",
            "PHMGET tenant:tree /a FIELDS 1 f /b FIELDS 1 missing", "PHGETALL tenant:tree /a",
            "PHEXISTS tenant:tree /a", "PHCARD tenant:tree", "PHDEL tenant:tree /a f",
            "PHDELPREFIX tenant:tree /", "PHLONGEST tenant:tree /abc WITHVALUES",
            "PHPREFIXES tenant:tree /abc WITHVALUES COUNT 2 MAXPATHLEN 3",
            "PHSCAN tenant:tree 0 PREFIX /a COUNT 2 WITHVALUES"]);
    }

    [Test]
    public async Task ManualPagingPreservesOwnedBinaryCursor()
    {
        byte[] cursor = [0xff, 0, 0x80, (byte)'0'];
        var firstPage = "*2\r\n$4\r\n"u8.ToArray().Concat(cursor).Concat("\r\n*0\r\n"u8.ToArray()).ToArray();
        await using var server = new FakeRespServer(firstPage, "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var page = await client.PathHashes.ScanPageAsync("tree");
        await Assert.That(page.IsComplete).IsFalse();
        var last = await client.PathHashes.ScanPageAsync("tree", page.Cursor);
        await Assert.That(last.IsComplete).IsTrue();
        await Assert.That(page.Cursor.Span.SequenceEqual(cursor)).IsTrue();
        await Assert.That(server.ReceivedArguments[1][2].AsSpan().SequenceEqual(cursor)).IsTrue();
    }

    [Test]
    [Arguments("0", true)]
    [Arguments("00", false)]
    [Arguments("+0", false)]
    [Arguments("0\0", false)]
    public async Task CompletionRequiresExactAsciiZero(string cursor, bool complete)
    {
        var page = new RespirePathHashPage(Encoding.UTF8.GetBytes(cursor), []);
        await Assert.That(page.IsComplete).IsEqualTo(complete);
    }

    [Test]
    public async Task StreamingPreservesBinaryCursorAcrossEmptyPages()
    {
        byte[] cursor = [0xff, 0, 0x80, (byte)'0'];
        var firstPage = "*2\r\n$4\r\n"u8.ToArray().Concat(cursor).Concat("\r\n*0\r\n"u8.ToArray()).ToArray();
        await using var server = new FakeRespServer(firstPage, "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await foreach (var _ in client.PathHashes.ScanAsync("tree")) { }
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(2);
        await Assert.That(server.ReceivedArguments[1][2].AsSpan().SequenceEqual(cursor)).IsTrue();
    }

    [Test]
    public async Task StreamingPreservesOpaqueCursorAcrossEmptyPagesAndOwnsResults()
    {
        var entry = "*2\r\n" + Path + Fields;
        await using var server = new FakeRespServer(
            "*2\r\n$7\r\nopaque!\r\n*0\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes("*2\r\n$1\r\n0\r\n*1\r\n" + entry));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var entries = new List<RespirePathHashEntry>();
        await foreach (var item in client.PathHashes.ScanAsync("tree", prefix: "/", count: 1)) entries.Add(item);
        await Assert.That(entries.Count).IsEqualTo(1);
        await Assert.That(entries[0].Fields[0].Value).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(server.ReceivedCommands).IsEquivalentTo([
            "PHSCAN tree 0 PREFIX / COUNT 1 WITHVALUES", "PHSCAN tree opaque! PREFIX / COUNT 1 WITHVALUES"]);
    }

    [Test]
    public async Task StreamingPinsEachEnumerationAndSpreadsFreshScansAcrossReplicas()
    {
        var entry = "*2\r\n" + Path + Fields;
        var firstPage = Encoding.ASCII.GetBytes("*2\r\n$7\r\nopaque!\r\n*1\r\n" + entry);
        var lastPage = "*2\r\n$1\r\n0\r\n*0\r\n"u8.ToArray();
        byte[]? Pages(string command) => command.StartsWith("PHSCAN tree 0 ", StringComparison.Ordinal) ? firstPage
            : command.StartsWith("PHSCAN tree opaque! ", StringComparison.Ordinal) ? lastPage : null;
        var replicaRole = "*5\r\n$5\r\nslave\r\n$9\r\n127.0.0.1\r\n:6379\r\n$9\r\nconnected\r\n:0\r\n"u8.ToArray();
        await using var primary = new FakeRespServer(FakeRespServer.OkReply);
        await using var first = new FakeRespServer(replicaRole) { ReplyOverride = (_, command) => Pages(command) };
        await using var second = new FakeRespServer(replicaRole) { ReplyOverride = (_, command) => Pages(command) };
#pragma warning disable RESP010 // One client serves both enumerations against this test's isolated replica topology.
        await using var client = RespireClient.Create(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2, Connections = 1,
            Endpoints = [new("127.0.0.1", primary.Port)],
            ReplicaEndpoints = [new("127.0.0.1", first.Port), new("127.0.0.1", second.Port)],
        });
#pragma warning restore RESP010
        await client.Core.ReadRouter.GetReplicaFromEndpointsAsync([new("127.0.0.1", first.Port)], default, default);
        await client.Core.ReadRouter.GetReplicaFromEndpointsAsync([new("127.0.0.1", second.Port)], default, default);
        var view = client.WithReadFrom(RespireReadFrom.Replica);
        for (var i = 0; i < 2; i++)
        {
            var entries = new List<RespirePathHashEntry>();
            await foreach (var item in view.PathHashes.ScanAsync("tree", count: 1)) entries.Add(item);
            await Assert.That(entries.Count).IsEqualTo(1);
        }
        foreach (var replica in new[] { first, second })
            await Assert.That(replica.ReceivedCommands.Where(command => command.StartsWith("PHSCAN ", StringComparison.Ordinal)))
                .IsEquivalentTo(["PHSCAN tree 0 COUNT 1 WITHVALUES", "PHSCAN tree opaque! COUNT 1 WITHVALUES"]);
        await Assert.That(primary.ReceivedCommands).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DeferredCommandsSnapshotBinaryInput(bool transaction)
    {
        var entry = "*2\r\n" + Path + Map;
        byte[][] results = [FakeRespServer.OkReply, "*1\r\n$1\r\nv\r\n"u8.ToArray(),
            Encoding.ASCII.GetBytes(entry), Encoding.ASCII.GetBytes("*2\r\n$1\r\n0\r\n*1\r\n" + entry), ":1\r\n"u8.ToArray(),
            "*1\r\n$1\r\nv\r\n"u8.ToArray(), ":1\r\n"u8.ToArray()];
        var replies = transaction
            ? new[] { FakeRespServer.OkReply }.Concat(Enumerable.Repeat("+QUEUED\r\n"u8.ToArray(), results.Length))
                .Append(Encoding.ASCII.GetBytes($"*{results.Length}\r\n").Concat(results.SelectMany(value => value)).ToArray()).ToArray()
            : results;
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transaction ? null : view.CreateBatch();
        await using var tx = transaction ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        var key = "tree"u8.ToArray(); var path = "/a"u8.ToArray(); var field = "f"u8.ToArray(); var value = "v"u8.ToArray();
        var write = queue.PathHashes.Set(key, path, field, value, RespireSetWhen.Exists);
        var read = queue.PathHashes.GetBytes(key, path, field);
        var match = queue.PathHashes.LongestPrefix(key, "/abc");
        byte[] cursor = [0xff, 0, 0x80];
        var page = queue.PathHashes.ScanPage(key, cursor, prefix: path, count: 1);
        var deleted = queue.PathHashes.Remove(key, path);
        var fieldRead = queue.PathHashes.GetFields(key, path, field);
        var fieldDeleted = queue.PathHashes.Remove(key, path, field);
        key[0] = (byte)'X'; path[0] = (byte)'X'; field[0] = (byte)'X'; value[0] = (byte)'X';
        cursor[0] = (byte)'X';
        if (tx is not null) await tx.CommitAsync(); else await batch!.ExecuteAsync();
        await Assert.That(write.Result).IsTrue();
        await Assert.That(read.Result!).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(match.Result!.Path).IsEquivalentTo("/a"u8.ToArray());
        await Assert.That(page.Result.IsComplete).IsTrue();
        await Assert.That(deleted.Result).IsEqualTo(1);
        await Assert.That(fieldRead.Result.Length).IsEqualTo(1);
        await Assert.That(fieldRead.Result[0]!).IsEquivalentTo("v"u8.ToArray());
        await Assert.That(fieldDeleted.Result).IsEqualTo(1);
        var scan = server.ReceivedArguments.Single(args => args[0].AsSpan().SequenceEqual("PHSCAN"u8));
        await Assert.That(scan[2].AsSpan().SequenceEqual(new byte[] { 0xff, 0, 0x80 })).IsTrue();
        await Assert.That(server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC")).IsEquivalentTo([
            "PHSET tenant:tree /a FXX FIELDS 1 f v", "PHGET tenant:tree /a f",
            "PHLONGEST tenant:tree /abc WITHVALUES", $"PHSCAN tenant:tree {Encoding.UTF8.GetString(new byte[] { 0xff, 0, 0x80 })} PREFIX /a COUNT 1 WITHVALUES", "PHDEL tenant:tree /a",
            "PHGET tenant:tree /a f", "PHDEL tenant:tree /a f"]);
    }

    [Test]
    public async Task TypedValuesPreserveStoredDefaultAndMissing()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply,
            "*1\r\n$1\r\n0\r\n"u8.ToArray(), "*1\r\n$1\r\n0\r\n"u8.ToArray(), "*1\r\n$-1\r\n"u8.ToArray());
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await client.PathHashes.SetAsync<int>("tree", "a", "count", 0);
        await Assert.That(await client.PathHashes.GetAsync<int>("tree", "a", "count")).IsEqualTo(0);
        var found = await client.PathHashes.TryGetAsync<int>("tree", "a", "count");
        var missing = await client.PathHashes.TryGetAsync<int>("tree", "a", "missing");
        await Assert.That(found.Found).IsTrue();
        await Assert.That(missing.Found).IsFalse();
    }

    [Test]
    [Arguments("*1\r\n:42\r\n")]
    [Arguments("*0\r\n")]
    [Arguments("$-1\r\n")]
    public async Task MalformedScalarArrayIsRejected(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.PathHashes.GetBytesAsync("tree", "a", "f")).Throws<RespireProtocolException>();
    }

    [Test]
    [Arguments("$-1\r\n")]
    [Arguments("*0\r\n")]
    [Arguments("*2\r\n:0\r\n*0\r\n")]
    [Arguments("*2\r\n$0\r\n\r\n*0\r\n")]
    [Arguments("*2\r\n$1\r\n0\r\n$-1\r\n")]
    [Arguments("*2\r\n$1\r\n0\r\n*1\r\n*2\r\n$1\r\na\r\n*1\r\n$1\r\nf\r\n")]
    public async Task MalformedPageIsRejected(string reply)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(reply));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.PathHashes.ScanPageAsync("tree")).Throws<RespireProtocolException>();
    }

    [Test]
    public async Task CancelingEnumerationStopsWithinPage()
    {
        var entry = "*2\r\n" + Path + Fields;
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes("*2\r\n$7\r\nopaque!\r\n*2\r\n" + entry + entry));
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        using var canceled = new CancellationTokenSource();
        await using var stream = client.PathHashes.ScanAsync("tree").GetAsyncEnumerator(canceled.Token);
        await Assert.That(await stream.MoveNextAsync()).IsTrue();
        canceled.Cancel();
        await Assert.That(async () => await stream.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands.Count).IsEqualTo(1);
    }

    [Test]
    public async Task InvalidArgumentsAndCancellationSendNothing()
    {
        await using var server = new FakeRespServer(FakeRespServer.OkReply);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        await Assert.That(async () => await client.PathHashes.SetFieldsAsync("tree", "a", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.PathHashes.GetFieldsAsync("tree", "a", [])).Throws<ArgumentException>();
        await Assert.That(async () => await client.PathHashes.PrefixesAsync("tree", "a", count: 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.PathHashes.ScanPageAsync("tree", count: 0)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.PathHashes.SetAsync("tree", "a", "f", "v", (RespireSetWhen)100)).Throws<ArgumentOutOfRangeException>();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.That(async () => await client.PathHashes.CountAsync("tree", canceled.Token)).Throws<OperationCanceledException>();
        var stream = client.PathHashes.ScanAsync("tree", cancellationToken: canceled.Token).GetAsyncEnumerator();
        await using (stream) await Assert.That(async () => await stream.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(server.ReceivedCommands).IsEmpty();
    }
}
