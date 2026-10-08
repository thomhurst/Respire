using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Respire.Protocol;
using Respire.Serialization;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ClientCacheStringPublicationTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly byte[] Hello = "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray();

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task FirstStringMissAndLocalHitReuseTheSameCanonicalString(bool coalesce, bool prefix)
    {
        await using var server = CreateServer("$7\r\né😀x\r\n"u8.ToArray());
        await using var root = await ConnectAsync(server, coalesce);
        var client = prefix ? root.WithKeyPrefix("tenant:") : root;
        var first = await client.GetStringAsync("key");
        var size = root.ClientSideCache!.GetStatistics().SizeBytes;
        await Assert.That(first).IsEqualTo("é😀x");
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(first);
        await Assert.That(root.ClientSideCache.GetStatistics().SizeBytes).IsEqualTo(size);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    public async Task CoalescedStringWaitersAndLocalHitReuseOneDecodedString()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, coalesce: true);
        var first = client.GetStringAsync("key").AsTask();
        var second = client.GetStringAsync("key").AsTask();
        await received.Task.WaitAsync(Limit);
        await server.SendRawAsync("$7\r\né😀x\r\n"u8.ToArray());
        var text = await first.WaitAsync(Limit);
        await Assert.That(await second.WaitAsync(Limit)).IsSameReferenceAs(text);
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(text);
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DecodedStorageIsChargedBeforeTheFirstStringMissReturns(bool coalesce)
    {
        var text = new string('x', 100);
        var reply = Encoding.UTF8.GetBytes("$100\r\n" + text + "\r\n");
        await using var server = CreateServer(reply);
        await using var client = await ConnectAsync(server, coalesce, maxSizeBytes: 200);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo(text);
        await Assert.That(client.ClientSideCache!.GetStatistics().Count).IsEqualTo(0);
        await Assert.That(client.ClientSideCache.GetStatistics().SizeBytes).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MixedCoalescedReadersKeepIndependentBytesAndCanonicalText(bool stringOwner)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, coalesce: true);
        Task<string?> textRead;
        Task<byte[]?> byteRead;
        if (stringOwner)
        {
            textRead = client.GetStringAsync("key").AsTask();
            byteRead = client.GetBytesAsync("key").AsTask();
        }
        else
        {
            byteRead = client.GetBytesAsync("key").AsTask();
            textRead = client.GetStringAsync("key").AsTask();
        }
        await received.Task.WaitAsync(Limit);
        await server.SendRawAsync("$7\r\né😀x\r\n"u8.ToArray());
        var text = await textRead.WaitAsync(Limit);
        var bytes = (await byteRead.WaitAsync(Limit))!;
        await Assert.That(bytes).IsEquivalentTo("é😀x"u8.ToArray());
        bytes[0] = 0;
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(text);
        await Assert.That((await client.GetBytesAsync("key"))!).IsEquivalentTo("é😀x"u8.ToArray());
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CustomSerializerResultsNeverSeedCanonicalText(bool coalesce, bool stringFirst)
    {
        var serializer = new TransformingSerializer();
        await using var server = CreateServer("$7\r\né😀x\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server, coalesce, serializer: serializer);
        var firstString = stringFirst ? await client.GetStringAsync("key") : null;
        await Assert.That((await client.GetAsync<Payload>("key"))!.Text).IsEqualTo("transformed-1");
        var binarySize = client.Core.ClientCache!.SizeBytes;
        var text = await client.GetStringAsync("key");
        await Assert.That(text).IsEqualTo("é😀x");
        if (stringFirst) await Assert.That(text).IsSameReferenceAs(firstString);
        else await Assert.That(client.Core.ClientCache.SizeBytes).IsGreaterThan(binarySize);
        await Assert.That((await client.GetAsync<Payload>("key"))!.Text).IsEqualTo("transformed-2");
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(text);
        await Assert.That(serializer.Calls).IsEqualTo(2);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false, 0)]
    [Arguments(false, 1)]
    [Arguments(false, 2)]
    [Arguments(true, 0)]
    [Arguments(true, 1)]
    [Arguments(true, 2)]
    public async Task PublicationPreservesNullEmptyAndMalformedUtf8(bool coalesce, int kind)
    {
        var reply = kind switch
        {
            0 => "$-1\r\n"u8.ToArray(),
            1 => new byte[] { (byte)'$', (byte)'3', 13, 10, 0xff, 0, (byte)'A', 13, 10 },
            _ => "$0\r\n\r\n"u8.ToArray(),
        };
        await using var server = CreateServer(reply);
        await using var client = await ConnectAsync(server, coalesce);
        var first = await client.GetStringAsync("key");
        await Assert.That(first).IsEqualTo(kind == 0 ? null : kind == 1 ? "�\0A" : "");
        var size = client.Core.ClientCache!.SizeBytes;
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(first);
        await Assert.That(client.Core.ClientCache.SizeBytes).IsEqualTo(size);
        if (kind == 1)
            await Assert.That((await client.GetBytesAsync("key"))!).IsEquivalentTo(new byte[] { 0xff, 0, 65 });
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task InvalidationDuringTheMissCannotPublishDecodedStorage(bool coalesce, bool clear)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        await using var server = CreateServer("$3\r\nnew\r\n"u8.ToArray());
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal) || Interlocked.Increment(ref reads) != 1)
                return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, coalesce);
        var first = client.GetStringAsync("key").AsTask();
        await received.Task.WaitAsync(Limit);
        var key = new RespireKey("key");
        if (clear) client.Core.ClientCache!.Clear();
        else client.Core.ClientCache!.Invalidate(in key);
        await server.SendRawAsync("$3\r\nold\r\n"u8.ToArray());
        await Assert.That(await first.WaitAsync(Limit)).IsEqualTo("old");
        await Assert.That(client.Core.ClientCache!.Count).IsEqualTo(0);
        await Assert.That(client.Core.ClientCache.SizeBytes).IsEqualTo(0);
        var fresh = await client.GetStringAsync("key");
        await Assert.That(fresh).IsEqualTo("new");
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(fresh);
        await Assert.That(client.Core.ClientCache.ActiveSharedReadCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ObsoleteBytePublicationCannotChargeAReplacementOrRetiredStore(bool retire)
    {
        var cache = new ClientSideCacheCoordinator(new());
        var key = new RespireKey("key");
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("old"u8.ToArray());
        var entry = cache.CompleteRead(in token, in response, allowInsert: true);
        var result = new ClientSideCacheCoordinator.GetReadResult(response, key, token.Store, entry, null);
        if (retire) cache.Clear();
        token = cache.BeginRead(in key);
        response = RespValue.BulkString("new"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true, decodedText: "new");
        var size = cache.SizeBytes;
        await Assert.That(result.GetString()).IsEqualTo("old");
        await Assert.That(cache.SizeBytes).IsEqualTo(size);
        await Assert.That(cache.TryGetString(in key, out var current)).IsTrue();
        await Assert.That(current).IsEqualTo("new");
        cache.Invalidate(in key);
        await Assert.That(cache.SizeBytes).IsEqualTo(0);
    }

    [Test]
    public async Task ASharedCachedResultOwnsTheBinaryKeyNeededForLaterTextPublication()
    {
        var cache = new ClientSideCacheCoordinator(new());
        var bytes = "key"u8.ToArray();
        var key = new RespireKey(bytes);
        var token = cache.BeginRead(in key);
        var response = RespValue.BulkString("value"u8.ToArray());
        cache.CompleteRead(in token, in response, allowInsert: true);
        var size = cache.SizeBytes;
        await Assert.That(cache.TryPeekRead(in key, out var result)).IsTrue();
        bytes[0] = (byte)'X';
        await Assert.That(result.GetString()).IsEqualTo("value");
        await Assert.That(cache.SizeBytes).IsGreaterThan(size);
        var original = new RespireKey("key");
        await Assert.That(cache.TryGetString(in original, out var retained)).IsTrue();
        await Assert.That(retained).IsSameReferenceAs(result.GetString());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ByteOnlyMissesKeepDecodedStorageLazyUnderASmallBudget(bool coalesce)
    {
        var text = new string('x', 100);
        await using var server = CreateServer(Encoding.UTF8.GetBytes("$100\r\n" + text + "\r\n"));
        await using var client = await ConnectAsync(server, coalesce, maxSizeBytes: 200);
        await Assert.That((await client.GetBytesAsync("key"))!.Length).IsEqualTo(100);
        await Assert.That(client.Core.ClientCache!.Count).IsEqualTo(1);
        await Assert.That(client.Core.ClientCache.SizeBytes).IsLessThanOrEqualTo(200);
        await Assert.That(await client.GetStringAsync("key")).IsEqualTo(text);
        await Assert.That(client.Core.ClientCache.Count).IsEqualTo(0);
        await Assert.That(client.Core.ClientCache.SizeBytes).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CancelingAStringWaiterPreservesTheSurvivingCallerAndCanonicalPublication(bool cancelOwner)
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = CreateServer(FakeRespServer.OkReply);
        server.SuppressReply = command =>
        {
            if (!command.StartsWith("GET ", StringComparison.Ordinal)) return false;
            received.TrySetResult();
            return true;
        };
        await using var client = await ConnectAsync(server, coalesce: true);
        using var cancellation = new CancellationTokenSource();
        Task<string?> canceled;
        Task<string?> survivor;
        if (cancelOwner)
        {
            canceled = client.GetStringAsync("key", cancellation.Token).AsTask();
            survivor = client.GetStringAsync("key").AsTask();
        }
        else
        {
            survivor = client.GetStringAsync("key").AsTask();
            canceled = client.GetStringAsync("key", cancellation.Token).AsTask();
        }
        await received.Task.WaitAsync(Limit);
        cancellation.Cancel();
        try
        {
            await canceled.WaitAsync(Limit);
            throw new InvalidOperationException("Expected caller cancellation.");
        }
        catch (OperationCanceledException error)
        {
            await Assert.That(error.CancellationToken).IsEqualTo(cancellation.Token);
        }
        await server.SendRawAsync("$7\r\né😀x\r\n"u8.ToArray());
        var text = await survivor.WaitAsync(Limit);
        await Assert.That(text).IsEqualTo("é😀x");
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(text);
        await Assert.That(client.Core.ClientCache!.ActiveSharedReadCount).IsEqualTo(0);
        await Assert.That(server.ReceivedCommands.Count(c => c.StartsWith("GET "))).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FirstLocalReuseAfterAStringMissAllocatesNothing(bool coalesce)
    {
        await using var server = CreateServer("$7\r\né😀x\r\n"u8.ToArray());
        await using var client = await ConnectAsync(server, coalesce);
        for (var index = 0; index < 32; index++)
        {
            client.ClientSideCache!.Clear();
            await client.GetStringAsync("key");
            MeasureFirstReuse(client, false);
            MeasureFirstReuse(client, true);
        }
        client.ClientSideCache!.Clear();
        var first = await client.GetStringAsync("key");
        var measured = AllocationMeasurement.WithoutConcurrentGc(() => (
            Actual: MeasureFirstReuse(client, false), Control: MeasureFirstReuse(client, true)));
        await Assert.That(measured.Actual).IsEqualTo(0);
        await Assert.That(measured.Control).IsGreaterThanOrEqualTo(37);
        await Assert.That(await client.GetStringAsync("key")).IsSameReferenceAs(first);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureFirstReuse(RespireClient client, bool control)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();
        var pending = client.GetStringAsync("key");
        if (!pending.IsCompletedSuccessfully) throw new InvalidOperationException("Expected a local hit.");
        GC.KeepAlive(pending.Result);
        if (control) GC.KeepAlive(new byte[37]);
        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static FakeRespServer CreateServer(byte[] getReply)
        => new(Hello, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command.StartsWith("HELLO ") ? Hello
                : command.StartsWith("GET ") ? getReply : FakeRespServer.OkReply,
        };

    private static ValueTask<RespireClient> ConnectAsync(FakeRespServer server, bool coalesce,
        long maxSizeBytes = 1_000_000, IRespireSerializer? serializer = null)
        => RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = [new("127.0.0.1", server.Port)], Connections = 1,
            ClientSideCache = new() { CoalesceConcurrentMisses = coalesce, MaxSizeBytes = maxSizeBytes },
            CommandTimeout = Limit, ConnectTimeout = Limit,
            Serializer = serializer ?? new SystemTextJsonSerializer(),
        });

    private sealed record Payload(string Text);

    private sealed class TransformingSerializer : IRespireSerializer
    {
        internal int Calls;
        public void Serialize(IBufferWriter<byte> destination, Type type, object? value)
            => throw new NotSupportedException();
        public void Serialize<T>(IBufferWriter<byte> destination, T value) => throw new NotSupportedException();
        public object Deserialize(Type type, ReadOnlySpan<byte> payload)
            => new Payload("transformed-" + Interlocked.Increment(ref Calls));
        public T? Deserialize<T>(ReadOnlySpan<byte> payload) => (T)Deserialize(typeof(T), payload);
    }
}
