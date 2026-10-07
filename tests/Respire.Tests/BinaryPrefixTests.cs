using System.Text;
using Respire.Commands;
using Respire.Internal;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class BinaryPrefixTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task BlockingTypedListsPrefixKeysAroundTimeoutAndCountArguments(bool counted)
    {
        await using var server = new FakeRespServer(2, "*-1\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] prefix = [255, 0];
        var view = root.WithKeyPrefix((RespireKey)prefix);
        if (counted)
            await Assert.That(await view.Lists.PopManyAsync(["one", "two"], waitFor: TimeSpan.FromMilliseconds(1))).IsNull();
        else
            await Assert.That(await view.Lists.PopAsync(["one", "two"], TimeSpan.FromMilliseconds(1))).IsNull();
        var received = server.ReceivedArguments.Single();
        var firstKey = counted ? 3 : 1;
        await Assert.That(received[firstKey].SequenceEqual(prefix.Concat("one"u8.ToArray()))).IsTrue();
        await Assert.That(received[firstKey + 1].SequenceEqual(prefix.Concat("two"u8.ToArray()))).IsTrue();
    }

    [Test]
    public async Task TypedCommandsPrefixMultipleKeysAndStripStreamReplyKeys()
    {
        byte[] prefix = [255, 0, (byte)':'];
        byte[] streamReply = [.. "*1\r\n*2\r\n"u8, .. Bulk([.. prefix, .. "stream"u8]), .. "*0\r\n"u8];
        await using var server = new FakeRespServer(":1\r\n"u8.ToArray(), "*2\r\n$-1\r\n$-1\r\n"u8.ToArray(),
            streamReply, ":1\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = root.WithKeyPrefix((RespireKey)prefix);
        await Assert.That(await view.Keys.CopyAsync("source", "target")).IsTrue();
        _ = await view.Strings.GetManyAsync("one", "two");
        var streams = await view.Streams.ReadAsync([("stream", RespireStreamId.Beginning)]);
        await Assert.That(streams.Length).IsEqualTo(1);
        await Assert.That(streams[0].Key).IsEqualTo((RespireKey)"stream");
        await view.PublishAsync("literal-channel", "payload"u8.ToArray());
        var received = server.ReceivedArguments;
        await Assert.That(received[0][1].SequenceEqual(prefix.Concat("source"u8.ToArray()))).IsTrue();
        await Assert.That(received[0][2].SequenceEqual(prefix.Concat("target"u8.ToArray()))).IsTrue();
        await Assert.That(received[1][1].SequenceEqual(prefix.Concat("one"u8.ToArray()))).IsTrue();
        await Assert.That(received[1][2].SequenceEqual(prefix.Concat("two"u8.ToArray()))).IsTrue();
        await Assert.That(received[2][2].SequenceEqual(prefix.Concat("stream"u8.ToArray()))).IsTrue();
        await Assert.That(received[3][1].AsSpan().SequenceEqual("literal-channel"u8)).IsTrue();
    }

    [Test]
    public async Task SortPatternsPreserveBytesAndRejectRedisSyntaxInBinaryPrefixes()
    {
        await using var server = new FakeRespServer("*0\r\n"u8.ToArray());
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] prefix = [255, (byte)':'];
        var view = root.WithKeyPrefix((RespireKey)prefix);
        _ = await view.Keys.SortAsync("list", new() { By = "weight:*", Get = new RespireKey[] { "value:*", "#" } });
        var received = server.ReceivedArguments[0];
        await Assert.That(received[1].SequenceEqual(prefix.Concat("list"u8.ToArray()))).IsTrue();
        await Assert.That(received[3].SequenceEqual(prefix.Concat("weight:*"u8.ToArray()))).IsTrue();
        await Assert.That(received[5].SequenceEqual(prefix.Concat("value:*"u8.ToArray()))).IsTrue();
        await Assert.That(received[7].AsSpan().SequenceEqual("#"u8)).IsTrue();
        foreach (var unsafeBytes in new byte[][] { [255, (byte)'*'], [255, (byte)'-', (byte)'>'], [255, 0] })
            await Assert.That(async () => await root.WithKeyPrefix((RespireKey)unsafeBytes).Keys.SortAsync("list", new() { By = "weight:*" }))
                .Throws<NotSupportedException>();
        await Assert.That(server.ReceivedArguments.Count).IsEqualTo(1);
    }

    [Test]
    public async Task BinaryViewsPreserveCorePoliciesAndDeferredOwnership()
    {
        await using var root = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ReplicaEndpoints = [new("127.0.0.1", 2)], ClientSideCache = new(),
        });
        var view = (RespireClient)root.WithKeyPrefix((RespireKey)new byte[] { 255, 0 });
        var replica = (RespireClient)view.WithReadFrom(RespireReadFrom.Replica);
        var bypass = (RespireClient)replica.WithoutClientCache();
        await Assert.That(replica.GetBatchReadFromPolicy()).IsEqualTo(RespireReadFrom.Replica);
        await Assert.That(bypass.ReadCache).IsNull();
        byte[] suffix = [(byte)'k'];
        var borrowed = view.ResolveKey(suffix);
        var owned = bypass.ForDeferredBatch().ResolveKey(suffix);
        suffix[0] = (byte)'x';
        await Assert.That(owned.ToBytes().SequenceEqual(new byte[] { 255, 0, (byte)'k' })).IsTrue();
        await Assert.That(borrowed.ToBytes().SequenceEqual(new byte[] { 255, 0, (byte)'x' })).IsTrue();
        await Assert.That(bypass.PrimaryReadView.KeyPrefixBytes.SequenceEqual(view.KeyPrefixBytes)).IsTrue();
        await bypass.DisposeAsync();
        await replica.DisposeAsync();
        await view.DisposeAsync();
        await Assert.That(root.Core.Disposed).IsFalse();
    }

    [Test]
    public async Task ServerInvalidationUsesExactPhysicalPrefixBytes()
    {
        await using var server = new FakeRespServer(
            "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray(), FakeRespServer.OkReply,
            FakeRespServer.OkReply, Bulk("old"u8.ToArray()), FakeRespServer.OkReply, Bulk("new"u8.ToArray()));
        await using var root = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespProtocol.Resp2, Connections = 1, ClientSideCache = new(),
            Endpoints = [new("127.0.0.1", server.Port)],
        });
        var view = root.WithKeyPrefix((RespireKey)new byte[] { 255, 0 });
        var physical = view.ResolveKey("key");
        var invalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observer = root.ClientSideCache!.SubscribeInvalidations(physical, _ => invalidated.TrySetResult());
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("old");
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("old");
        await server.SendRawAsync([.. ">2\r\n+invalidate\r\n*1\r\n"u8, .. Bulk(physical.ToBytes())]);
        await invalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(await view.GetStringAsync("key")).IsEqualTo("new");
        await Assert.That(server.ReceivedArguments.Count(command => command[0].AsSpan().SequenceEqual("GET"u8)
            && command[1].SequenceEqual(physical.ToBytes()))).IsEqualTo(2);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task RawPrefixContractsRemainExplicitOnImmediateBatchAndTransactionPaths(int mode)
    {
        (string Operation, RespireValue[] Args, int[] Keys, bool Deferred)[] cases =
        [
            ("PING", [], [], true),
            ("GET", ["one"], [0], true),
            ("COPY", ["one", "two", "REPLACE"], [0, 1], true),
            ("MGET", ["one", "two"], [0, 1], true),
            ("MSET", ["one", "value", "two", "value"], [0, 2], true),
            ("BITOP", ["AND", "dest", "one", "two"], [1, 2, 3], true),
            ("EVAL", ["return 1", 2, "one", "two", "argument"], [2, 3], true),
            ("FCALL", ["function", 2, "one", "two", "argument"], [2, 3], true),
            ("LMPOP", [2, "one", "two", "LEFT"], [1, 2], true),
            ("ZUNIONSTORE", ["dest", 2, "one", "two", "WEIGHTS", 1, 2], [0, 2, 3], true),
            ("OBJECT ENCODING", ["one"], [0], true),
            ("XREAD", ["STREAMS", "one", "two", "0", "0"], [1, 2], false),
            ("XREADGROUP", ["GROUP", "group", "consumer", "STREAMS", "one", "two", ">", ">"], [4, 5], false),
            ("JSON.MSET", ["one", "$", "{}", "two", "$", "{}"], [0, 3], false),
            ("TS.MADD", ["one", 1, 2, "two", 3, 4], [0, 3], false),
            ("JSON.DEBUG", ["MEMORY", "one", "$"], [1], false),
            ("JSON.DEBUG HELP", [], [], false),
            ("JSON.MGET", ["one", "two", "$"], [0, 1], false),
            ("MSETEX", [2, "one", "value", "two", "value"], [1, 3], false),
            ("BLMPOP", [0.001, 2, "one", "two", "LEFT"], [2, 3], false),
            ("MIGRATE", ["host", 6379, "", 0, 1000, "KEYS", "one", "two"], [6, 7], false),
        ];
        var unsupportedDeferred = cases.Where(item => !item.Deferred).ToArray();
        if (mode != 0) cases = cases.Where(item => item.Deferred).ToArray();
        byte[][] replies = mode == 2
            ? [FakeRespServer.OkReply, .. cases.Select(_ => "+QUEUED\r\n"u8.ToArray()),
                Encoding.ASCII.GetBytes($"*{cases.Length}\r\n" + string.Concat(cases.Select(_ => "+OK\r\n")))]
            : cases.Select(_ => FakeRespServer.OkReply).ToArray();
        await using var server = new FakeRespServer(2, replies);
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        byte[] prefix = [255, 0, (byte)':'];
        var view = root.WithKeyPrefix((RespireKey)prefix);
        using var batch = mode == 1 ? view.CreateBatch() : null;
        await using var transaction = mode == 2 ? view.CreateTransaction() : null;
        IRespireCommandQueue? queue = transaction ?? (IRespireCommandQueue?)batch;
        if (queue is not null)
            foreach (var item in unsupportedDeferred)
                await Assert.That(() => queue.Execute(item.Operation, item.Args)).Throws<NotSupportedException>();
        var pending = new List<RespirePending<RespireResult>>();
        foreach (var item in cases)
        {
            if (queue is null) { using var result = await view.ExecuteAsync((RespireCommand)item.Operation, item.Args); }
            else pending.Add(queue.Execute(item.Operation, item.Args));
        }
        if (batch is not null) await batch.ExecuteAsync();
        if (transaction is not null) await transaction.CommitAsync();
        foreach (var result in pending) result.Result.Dispose();
        var received = server.ReceivedArguments.Where(args => !args[0].AsSpan().SequenceEqual("MULTI"u8)
            && !args[0].AsSpan().SequenceEqual("EXEC"u8)).ToArray();
        await Assert.That(received.Length).IsEqualTo(cases.Length);
        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var words = item.Operation.Split(' ');
            await Assert.That(received[index].Length).IsEqualTo(words.Length + item.Args.Length);
            for (var argument = 0; argument < item.Args.Length; argument++)
            {
                var expected = item.Args[argument].AsKey().ToBytes();
                // Immediate caller-supplied core commands retain physical arguments. Known
                // module layouts and supported deferred layouts apply the view's prefix.
                if (item.Keys.Contains(argument) && (mode != 0 || item.Operation.Contains('.')))
                    expected = [.. prefix, .. expected];
                await Assert.That(received[index][words.Length + argument].SequenceEqual(expected)).IsTrue();
            }
        }
        await Assert.That(async () => { using var result = await view.ExecuteAsync(RespireCommands.String.MGET, "key"); })
            .Throws<NotSupportedException>();
    }

    [Test]
    public async Task ScanEscapesBytesAndFiltersBeforeDecoding()
    {
        byte[] prefix = [255, (byte)'*', 0];
        byte[] first = [.. prefix, .. "visible"u8];
        byte[] other = [254, (byte)'*', 0, .. "secret"u8];
        byte[] response = [.. "*2\r\n$1\r\n0\r\n*2\r\n"u8, .. Bulk(first), .. Bulk(other)];
        await using var server = new FakeRespServer(response);
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var keys = new List<string>();
        await foreach (var key in root.WithKeyPrefix((RespireKey)prefix).Keys.ScanAsync()) keys.Add(key);
        await Assert.That(keys).IsEquivalentTo(["visible"]);
        await Assert.That(server.ReceivedArguments[0][3].SequenceEqual(new byte[] { 255, (byte)'\\', (byte)'*', 0, (byte)'*' })).IsTrue();
    }

    internal static byte[] Bulk(byte[] bytes)
        => [.. Encoding.ASCII.GetBytes($"${bytes.Length}\r\n"), .. bytes, .. "\r\n"u8];

    [Test]
    public async Task TextPrefixesKeepSplitSurrogateScanSemantics()
    {
        var physical = Encoding.UTF8.GetBytes("\uD800\uDC00key");
        await using var server = new FakeRespServer([.. "*2\r\n$1\r\n0\r\n*1\r\n"u8, .. Bulk(physical)]);
        await using var root = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = root.WithKeyPrefix((RespireKey)"\uD800");
        var keys = new List<string>();
        await foreach (var key in view.Keys.ScanAsync("\uDC00*")) keys.Add(key);
        await Assert.That(keys).IsEquivalentTo(["\uDC00key"]);
        await Assert.That(view.ResolveKey(keys[0]).ToBytes().SequenceEqual(physical)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrefixOwnsBytesAndPreservesKeyIdentity(bool binarySuffix)
    {
        await using var root = RespireClient.Create("localhost");
        byte[] prefix = [255, 0, (byte)':'];
        var view = root.WithKeyPrefix((RespireKey)prefix);
        prefix[0] = 1;
        var suffix = "£:key";
        RespireKey key = binarySuffix ? Encoding.UTF8.GetBytes(suffix) : suffix;
        var resolved = view.ResolveKey(key);
        byte[] expected = [255, 0, (byte)':', .. Encoding.UTF8.GetBytes(suffix)];
        await Assert.That(resolved.ToBytes().SequenceEqual(expected)).IsTrue();
        await Assert.That(resolved == new RespireKey(expected)).IsTrue();
        await Assert.That(resolved.GetHashCode()).IsEqualTo(new RespireKey(expected).GetHashCode());
        await Assert.That(resolved.ClusterSlot).IsEqualTo(ClusterHash.GetSlot(expected));
        var nested = root.WithKeyPrefix("outer:").ResolveKey(resolved);
        await Assert.That(nested.ToBytes().SequenceEqual("outer:"u8.ToArray().Concat(expected))).IsTrue();
    }

    [Test]
    [Arguments("{fixed}:", "key")]
    [Arguments("{split", "}:key")]
    [Arguments("{", "}:empty{later}")]
    [Arguments("{}:", "{later}:key")]
    [Arguments("plain:", "{£}:key")]
    public async Task BinaryPrefixClusterSlotMatchesPhysicalBytes(string prefixText, string suffix)
    {
        await using var root = RespireClient.Create("localhost");
        byte[] prefix = [255, .. Encoding.UTF8.GetBytes(prefixText)];
        var view = root.WithKeyPrefix((RespireKey)prefix);
        byte[] expected = [.. prefix, .. Encoding.UTF8.GetBytes(suffix)];
        await Assert.That(view.ResolveKey(suffix).ClusterSlot).IsEqualTo(ClusterHash.GetSlot(expected));
        await Assert.That(view.ResolveKey(Encoding.UTF8.GetBytes(suffix)).ClusterSlot).IsEqualTo(ClusterHash.GetSlot(expected));
    }

    [Test]
    public async Task MixedViewsComposeWithoutDecodingBinaryPrefixes()
    {
        await using var root = RespireClient.Create("localhost");
        var view = root.WithKeyPrefix("a:").WithKeyPrefix((RespireKey)new byte[] { 255, 0 }).WithKeyPrefix("b:");
        byte[] expected = [(byte)'a', (byte)':', 255, 0, (byte)'b', (byte)':', (byte)'k'];
        await Assert.That(view.ResolveKey("k").ToBytes().SequenceEqual(expected)).IsTrue();
        await Assert.That(() => root.WithKeyPrefix(RespireKey.Empty)).Throws<ArgumentException>();
    }

    [Test]
    public async Task BinaryCompositionKeepsTextReplacementBytesAtSurrogateBoundary()
    {
        await using var root = RespireClient.Create("localhost");
        const string text = "a\uD800";
        byte[] binary = [255, 0];
        var view = root.WithKeyPrefix(text).WithKeyPrefix((RespireKey)binary).WithKeyPrefix("b:");
        byte[] expected = [.. Encoding.UTF8.GetBytes(text), .. binary, .. "b:key"u8];
        await Assert.That(view.ResolveKey("key").ToBytes().SequenceEqual(expected)).IsTrue();
        var composed = new KeyPrefix(text).Append((RespireKey)binary);
        byte[] expectedPrefix = [.. Encoding.UTF8.GetBytes(text), .. binary];
        await Assert.That(composed.Bytes.AsSpan().SequenceEqual(expectedPrefix)).IsTrue();
    }
}
