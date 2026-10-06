using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using Respire.Tests.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests;

public class PrefixedKeyTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task QueuedPrefixedKeysOwnBinaryStorage(bool transaction)
    {
        var value = "$5\r\nvalue\r\n"u8.ToArray();
        byte[][] replies = transaction
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), "*4\r\n+OK\r\n$5\r\nvalue\r\n+OK\r\n:2\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply, value, FakeRespServer.OkReply, ":2\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        using var batch = transaction ? null : view.CreateBatch();
        await using var tx = transaction ? view.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        var source = "source"u8.ToArray();
        var destination = "target"u8.ToArray();
        var set = queue.Strings.Set(source, "value");
        var get = queue.Strings.Get<string>(source);
        var rename = queue.Keys.Rename(source, destination);
        var delete = queue.Keys.Delete(source, destination);
        source[0] = (byte)'X';
        destination[0] = (byte)'Y';
        if (tx is not null) await tx.CommitAsync();
        else await batch!.ExecuteAsync();
        await Assert.That(set.Result).IsTrue();
        await Assert.That(get.Result).IsEqualTo("value");
        _ = rename.Result;
        await Assert.That(delete.Result).IsEqualTo(2);
        var commands = server.ReceivedCommands.Where(command => command is not "MULTI" and not "EXEC").ToArray();
        await Assert.That(commands.SequenceEqual(new[]
        {
            "SET tenant:source value", "GET tenant:source", "RENAME tenant:source tenant:target",
            "DEL tenant:source tenant:target",
        })).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    [Arguments(14)]
    [Arguments(15)]
    public async Task PrefixPreservesWireIdentityAndClusterSlot(int scenario)
    {
        var (prefix, text) = scenario switch
        {
            0 => ("tenant:", "key"),
            1 => ("£:", "𐍈"),
            2 => ("{tenant", "}:key"),
            3 => ("{", "}:empty{later}"),
            4 => ("before{", "unfinished"),
            5 => ("\uD800", "\uDC00tail"),
            6 => ("\uD800", "\uD801"),
            8 => ("tenant:", ""),
            9 => ("tenant:{fixed}:", new string('x', 8192)),
            10 => ("tenant:{outer{", "inner}:unused}"),
            11 => ("tenant:{\uD800", "\uDC00}:unused"),
            12 => ("tenant:", "{£}:unused"),
            13 => ("tenant:{}:", "{later}:unused"),
            14 => ("tenant:", "{}{later}:unused"),
            15 => ("tenant:{" + new string('£', 512), "𐍈}:unused"),
            _ => (new string('x', 1024) + "{", "tag}:£"),
        };
        await using var client = RespireClient.Create("localhost");
        var view = (RespireClient)client.WithKeyPrefix(prefix);
        foreach (var binary in new[] { false, true })
        {
            RespireKey input = binary ? new RespireKey(Encoding.UTF8.GetBytes(text)) : new RespireKey(text);
            var expected = binary
                ? Encoding.UTF8.GetBytes(prefix).Concat(Encoding.UTF8.GetBytes(text)).ToArray()
                : Encoding.UTF8.GetBytes(prefix + text);
            var resolved = view.ResolveKey(input);
            var value = view.Key(in input);
            var buffer = new WriteBuffer(expected.Length + 64);
            try
            {
                var writer = new RespWriter(buffer);
                value.WriteTo(ref writer);
                var frame = Encoding.ASCII.GetBytes($"${expected.Length}\r\n").Concat(expected).Concat("\r\n"u8.ToArray());
                await Assert.That(buffer.WrittenMemory.ToArray().SequenceEqual(frame)).IsTrue();
                await Assert.That(resolved.ToBytes().SequenceEqual(expected)).IsTrue();
                await Assert.That(resolved.WireLength).IsEqualTo(expected.Length);
                await Assert.That(resolved.IsEmpty).IsFalse();
                await Assert.That(value.IsEmpty).IsFalse();
                await Assert.That(resolved.ClusterSlot).IsEqualTo(new RespireKey(expected).ClusterSlot);
                await Assert.That(value.TryGetClusterSlot(out var slot)).IsTrue();
                await Assert.That(slot).IsEqualTo(resolved.ClusterSlot);
                await Assert.That(resolved == new RespireKey(expected)).IsTrue();
                await Assert.That(new RespireKey(expected) == resolved).IsTrue();
                await Assert.That(value == new RespireValue(expected)).IsTrue();
                await Assert.That(value.AsKey() == resolved).IsTrue();
                await Assert.That(resolved.GetHashCode()).IsEqualTo(new RespireKey(expected).GetHashCode());
                var prefixes = Internal.ClientCachePrefixSet.Create([new RespireKey(expected.AsMemory(0, 1))]);
                await Assert.That(resolved.StartsWithAny(prefixes)).IsTrue();
                await Assert.That(resolved.StartsWithAny(Internal.ClientCachePrefixSet.Create(["unrelated"]))).IsFalse();
            }
            finally { buffer.Release(); }
        }
    }

    [Test]
    public async Task BinarySnapshotOwnsStorageAndChainedViewsComposePrefixes()
    {
        await using var client = RespireClient.Create("localhost");
        var view = (RespireClient)client.WithKeyPrefix("first:").WithKeyPrefix("second:");
        byte[] bytes = [0, 255, 128];
        RespireKey key = bytes;
        var resolved = view.ResolveKey(key);
        var snapshot = resolved.Snapshot();
        var valueSnapshot = view.Key(in key).Snapshot();
        var expected = "first:second:"u8.ToArray().Concat(bytes).ToArray();
        bytes[0] = 42;
        await Assert.That(snapshot.ToBytes().SequenceEqual(expected)).IsTrue();
        await Assert.That(valueSnapshot.AsKey() == snapshot).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrefixedValuesRetainNumericAndEqualitySemantics(bool binary)
    {
        await using var client = RespireClient.Create("localhost");
        var view = (RespireClient)client.WithKeyPrefix("1");
        RespireKey key = binary ? new RespireKey("23"u8.ToArray()) : new RespireKey("23");
        var value = view.Key(in key);
        await Assert.That(value.TryGetInt64(out var number)).IsTrue();
        await Assert.That(number).IsEqualTo(123);
        await Assert.That(value == (RespireValue)123).IsTrue();
        await Assert.That((RespireValue)123 == value).IsTrue();
        await Assert.That(value == RespireValue.Null).IsFalse();
        await Assert.That(value == (RespireValue)"other").IsFalse();
        await Assert.That(value == view.Key(in key)).IsTrue();
        await Assert.That(value.EqualsAsciiIgnoreCase("123")).IsTrue();
        await Assert.That(value.GetHashCode()).IsEqualTo(((RespireValue)123).GetHashCode());
        await Assert.That(value.ToString()).IsEqualTo("123");
    }

    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PrefixedGetSerializationAllocatesNothing(bool binary)
    {
        await using var client = RespireClient.Create("localhost");
        var view = (RespireClient)client.WithKeyPrefix("tenant:");
        RespireKey key = binary ? new RespireKey("key"u8.ToArray()) : new RespireKey("key");
        var buffer = new WriteBuffer(256);
        try
        {
            _ = Measure(client, key, buffer, false);
            _ = Measure(view, key, buffer, false);
            _ = Measure(view, key, buffer, true);
            var result = AllocationMeasurement.WithoutConcurrentGc(() => (
                Plain: Measure(client, key, buffer, false),
                Prefixed: Measure(view, key, buffer, false),
                Control: Measure(view, key, buffer, true)));
            await Assert.That(result.Plain).IsEqualTo(0);
            await Assert.That(result.Prefixed).IsEqualTo(0);
            await Assert.That(result.Control).IsGreaterThanOrEqualTo(37_000);
        }
        finally { buffer.Release(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Measure(RespireClient client, RespireKey key, WriteBuffer buffer, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            buffer.Reset();
            var writer = new RespWriter(buffer);
            var command = new Cmd1(RespireCommands.String.GET.Verb, client.Key(in key));
            command.Write(ref writer);
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ConcurrentFacetAccessReturnsOneInstanceOwnedByItsView()
    {
        await using var client = RespireClient.Create("localhost");
        var first = client.WithKeyPrefix("first:");
        var second = client.WithKeyPrefix("second:");
        var facets = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => first.Strings)));
        await Assert.That(facets.All(facet => ReferenceEquals(facet, first.Strings))).IsTrue();
        await Assert.That(ReferenceEquals(first.Strings, second.Strings)).IsFalse();
        await Assert.That(ReferenceEquals(first.Keys, first.Keys)).IsTrue();
    }

    [Test, NotInParallel]
    public async Task CreatingViewDefersFacetAllocations()
    {
        await using var client = RespireClient.Create("localhost");
        _ = MeasureView(client, false);
        _ = MeasureView(client, true);
        var result = AllocationMeasurement.WithoutConcurrentGc(() => (
            View: MeasureView(client, false), WithFacets: MeasureView(client, true)));
        // Each facet is a distinct object holding its view. Accessing all fifteen must
        // allocate at least fifteen pointer-sized fields beyond creating the view alone.
        await Assert.That(result.WithFacets - result.View).IsGreaterThanOrEqualTo(15 * IntPtr.Size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureView(RespireClient client, bool facets)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var view = client.WithKeyPrefix("tenant:");
        if (facets)
        {
            GC.KeepAlive(view.Strings);
            GC.KeepAlive(view.Keys);
            GC.KeepAlive(view.Locks);
            GC.KeepAlive(view.Hashes);
            GC.KeepAlive(view.Lists);
            GC.KeepAlive(view.Sets);
            GC.KeepAlive(view.SortedSets);
            GC.KeepAlive(view.Streams);
            GC.KeepAlive(view.Bitmaps);
            GC.KeepAlive(view.HyperLogLog);
            GC.KeepAlive(view.Geo);
            GC.KeepAlive(view.VectorSets);
            GC.KeepAlive(view.Scripts);
            GC.KeepAlive(view.Functions);
            GC.KeepAlive(view.Server);
        }
        GC.KeepAlive(view);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReapplyingPrefixPreservesTextAndBinaryBoundarySemantics(bool binary)
    {
        await using var client = RespireClient.Create("localhost");
        RespireKey key = binary ? new RespireKey("key"u8.ToArray()) : new RespireKey("key");
        var first = client.WithKeyPrefix("\uDC00").ResolveKey(key);
        var second = client.WithKeyPrefix("\uD800").ResolveKey(first);
        var expected = binary
            ? Encoding.UTF8.GetBytes("\uD800").Concat(Encoding.UTF8.GetBytes("\uDC00key")).ToArray()
            : Encoding.UTF8.GetBytes("\uD800\uDC00key");
        await Assert.That(second.ToBytes().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    public async Task PrefixedBinaryMetadataDoesNotDecodeInvalidUtf8()
    {
        await using var client = RespireClient.Create("localhost");
        var view = (RespireClient)client.WithKeyPrefix("tenant:");
        RespireKey key = new byte[] { 255 };
        var value = view.Key(in key);
        await Assert.That(value.EqualsAsciiIgnoreCase("tenant:�")).IsFalse();
        await Assert.That(value == (RespireValue)"tenant:�").IsFalse();
        var expected = Internal.Utf8String.GetString("tenant:"u8.ToArray().Concat(new byte[] { 255 }).ToArray().AsMemory());
        await Assert.That(value.ToString()).IsEqualTo(expected);
        await Assert.That(value.AsKey().ToString()).IsEqualTo(expected);
    }

    [Test]
    public async Task CompletePrefixTagDoesNotReadBinarySuffix()
    {
        await using var client = RespireClient.Create("localhost");
        using var suffix = new GuardedSuffix();
        var key = new RespireKey(suffix.Memory);
        suffix.RejectReads = true;
        var resolved = client.WithKeyPrefix("tenant:{fixed}:").ResolveKey(key);
        await Assert.That(resolved.ClusterSlot).IsEqualTo(new RespireKey("{fixed}").ClusterSlot);
        // Positive control: the backing memory really rejects payload access.
        await Assert.That(() => resolved.ToBytes()).Throws<InvalidOperationException>();
    }

    private sealed class GuardedSuffix : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[8192];
        internal bool RejectReads { get; set; }
        public override Span<byte> GetSpan()
            => RejectReads ? throw new InvalidOperationException("The unused suffix was read.") : _bytes;
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    [Test]
    public async Task AsciiFallbackPreservesEarlierFrames()
    {
        await using var client = RespireClient.Create("localhost");
        var key = client.WithKeyPrefix("tenant:").ResolveKey("ascii-£-𐍈");
        var buffer = new WriteBuffer(16);
        try
        {
            var writer = new RespWriter(buffer);
            writer.WriteBulkString("before");
            key.WriteTo(ref writer);
            writer.WriteBulkString("after");
            var payload = Encoding.UTF8.GetBytes("tenant:ascii-£-𐍈");
            var expected = "$6\r\nbefore\r\n"u8.ToArray()
                .Concat(Encoding.ASCII.GetBytes($"${payload.Length}\r\n"))
                .Concat(payload).Concat("\r\n$5\r\nafter\r\n"u8.ToArray());
            await Assert.That(buffer.WrittenMemory.ToArray().SequenceEqual(expected)).IsTrue();
        }
        finally { buffer.Release(); }
    }
}
