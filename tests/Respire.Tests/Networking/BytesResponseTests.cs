using System.Text;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BytesResponseTests
{
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(1024)]
    [Arguments(4095)]
    [Arguments(4096)]
    [Arguments(1048576)]
    public async Task BufferedAndFragmentedRepliesReturnOwnedBytes(int length)
    {
        var expected = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        var command = new Cmd1(Verbs.Get, "key");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync([.. Encoding.ASCII.GetBytes($"${length}\r\n"), .. expected, 13, 10]);
        var owned = await first.WaitAsync(deadline.Token);
        await Assert.That(owned!.AsSpan().SequenceEqual(expected)).IsTrue();

        var second = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 2, deadline.Token);
        // Attributes and fragmented framing must not change the top-level reply's ownership.
        await server.SendRawAsync("|1\r\n+meta\r\n+value\r\n$"u8.ToArray());
        await server.SendRawAsync(Encoding.ASCII.GetBytes($"{length}\r\n"));
        var half = length / 2;
        if (half != 0) await server.SendRawAsync(expected[..half]);
        await server.SendRawAsync([.. expected[half..], 13]);
        await server.SendRawAsync([10]);
        var next = await second.WaitAsync(deadline.Token);
        await Assert.That(next!.AsSpan().SequenceEqual(expected)).IsTrue();
        if (length > 0)
        {
            next![0] ^= 0xff;
            await Assert.That(owned.AsSpan().SequenceEqual(expected)).IsTrue();
        }
    }

    [Test]
    public async Task PipelinedRepliesInOneReceiveCompleteInOrder()
    {
        // TCP may coalesce or split these sends, so this covers reply order across whatever
        // receive boundaries occur; DirectBulkCompletion* below pin the boundaries exactly.
        byte[][] payloads = [null!, [], "x"u8.ToArray(), Pattern(4095), Pattern(4096), null!, Pattern(64)];
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        var command = new Cmd1(Verbs.Get, "key");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = new Task<byte[]?>[payloads.Length];
        for (var i = 0; i < pending.Length; i++)
            pending[i] = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, pending.Length, deadline.Token);

        var frames = new List<byte>();
        for (var i = 0; i < payloads.Length - 1; i++)
        {
            if (i == 5) frames.AddRange("-ERR failed\r\n"u8.ToArray());
            else if (payloads[i] is null) frames.AddRange("$-1\r\n"u8.ToArray());
            else frames.AddRange([.. Encoding.ASCII.GetBytes($"${payloads[i].Length}\r\n"), .. payloads[i], 13, 10]);
        }

        // The last reply's payload arrives in a later receive than its header.
        var last = payloads[^1];
        frames.AddRange([.. "$64\r\n"u8, .. last[..10]]);
        await server.SendRawAsync([.. frames]);
        await server.SendRawAsync([.. last[10..], 13, 10]);

        await Assert.That(await pending[0].WaitAsync(deadline.Token)).IsNull();
        for (var i = 1; i < 5; i++)
        {
            var result = await pending[i].WaitAsync(deadline.Token);
            await Assert.That(result!.AsSpan().SequenceEqual(payloads[i])).IsTrue();
        }
        await Assert.That(async () => await pending[5].WaitAsync(deadline.Token)).Throws<RespireServerException>();
        var tail = await pending[6].WaitAsync(deadline.Token);
        await Assert.That(tail!.AsSpan().SequenceEqual(last)).IsTrue();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(4095)]
    [Arguments(4096)]
    [Arguments(-1)]
    public async Task DirectBulkCompletionUsesOnlyCompleteBufferedFrames(int length)
    {
        var expected = length < 0 ? null : Pattern(length);
        byte[] header = Encoding.ASCII.GetBytes($"${length}\r\n");
        byte[] frame = expected is null ? header : [.. header, .. expected, 13, 10];
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);

        if (expected is not null)
        {
            // Every strict prefix of the frame, and a bad terminator, leaves the head in place.
            for (var cut = header.Length; cut < frame.Length; cut++)
            {
                await Assert.That(TryCompleteBulkDirect(connection, frame.AsSpan(0, cut), header.Length, length, out _)).IsFalse();
            }
            byte[] malformed = [.. frame[..^2], 0, 0];
            await Assert.That(TryCompleteBulkDirect(connection, malformed, header.Length, length, out _)).IsFalse();
            await Assert.That(Inflight(connection).TryPeek(out var head)).IsTrue();
            await Assert.That(head is BytesPendingResponseSource).IsTrue();
            await Assert.That(pending.IsCompleted).IsFalse();
        }

        // Trailing bytes from the next reply are not consumed.
        byte[] buffer = [.. frame, .. "$1\r\nz\r\n"u8];
        await Assert.That(TryCompleteBulkDirect(connection, buffer, header.Length, length, out var frameEnd)).IsTrue();
        await Assert.That(frameEnd).IsEqualTo(frame.Length);
        Completions(connection).Flush();
        var result = await pending.WaitAsync(deadline.Token);
        if (expected is null) await Assert.That(result).IsNull();
        else await Assert.That(result!.AsSpan().SequenceEqual(expected)).IsTrue();
    }

    [Test]
    [NotInParallel] // Allocation measurement; see docs/ALLOCATION_MEASUREMENT.md.
    public async Task DirectBulkCompletionSkipsCopyForAbandonedHead()
    {
        const int length = 64 * 1024;
        byte[] warmFrame = "$1\r\nw\r\n"u8.ToArray();
        byte[] header = Encoding.ASCII.GetBytes($"${length}\r\n");
        byte[] frame = [.. header, .. new byte[length], 13, 10];
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var command = new Cmd1(Verbs.Get, "key");
        using var warmCancellation = new CancellationTokenSource();
        using var cancellation = new CancellationTokenSource();
        // Ring order: canceled, live (warm-up pair), then canceled, live (measured pair).
        var warmCanceled = connection.SendBytesAsync(command, warmCancellation.Token, "GET").AsTask();
        var warmLive = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        var canceled = connection.SendBytesAsync(command, cancellation.Token, "GET").AsTask();
        var live = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 4, deadline.Token);
        warmCancellation.Cancel();
        cancellation.Cancel();
        await Assert.That(async () => await warmCanceled.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await canceled.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();

        // Warm both branches of the measured method before counting.
        MeasureDirectCompletion(connection, warmFrame, 4, 1);
        MeasureDirectCompletion(connection, warmFrame, 4, 1);
        var abandonedBytes = AllocationMeasurement.WithoutConcurrentGc(
            () => MeasureDirectCompletion(connection, frame, header.Length, length));
        // Positive control: the same measurement observes the live head's owned copy.
        var liveBytes = AllocationMeasurement.WithoutConcurrentGc(
            () => MeasureDirectCompletion(connection, frame, header.Length, length));
        Completions(connection).Flush();
        await Assert.That(abandonedBytes).IsEqualTo(0);
        await Assert.That(liveBytes).IsGreaterThanOrEqualTo(length);
        await Assert.That((await warmLive.WaitAsync(deadline.Token))![0]).IsEqualTo((byte)'w');
        await Assert.That((await live.WaitAsync(deadline.Token))!.Length).IsEqualTo(length);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureDirectCompletion(RespireConnection connection, byte[] frame, int headerEnd, long length)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        var completed = TryCompleteBulkDirect(connection, frame, headerEnd, length, out var frameEnd);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (!completed || frameEnd != frame.Length)
            throw new InvalidOperationException("Direct completion did not consume the frame.");
        return allocated;
    }

    // Called while the receive loop is parked on an empty socket, so the test thread is the
    // ring's only consumer, as the receive loop would be.
    private static bool TryCompleteBulkDirect(
        RespireConnection connection, ReadOnlySpan<byte> buffer, int headerEnd, long length, out int frameEnd)
        => TryCompleteBulkDirect(connection, buffer, RespireDataType.BulkString, length, headerEnd, out frameEnd);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "TryCompleteBulkDirect")]
    private static extern bool TryCompleteBulkDirect(
        RespireConnection connection, ReadOnlySpan<byte> buffer, RespireDataType type, long payloadLength, int headerEnd, out int frameEnd);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_completions")]
    private static extern ref CompletionScheduler Completions(RespireConnection connection);

    private static byte[] Pattern(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    [Test]
    public async Task NullEmptyAndErrorPreserveReplyOrder()
    {
        await using var server = new FakeRespServer("$-1\r\n"u8.ToArray(), "$0\r\n\r\n"u8.ToArray(), "-ERR failed\r\n"u8.ToArray(), "$1\r\nx\r\n"u8.ToArray());
        await using var connection = await Connect(server);
        var command = new Cmd1(Verbs.Get, "key");
        await Assert.That(await connection.SendBytesAsync(command, commandName: "GET")).IsNull();
        await Assert.That((await connection.SendBytesAsync(command, commandName: "GET"))!.Length).IsEqualTo(0);
        await Assert.That(async () => await connection.SendBytesAsync(command, commandName: "GET")).Throws<RespireServerException>();
        await Assert.That((await connection.SendBytesAsync(command, commandName: "GET"))![0]).IsEqualTo((byte)'x');
    }

    [Test]
    public async Task CanceledLargeReplyIsDrainedBeforeNextReply()
    {
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();
        var command = new Cmd1(Verbs.Get, "key");
        var canceled = connection.SendBytesAsync(command, cancellation.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync("$8192\r\nx"u8.ToArray());
        cancellation.Cancel();
        await Assert.That(async () => await canceled.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        var next = connection.SendBytesAsync(command, deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 2, deadline.Token);
        await server.SendRawAsync([.. new byte[8191], 13, 10, .. "$1\r\nz\r\n"u8]);
        await Assert.That((await next.WaitAsync(deadline.Token))![0]).IsEqualTo((byte)'z');
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TruncatedOrMalformedLargeReplyFailsWithoutPublishingArray(bool malformed)
    {
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await Connect(server);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        if (malformed) await server.SendRawAsync([.. "$8192\r\n"u8, .. new byte[8192], 0, 0]);
        else
        {
            await server.SendRawAsync("$8192\r\nx"u8.ToArray());
            await server.DisposeAsync();
        }
        await Assert.That(async () => await pending.WaitAsync(deadline.Token)).Throws<RespireException>();
    }

    [Test]
    [NotInParallel] // Source selection depends on process-wide telemetry listeners.
    [Arguments(0, false)]
    [Arguments(1, false)]
    [Arguments(2, false)]
    [Arguments(3, false)]
    [Arguments(0, true)]
    [Arguments(1, true)]
    [Arguments(2, true)]
    [Arguments(3, true)]
    public async Task PublicGetPathsPreserveCallerOwnership(int mode, bool cached)
    {
        var expected = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        byte[] reply = [.. "$8192\r\n"u8, .. expected, 13, 10];
        await using var server = new FakeRespServer
        {
            SuppressReply = command => command.StartsWith("GET ", StringComparison.Ordinal),
            ReplyOverride = (_, command) => command.StartsWith("GET ", StringComparison.Ordinal)
                ? reply : command == "HELLO 3" ? "%1\r\n$5\r\nproto\r\n:3\r\n"u8.ToArray() : FakeRespServer.OkReply,
        };
#pragma warning disable RESP010 // Test-owned client isolates cached and uncached byte-array ownership on the scripted connection.
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Protocol = RespireProtocol.Resp2,
            Endpoints = { new RespireEndpoint("127.0.0.1", server.Port) },
            Connections = 1,
            ClientSideCache = cached ? new() : null,
        });
#pragma warning restore RESP010
        var view = client.WithKeyPrefix("tenant:");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = Read(view, mode, deadline.Token).AsTask();
        while (!server.ReceivedCommands.Contains("GET tenant:key")) await Task.Delay(1, deadline.Token);
        var ring = Inflight(client.Core.Multiplexer.GetConnection());
        await Assert.That(ring.TryPeek(out var head)).IsTrue();
        await Assert.That(head is BytesPendingResponseSource).IsEqualTo(!cached && !RespireTelemetry.IsOperationEnabled("GET"));
        await server.SendRawAsync(reply);
        var first = await pending.WaitAsync(deadline.Token);
        await Assert.That(first!.AsSpan().SequenceEqual(expected)).IsTrue();
        first![0] ^= 0xff;
        var secondPending = Read(view, mode, deadline.Token).AsTask();
        if (!cached)
        {
            while (server.ReceivedCommands.Count(command => command == "GET tenant:key") < 2)
                await Task.Delay(1, deadline.Token);
            await server.SendRawAsync(reply);
        }
        var second = await secondPending.WaitAsync(deadline.Token);
        await Assert.That(second!.AsSpan().SequenceEqual(expected)).IsTrue();
        await Assert.That(server.ReceivedCommands.Count(command => command == "GET tenant:key"))
            .IsEqualTo(cached ? 1 : 2);
    }

    [Test]
    public async Task LargePushPayloadDoesNotConsumePendingByteReply()
    {
        var pushed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new FakeRespServer { SuppressReply = _ => true };
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespireProtocol.Resp2,
            PushHandler = (in RespValue value) => pushed.TrySetResult(value.AsArray()[1].AsSpan().Length),
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), deadline.Token, "GET").AsTask();
        await WaitForCommands(server, 1, deadline.Token);
        await server.SendRawAsync([.. ">2\r\n+notice\r\n$8192\r\n"u8, .. new byte[8192], 13, 10, .. "$1\r\nz\r\n"u8]);
        await Assert.That(await pushed.Task.WaitAsync(deadline.Token)).IsEqualTo(8192);
        await Assert.That((await pending.WaitAsync(deadline.Token))![0]).IsEqualTo((byte)'z');
    }

    [Test]
    [Arguments("+hello\r\n")]
    [Arguments("=9\r\ntxt:hello\r\n")]
    public async Task OtherStringShapesRetainConversion(string frame)
    {
        await using var server = new FakeRespServer(Encoding.ASCII.GetBytes(frame));
        await using var connection = await Connect(server);
        var result = await connection.SendBytesAsync(new Cmd1(Verbs.Get, "key"), commandName: "GET");
        await Assert.That(result!.AsSpan().SequenceEqual("hello"u8)).IsTrue();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inflight")]
    private static extern ref InflightRing Inflight(RespireConnection connection);

    private static ValueTask<byte[]?> Read(IRespireClient client, int mode, CancellationToken token)
        => mode switch
        {
            0 => client.GetBytesAsync("key", token),
            1 => client.GetAsync<byte[]>("key", token),
            2 => client.Strings.GetBytesAsync("key", token),
            _ => client.Strings.GetAsync<byte[]>("key", token),
        };

    private static Task<RespireConnection> Connect(FakeRespServer server)
        => RespireConnection.ConnectAsync("127.0.0.1", server.Port, new() { Protocol = RespireProtocol.Resp2 });

    private static async Task WaitForCommands(FakeRespServer server, int count, CancellationToken token)
    {
        while (server.CommandsSeen < count) await Task.Delay(1, token);
    }
}
