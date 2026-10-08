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
    [Test, NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CachedDeferredViewAllocatesNothing(bool prefixed)
    {
        await using var client = RespireClient.Create("localhost");
        var view = prefixed ? (RespireClient)client.WithKeyPrefix("tenant:{fixed}:") : client;
        _ = MeasureDeferredView(view, false);
        _ = MeasureDeferredView(view, true);
        var result = AllocationMeasurement.WithoutConcurrentGc(() => (
            Cached: MeasureDeferredView(view, false), Control: MeasureDeferredView(view, true)));
        await Assert.That(result.Cached).IsEqualTo(0);
        await Assert.That(result.Control).IsGreaterThanOrEqualTo(37_000);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDeferredView(RespireClient view, bool control)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            GC.KeepAlive(view.ForDeferredBatch());
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    public async Task ConcurrentDeferredViewsShareEncodingAndKeepOwnershipPolicyAcrossClones()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)],
            ReplicaEndpoints = [new("127.0.0.1", 2)],
            ClientSideCache = new(),
        });
        var view = (RespireClient)client.WithKeyPrefix("tenant:");
        var siblings = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(view.ForDeferredBatch)));
        var deferred = siblings[0];
        await Assert.That(siblings.All(sibling => ReferenceEquals(sibling, deferred))).IsTrue();
        await Assert.That(ReferenceEquals(deferred, deferred.ForDeferredBatch())).IsTrue();
        await Assert.That(deferred.KeyPrefixBytes.Overlaps(view.KeyPrefixBytes, out var offset) && offset == 0).IsTrue();
        await Assert.That(ReferenceEquals(deferred.Core, view.Core)).IsTrue();
        var replica = (RespireClient)deferred.WithReadFrom(RespireReadFrom.Replica);
        RespireClient[] variants = [deferred, replica, replica.PrimaryReadView,
            (RespireClient)deferred.WithoutClientCache(), (RespireClient)deferred.WithKeyPrefix("next:")];
        var source = "source"u8.ToArray();
        var owned = variants.Select(variant => variant.ResolveKey(source)).ToArray();
        var borrowed = view.ResolveKey(source);
        source[0] = (byte)'X';
        for (var index = 0; index < owned.Length; index++)
            await Assert.That(owned[index].ToString()).IsEqualTo(index == owned.Length - 1 ? "tenant:next:source" : "tenant:source");
        await Assert.That(borrowed.ToString()).IsEqualTo("tenant:Xource");
    }

    [Test]
    public async Task RootDeferredViewsOwnResolvedPrefixesAndRetainOrdinaryBorrowing()
    {
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", 1)], ReplicaEndpoints = [new("127.0.0.1", 2)],
            ClientSideCache = new(),
        });
        var siblings = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(client.ForDeferredBatch)));
        var deferred = siblings[0];
        await Assert.That(siblings.All(sibling => ReferenceEquals(sibling, deferred))).IsTrue();
        await Assert.That(ReferenceEquals(deferred, deferred.ForDeferredBatch())).IsTrue();
        await Assert.That(ReferenceEquals(deferred.Core, client.Core)).IsTrue();
        var replica = (RespireClient)deferred.WithReadFrom(RespireReadFrom.Replica);
        RespireClient[] variants = [deferred, replica, replica.PrimaryReadView,
            (RespireClient)deferred.WithoutClientCache()];
        var bytes = "source"u8.ToArray();
        var resolved = client.WithKeyPrefix("tenant:").ResolveKey(bytes);
        var owned = variants.Select(variant => variant.ResolveKey(resolved)).ToArray();
        var borrowed = variants.Select(variant => variant.ResolveKey(bytes)).ToArray();
        bytes[0] = (byte)'X';
        foreach (var key in owned) await Assert.That(key.ToString()).IsEqualTo("tenant:source");
        foreach (var key in borrowed) await Assert.That(key.ToString()).IsEqualTo("Xource");
        await Assert.That(resolved.ToString()).IsEqualTo("tenant:Xource");
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task QueuedPrefixedKeysOwnBinaryStorage(bool transaction, bool resolveBeforeQueueing)
    {
        var value = "$5\r\nvalue\r\n"u8.ToArray();
        byte[][] replies = transaction
            ? [FakeRespServer.OkReply, "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(),
                "+QUEUED\r\n"u8.ToArray(), "+QUEUED\r\n"u8.ToArray(), "*4\r\n+OK\r\n$5\r\nvalue\r\n+OK\r\n:2\r\n"u8.ToArray()]
            : [FakeRespServer.OkReply, value, FakeRespServer.OkReply, ":2\r\n"u8.ToArray()];
        await using var server = new FakeRespServer(replies);
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);
        var view = client.WithKeyPrefix("tenant:");
        var queueView = resolveBeforeQueueing ? client : view;
        using var batch = transaction ? null : queueView.CreateBatch();
        await using var tx = transaction ? queueView.CreateTransaction() : null;
        IRespireCommandQueue queue = tx ?? (IRespireCommandQueue)batch!;
        var source = "source"u8.ToArray();
        var destination = "target"u8.ToArray();
        RespireKey sourceKey = resolveBeforeQueueing ? view.ResolveKey(source) : source;
        RespireKey destinationKey = resolveBeforeQueueing ? view.ResolveKey(destination) : destination;
        var set = queue.Strings.Set(sourceKey, "value");
        var get = queue.Strings.Get<string>(sourceKey);
        var rename = queue.Keys.Rename(sourceKey, destinationKey);
        var delete = queue.Keys.Delete(sourceKey, destinationKey);
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
            7 => (new string('x', 1024) + "{", "tag}:£"),
            8 => ("tenant:", ""),
            9 => ("tenant:{fixed}:", new string('x', 8192)),
            10 => ("tenant:{outer{", "inner}:unused}"),
            11 => ("tenant:{\uD800", "\uDC00}:unused"),
            12 => ("tenant:", "{£}:unused"),
            13 => ("tenant:{}:", "{later}:unused"),
            14 => ("tenant:", "{}{later}:unused"),
            15 => ("tenant:{" + new string('£', 512), "𐍈}:unused"),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
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
                writer.Complete();
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
            writer.Complete();
            if (control) GC.KeepAlive(new byte[37]);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentFacetAccessReturnsOneInstanceOwnedByItsView(bool arrays)
    {
        await using var client = RespireClient.Create("localhost");
        var first = client.WithKeyPrefix("first:");
        var second = client.WithKeyPrefix("second:");
        object Facet(IRespireClient view) => arrays ? view.Arrays : view.Strings;
        var facets = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => Facet(first))));
        await Assert.That(facets.All(facet => ReferenceEquals(facet, Facet(first)))).IsTrue();
        await Assert.That(ReferenceEquals(Facet(first), Facet(second))).IsFalse();
        await Assert.That(ReferenceEquals(first.Keys, first.Keys)).IsTrue();
    }

    [Test, NotInParallel]
    public async Task CreatingViewDefersFacetAllocations()
    {
        await using var client = RespireClient.Create("localhost");
        _ = MeasureView(client, false);
        _ = MeasureView(client, true);
        _ = MeasureView(client, false, arraysOnly: true);
        var result = AllocationMeasurement.WithoutConcurrentGc(() => (
            View: MeasureView(client, false), WithArray: MeasureView(client, false, arraysOnly: true),
            WithFacets: MeasureView(client, true)));
        // The newly added array facet must also be deferred until first access.
        await Assert.That(result.WithArray).IsGreaterThan(result.View);
        // Each facet is a distinct object holding its view. Accessing all sixteen must
        // allocate at least sixteen pointer-sized fields beyond creating the view alone.
        await Assert.That(result.WithFacets - result.View).IsGreaterThanOrEqualTo(16 * IntPtr.Size);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureView(RespireClient client, bool facets, bool arraysOnly = false)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var view = client.WithKeyPrefix("tenant:");
        if (arraysOnly) GC.KeepAlive(view.Arrays);
        else if (facets)
        {
            GC.KeepAlive(view.Strings);
            GC.KeepAlive(view.Keys);
            GC.KeepAlive(view.Locks);
            GC.KeepAlive(view.Hashes);
            GC.KeepAlive(view.Lists);
            GC.KeepAlive(view.Arrays);
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
            writer.Complete();
            var payload = Encoding.UTF8.GetBytes("tenant:ascii-£-𐍈");
            var expected = "$6\r\nbefore\r\n"u8.ToArray()
                .Concat(Encoding.ASCII.GetBytes($"${payload.Length}\r\n"))
                .Concat(payload).Concat("\r\n$5\r\nafter\r\n"u8.ToArray());
            await Assert.That(buffer.WrittenMemory.ToArray().SequenceEqual(expected)).IsTrue();
        }
        finally { buffer.Release(); }
    }
}
