using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Respire;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class StreamedSetTests
{
    private const int MaximumStreamingBufferCapacity = 256 * 1024;

    private static byte[] PatternedPayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        return payload;
    }

    [Test]
    public async Task SetStreamSendsFiftyMegabytesWithBoundedBufferMemory()
    {
        const int length = 50 * 1024 * 1024;
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var source = new GeneratedStream(length + 1);
        var command = new StreamedSetCommand((RespireValue)"large", source, length, default, SetWhen.Always);
        using var response = await connection.SendCheckedAsync(in command, commandName: "SET");

        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(source.Position).IsEqualTo(length);
        await Assert.That(server.ValueLength).IsEqualTo(length);
        await Assert.That(source.MaximumReadSize).IsLessThanOrEqualTo(RespireConnection.StreamChunkSize);
        // Far below the payload size. The bound allows for the shared write-buffer pool handing
        // out an array up to two buckets larger than requested while parallel tests return buffers.
        await Assert.That(connection.WriteBufferCapacity).IsLessThanOrEqualTo(MaximumStreamingBufferCapacity);
        await Assert.That(response.AsString()).IsEqualTo("OK");
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET" });
    }

    [Test]
    public async Task SetReadOnlySequenceWritesEverySegment()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        var first = new BufferSegment(new byte[] { 1, 2 });
        var last = first.Append(new byte[] { 3, 4, 5 });
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        await Assert.That(await client.Strings.SetAsync("sequence", sequence)).IsTrue();
        await Assert.That(server.ValueLength).IsEqualTo(5);
        await Assert.That(server.SmallValue).IsEquivalentTo(new byte[] { 1, 2, 3, 4, 5 });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task StalledStreamedSetDoesNotBlockOrAbortMultiplexedCommands(bool useCluster)
    {
        var pong = "+PONG\r\n"u8.ToArray();
        FakeRespServer? server = null;
        server = new FakeRespServer(2, FakeRespServer.OkReply)
        {
            ReplyOverride = (_, command) => command switch
            {
                "PING" => pong,
                "CLUSTER SLOTS" => Encoding.ASCII.GetBytes(
                    $"*1\r\n*3\r\n:0\r\n:16383\r\n*2\r\n$9\r\n127.0.0.1\r\n:{server!.Port}\r\n"),
                _ => null,
            },
        };
        await using var client = await RespireClient.ConnectAsync(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            UseCluster = useCluster,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        const int length = RespireConnection.StreamChunkSize + 1;
        using var cancellation = new CancellationTokenSource();
        var source = new PartialThenBlockedStream(new byte[length], RespireConnection.StreamChunkSize,
            RespireConnection.StreamChunkSize);
        var upload = client.Strings.SetAsync("stalled", source, length, cancellationToken: cancellation.Token).AsTask();
        await source.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
            cancellation.Cancel();
            try { await upload; }
            catch (Exception) { }
            throw;
        }

        cancellation.Cancel();
        var error = await Assert.That(async () => await upload).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await client.PingAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var expectedCommands = useCluster
            ? new[] { "CLUSTER SLOTS", "PING", "PING" }
            : new[] { "PING", "PING" };
        await Assert.That(server.ReceivedCommands).IsEquivalentTo(expectedCommands);
        await Assert.That(server.ReceivedConnectionIds.Distinct().Count()).IsEqualTo(1);
    }

    [Test]
    public async Task SetReadOnlySequenceStreamsLargeSegmentsWithBoundedBufferMemory()
    {
        const int length = 4 * 1024 * 1024;
        const int largeSegment = 3 * 1024 * 1024;
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        // One 3 MiB segment (split into chunks) followed by many small segments (coalesced).
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index % 251);
        var first = new BufferSegment(payload.AsMemory(0, largeSegment));
        var last = first;
        for (var offset = largeSegment; offset < length; offset += 1000)
            last = last.Append(payload.AsMemory(offset, Math.Min(1000, length - offset)));
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        var command = new StreamedSetCommand((RespireValue)"large-sequence", sequence, default, SetWhen.Always);

        using var response = await connection.SendCheckedAsync(in command, commandName: "SET");

        await Assert.That(response.AsString()).IsEqualTo("OK");
        await Assert.That(server.ValueLength).IsEqualTo(length);
        // Far below the payload size. The bound allows for the shared write-buffer pool handing
        // out an array up to two buckets larger than requested while parallel tests return buffers.
        await Assert.That(connection.WriteBufferCapacity).IsLessThanOrEqualTo(MaximumStreamingBufferCapacity);
    }

    [Test]
    public async Task SetReadOnlySequenceHandlesEmptySegmentsAndEmptyPayload()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        var first = new BufferSegment(Array.Empty<byte>());
        var last = first.Append(new byte[] { 1, 2 }).Append(Array.Empty<byte>()).Append(new byte[] { 3 })
            .Append(Array.Empty<byte>());
        var sequence = new ReadOnlySequence<byte>(first, 0, last, 0);
        await Assert.That(await client.Strings.SetAsync("gaps", sequence)).IsTrue();
        await Assert.That(server.SmallValue).IsEquivalentTo(new byte[] { 1, 2, 3 });

        await Assert.That(await client.Strings.SetAsync("empty", ReadOnlySequence<byte>.Empty)).IsTrue();
        await Assert.That(server.ValueLength).IsEqualTo(0);
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET", "SET" });
    }

    [Test]
    public async Task SmallSourceReadsAreCoalescedWithoutCorruptingThePayload()
    {
        const int length = 200_000;
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        // Odd-sized reads exercise partial chunk fills; the server verifies every payload byte.
        var source = new GeneratedStream(length, maximumRead: 7);
        var command = new StreamedSetCommand((RespireValue)"trickle", source, length, default, SetWhen.Always);

        using var response = await connection.SendCheckedAsync(in command, commandName: "SET");

        await Assert.That(response.AsString()).IsEqualTo("OK");
        await Assert.That(server.ValueLength).IsEqualTo(length);
        await Assert.That(source.Position).IsEqualTo(length);
    }

    [Test]
    public async Task NextSourceChunkStartsReadingWhileCurrentChunkWriteIsBlocked()
    {
        const int length = RespireConnection.StreamChunkSize * 2;
        await using var server = new CountingSetServer();
        GatedWriteStream? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return transport = new GatedWriteStream(client);
            },
        });
        var source = new ReadSignalStream(length);
        transport!.GateSecondWrite();
        var command = new StreamedSetCommand((RespireValue)"overlap", source, length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();

        await transport.SecondWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await source.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(send.IsCompleted).IsFalse();

        transport.OpenSecondWrite();
        using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(response.AsString()).IsEqualTo("OK");
        await Assert.That(server.ValueLength).IsEqualTo(length);
    }

    [Test]
    public async Task FailedSocketWriteKeepsPendingSourceBufferUntilReadCompletes()
    {
        const int length = RespireConnection.StreamChunkSize * 2;
        await using var server = new CountingSetServer();
        GatedWriteStream? transport = null;
        var pool = new TrackingArrayPool();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            StreamPayloadPool = pool,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return transport = new GatedWriteStream(client);
            },
        });
        var source = new PartialThenIgnoringCancellationStream(PatternedPayload(length),
            maxRead: RespireConnection.StreamChunkSize, pauseAfter: RespireConnection.StreamChunkSize);
        transport!.GateSecondWrite();
        using var cancellation = new CancellationTokenSource();
        var command = new StreamedSetCommand((RespireValue)"overlap-cancel", source, length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, cancellationToken: cancellation.Token, commandName: "SET").AsTask();

        try
        {
            await transport.SecondWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await source.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            await Assert.That(pool.Returned.Contains(source.CapturedBuffer!)).IsFalse();
        }
        finally
        {
            source.ContinueReading.TrySetResult();
            try { await send.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }

        await source.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pool.WaitForReturnAsync(source.CapturedBuffer!).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pool.Returned.Contains(source.CapturedBuffer!)).IsTrue();
    }

    [Test]
    public async Task PrefetchReadFailureSurfacesAfterCurrentWriteCompletes()
    {
        const int length = RespireConnection.StreamChunkSize * 2;
        await using var server = new CountingSetServer();
        GatedWriteStream? transport = null;
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return transport = new GatedWriteStream(client);
            },
        });
        var source = new FailingSecondReadStream(length);
        transport!.GateSecondWrite();
        var command = new StreamedSetCommand((RespireValue)"failed-prefetch", source, length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();

        try
        {
            await transport.SecondWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await source.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(send.IsCompleted).IsFalse();
            transport.OpenSecondWrite();
            await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<IOException>();
        }
        finally
        {
            transport.OpenSecondWrite();
            try { await send.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
    }

    [Test]
    public async Task DisposeKeepsPartialChunkBufferPooledOutUntilFillTaskSettles()
    {
        const int length = RespireConnection.StreamChunkSize * 2;
        await using var server = new CountingSetServer();
        GatedWriteStream? transport = null;
        var source = new ShortReadThenBlockedStream();
        var pool = new TrackingArrayPool();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            StreamPayloadPool = pool,
            TestingStreamFactory = async (host, port, cancellationToken) =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return transport = new GatedWriteStream(client);
            },
        });
        transport!.GateSecondWrite();
        using var cancellation = new CancellationTokenSource();
        var command = new StreamedSetCommand((RespireValue)"partial-prefetch", source, length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, cancellationToken: cancellation.Token, commandName: "SET").AsTask();
        try
        {
            await transport.SecondWriteStarted.WaitAsync(TimeSpan.FromSeconds(5));
            await source.ThirdReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            var pendingBuffer = source.CapturedBuffer!;
            await Assert.That(pool.Returned.Contains(pendingBuffer)).IsFalse();
        }
        finally
        {
            source.ContinueThirdRead.TrySetResult();
            transport.OpenSecondWrite();
            try { await send.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        await pool.WaitForReturnAsync(source.CapturedBuffer!).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pool.Returned.Contains(source.CapturedBuffer!)).IsTrue();
    }

    [Test]
    public async Task SurplusSourceBytesAreLeftUnread()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        using var source = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

        await Assert.That(await client.Strings.SetAsync("prefix", source, 4)).IsTrue();

        await Assert.That(source.Position).IsEqualTo(4);
        await Assert.That(server.SmallValue).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
    }

    [Test]
    public async Task SeekableStreamWithUnsupportedLengthFallsBackToStreaming()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        using var source = new UnknownLengthSeekableStream(new byte[] { 7, 8, 9 });

        await Assert.That(await client.Strings.SetAsync("wrapped", source, 3)).IsTrue();

        await Assert.That(server.SmallValue).IsEquivalentTo(new byte[] { 7, 8, 9 });
    }

    [Test]
    public async Task UndefinedSetWhenIsRejectedBeforeAnyFrameIsSent()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        const SetWhen undefined = (SetWhen)3;
        using var source = new MemoryStream(new byte[] { 1 });
        RespireValue value = "v";

        await Assert.That(async () => await client.Strings.SetAsync("stream", source, 1, when: undefined))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Strings.SetAsync(
                "sequence", new ReadOnlySequence<byte>(new byte[] { 1 }), when: undefined))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(async () => await client.Strings.SetAsync("value", value, when: undefined))
            .Throws<ArgumentOutOfRangeException>();
        await client.PingAsync();

        await Assert.That(source.Position).IsEqualTo(0);
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task UndefinedSetWhenCannotDesynchronizeTheConnection()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        // Bypasses facet validation to prove the command itself never writes a short array.
        var command = new StreamedSetCommand(
            (RespireValue)"bad", new MemoryStream(new byte[] { 1 }), 1, default, (SetWhen)3);

        await Assert.That(async () => await connection.SendCheckedAsync(in command, commandName: "SET"))
            .Throws<ArgumentOutOfRangeException>();
        using var ping = await connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING");

        await Assert.That(ping.AsString()).IsEqualTo("PONG");
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task WarmClientStreamsSetPayloads()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        await client.PingAsync();
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.That(await client.Strings.SetAsync("stream", source, 3)).IsTrue();

        var first = new BufferSegment(new byte[] { 4, 5 });
        var last = first.Append(new byte[] { 6 });
        var sequence = new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
        await Assert.That(await client.Strings.SetAsync("sequence", sequence)).IsTrue();

        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING", "SET", "SET" });
    }

    [Test]
    public async Task HeaderSerializationFailureDoesNotCorruptConnection()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        using var keyMemory = new ThrowOnAccessMemoryManager();
        var command = new StreamedSetCommand(
            new RespireKey(keyMemory.Memory), new MemoryStream([1]), 1, default, SetWhen.Always);

        await Assert.That(async () => await connection.SendCheckedAsync(in command, commandName: "SET"))
            .ThrowsExactly<InvalidOperationException>();
        using var ping = await connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING");

        await Assert.That(ping.AsString()).IsEqualTo("PONG");
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task StreamedSetSupportsCommandTimeoutsLongerThanTimerRange()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromDays(60),
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        using var source = new MemoryStream(new byte[] { 1, 2, 3 });

        await Assert.That(await client.Strings.SetAsync("long-timeout", source, 3)).IsTrue();
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET" });
    }

    [Test]
    public async Task EarlyEndOfStreamAfterFirstChunkClosesConnectionBeforeAnotherFrameCanFollow()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        // The first chunk is sent, so the frame is open on the wire when the source ends.
        var source = new GeneratedStream(RespireConnection.StreamChunkSize + 50);
        await Assert.That(async () => await client.Strings.SetAsync("partial", source, RespireConnection.StreamChunkSize + 100))
            .Throws<EndOfStreamException>();
        await server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
    }

    [Test]
    public async Task EarlyEndOfStreamWithinFirstChunkLeavesConnectionOpen()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        // The first chunk is read before the header is queued, so nothing reaches the wire.
        var source = new GeneratedStream(50);
        await Assert.That(async () => await client.Strings.SetAsync("partial", source, 100))
            .Throws<EndOfStreamException>();
        await client.PingAsync();
        await Assert.That(server.ConnectionClosed.IsCompleted).IsFalse();
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task ShortSeekableStreamIsRejectedBeforeAnyFrameIsSent()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        var source = new MemoryStream(new byte[50]) { Position = 10 };
        await Assert.That(async () => await client.Strings.SetAsync("short", source, 41))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(source.Position).IsEqualTo(10);
        await client.PingAsync();
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
    }

    [Test]
    public async Task EmptySetCanUseSeekableStreamPositionedAtEnd()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        var source = new MemoryStream([1, 2]) { Position = 2 };

        await Assert.That(await client.Strings.SetAsync("empty", source, 0)).IsTrue();

        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET" });
        await Assert.That(server.ValueLength).IsEqualTo(0);
    }

    [Test]
    public async Task StreamingGateWaitTimeoutReportsCommandTimeout()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            CommandTimeout = TimeSpan.FromMilliseconds(200),
        });
        var holder = new PausedStream();
        var first = new StreamedSetCommand((RespireValue)"first", holder, 4, default, SetWhen.Always);
        var firstSet = connection.SendAsync(in first, armCommandDeadline: false, commandName: "SET").AsTask();
        await holder.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = new StreamedSetCommand(
            (RespireValue)"second", new MemoryStream(new byte[4]), 4, default, SetWhen.Always);
        var error = await Assert.That(async () => await connection.SendCheckedAsync(in second, commandName: "SET"))
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.WaitingForCapacity);

        holder.ContinueReading.TrySetResult();
        using var reply = await firstSet;
        await Assert.That(reply.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task CallerCancellationWhileWaitingForStreamingGatePreservesToken()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var holder = new PausedStream();
        var first = new StreamedSetCommand((RespireValue)"first", holder, 4, default, SetWhen.Always);
        var firstSet = connection.SendAsync(in first, armCommandDeadline: false, commandName: "SET").AsTask();
        await holder.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var cancellation = new CancellationTokenSource();
        var second = new StreamedSetCommand(
            (RespireValue)"second", new MemoryStream(new byte[4]), 4, default, SetWhen.Always);
        var secondSet = connection.SendCheckedAsync(in second, commandName: "SET", cancellationToken: cancellation.Token).AsTask();
        cancellation.Cancel();
        var error = await Assert.That(async () => await secondSet).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        // Nothing of the second frame was written, so cancelling it must not close the connection.
        await Assert.That(connection.IsConnected).IsTrue();

        holder.ContinueReading.TrySetResult();
        using var reply = await firstSet;
        await Assert.That(reply.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task LocalRetirementDuringFirstChunkRejectsAndRestoresSource()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var source = new PausedStream();
        var command = new StreamedSetCommand((RespireValue)"retiring", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Cluster node retirement is local retirement. The upload has no header on the wire, so it
        // must be rejected for the router to retry on the new owner, not drained to this node.
        var retirement = connection.RetireAsync();
        await Task.Delay(100);
        await Assert.That(retirement.IsCompleted).IsFalse(); // Waits for the read that owns the write path.

        source.ContinueReading.TrySetResult();
        await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
        await Assert.That(server.Commands).IsEmpty();
        var replayed = new byte[4];
        await command.SourceStream!.ReadExactlyAsync(replayed);
        await Assert.That(replayed).IsEquivalentTo("data"u8.ToArray());
    }

    [Test]
    public async Task RetirementWakesStreamedSetQueuedBehindActiveStream()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var activeSource = new PausedStream();
        var activeCommand = new StreamedSetCommand((RespireValue)"active", activeSource, 4, default, SetWhen.Always);
        var activeSet = connection.SendCheckedAsync(in activeCommand, commandName: "SET").AsTask();
        await activeSource.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var queuedSource = new PausedStream();
        var queuedCommand = new StreamedSetCommand((RespireValue)"queued", queuedSource, 4, default, SetWhen.Always);
        var queuedSet = connection.SendCheckedAsync(in queuedCommand, commandName: "SET").AsTask();
        await Task.Delay(50);
        await Assert.That(queuedSet.IsCompleted).IsFalse();

        var retirement = connection.RetireAsync();
        await Assert.That(async () => await queuedSet.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();
        await Assert.That(queuedSource.ReadStarted.Task.IsCompleted).IsFalse();

        // The active upload was still reading its first chunk, so it is rejected for a retry too.
        activeSource.ContinueReading.TrySetResult();
        await Assert.That(async () => await activeSet.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.Commands).IsEmpty();
    }

    [Test]
    public async Task CancellationDuringPayloadWriteClosesConnection()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        using var cancellation = new CancellationTokenSource();
        // The first chunk is sent before the source pauses, so the frame is open on the wire.
        var source = new PausedStream(prefix: RespireConnection.StreamChunkSize);
        var set = client.Strings.SetAsync("cancel", source, source.Length, cancellationToken: cancellation.Token).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await set).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
    }

    [Test]
    public async Task CancellationDuringSynchronouslyBlockedPrefetchReturnsAndRetainsBufferUntilReadSettles()
    {
        await using var server = new CountingSetServer();
        var pool = new TrackingArrayPool();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new RespireConnectionOptions
        {
            Protocol = RespProtocol.Resp2,
            StreamPayloadPool = pool,
        });
        using var cancellation = new CancellationTokenSource();
        using var source = new SynchronouslyBlockedSecondReadStream();
        var command = new StreamedSetCommand((RespireValue)"sync-block", source, source.Length, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET", cancellationToken: cancellation.Token).AsTask();

        byte[]? buffer = null;
        try
        {
            await source.SecondReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
                .Throws<OperationCanceledException>();
            buffer = source.CapturedBuffer!;
            await Assert.That(pool.Returned.Contains(buffer)).IsFalse();
        }
        finally
        {
            source.ContinueSecondRead.Set();
        }
        if (buffer is null) return;
        await source.SecondReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pool.WaitForReturnAsync(buffer).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(pool.Returned.Contains(buffer)).IsTrue();
    }

    [Test]
    public async Task CancellationDuringFirstChunkReadLeavesConnectionOpen()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });

        using var cancellation = new CancellationTokenSource();
        var source = new PausedStream();
        var set = client.Strings.SetAsync("cancel", source, 4, cancellationToken: cancellation.Token).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await set).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);

        await client.PingAsync();
        await Assert.That(server.ConnectionClosed.IsCompleted).IsFalse();
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TokenlessSourceCancellationIsClassifiedFromLinkedSources(bool timeout)
    {
        CountingSetServer? server = new();
        try
        {
            await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = timeout ? TimeSpan.FromMilliseconds(200) : null,
            });
            // The source sees the linked token cancelled during a read and may report it with an
            // exception that carries CancellationToken.None, which streams are allowed to do.
            var source = new TokenlessCancellationStream();
            var command = new StreamedSetCommand((RespireValue)"tokenless", source, 4, default, SetWhen.Always);
            var set = Task.Run(async () =>
            {
                using var _ = await connection.SendCheckedAsync(in command, commandName: "SET");
            });
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!timeout)
            {
                await server.DisposeAsync();
                server = null;
            }

            if (timeout)
            {
                var error = await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Throws<RespireTimeoutException>();
                await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Writing);
                // Nothing was written, so the timeout did not need to close the connection.
                await Assert.That(connection.IsConnected).IsTrue();
            }
            else
            {
                await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
                    .Throws<RespireConnectionException>();
            }

            // WaitAsync may return before the background source read observes cancellation.
            await source.TokenlessCancellationThrown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (server is not null) await server.DisposeAsync();
        }
    }

    [Test]
    public async Task FinalFrameWriteCompletedBeforeObservedCancellationIsNotAFailure()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            // Completing the write queues WaitAsync's continuation; the inline cancellation then
            // usually wins, which is the race where the frame is already on the socket.
            var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = new CancellationTokenSource();
            var wait = RespireConnection.WaitForFinalFrameWriteAsync(write.Task, cancellation.Token).AsTask();
            write.SetResult();
            cancellation.Cancel();
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Test]
    public async Task FinalFrameWriteStillPendingAtCancellationThrows()
    {
        var write = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var wait = RespireConnection.WaitForFinalFrameWriteAsync(write.Task, cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.That(async () => await wait.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ConcurrentCommandRunsWhileStreamedSetIsStalled()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        var source = new PausedStream();
        var set = client.Strings.SetAsync("ordered", source, 4).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ping = client.PingAsync().AsTask();
        await ping.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.Commands).IsEquivalentTo(["PING"]);

        source.ContinueReading.TrySetResult();
        await Assert.That(await set).IsTrue();
        await Assert.That(server.Commands).IsEquivalentTo(["PING", "SET"]);
    }

    [Test]
    public async Task ConnectionCloseWhileReadingSourceFailsStreamedSet()
    {
        var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var source = new PausedStream();
        var command = new StreamedSetCommand((RespireValue)"orphaned", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // No command timeout is configured, so only the connection abort can end the source read.
        await server.DisposeAsync();
        await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AbortDoesNotRunSourceCancellationCallbacksInline(bool blockCallback)
    {
        CountingSetServer? server = new();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var source = new CancellationCallbackStream(blockCallback);
        var command = new StreamedSetCommand((RespireValue)"callback", source, 1, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        try
        {
            await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await server.DisposeAsync();
            server = null;
            await Assert.That(async () => { using var _ = await set.WaitAsync(TimeSpan.FromSeconds(5)); })
                .Throws<RespireConnectionException>();
            await source.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(connection.IsConnected).IsFalse();
        }
        finally
        {
            source.ReleaseCallback.TrySetResult();
            if (server is not null) await server.DisposeAsync();
            await source.DisposeAsync();
        }
        await source.CallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task NonCooperativeSourceReadCannotOutlastCommandTimeout()
    {
        await using var server = new CountingSetServer();
        await using var client = RespireClient.Create(new RespireOptions
        {
            Endpoints = { new("127.0.0.1", server.Port) },
            Protocol = RespProtocol.Resp2,
            Connections = 1,
            CommandTimeout = TimeSpan.FromMilliseconds(250),
            ThreadPoolMonitoring = false,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        var source = new NonCooperativeStream();
        var set = client.Strings.SetAsync("stalled-source", source, 1).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(3)))
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Writing);
        // The timeout leaves the dedicated upload lease healthy; ordinary traffic stays available.
        await client.PingAsync();

        // Finish the ignored read so its rented buffer can be returned safely.
        source.CompleteRead.TrySetResult();
        await source.ReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task StalledPeerWriteHonorsCommandTimeout()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = TimeSpan.FromMilliseconds(500),
            });
        // The peer never reads, so socket buffers fill and a payload write stalls indefinitely.
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var command = new StreamedSetCommand(
            (RespireValue)"stalled", new GeneratedStream(int.MaxValue), int.MaxValue, default, SetWhen.Always);

        var error = await Assert.That(async () => await connection.SendCheckedAsync(in command, commandName: "SET")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10)))
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Writing);
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    public async Task RetirementRejectsStreamedSetQueuedBehindStalledEarlierWrite()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        // The transport write is gated by the test, so the earlier write stalls on every OS.
        // Kernel socket buffers are not reliable for this: Windows loopback absorbs megabytes,
        // so a large frame there never stalls and the SET is not queued behind it.
        GatedWriteStream? transport = null;
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
                TestingStreamFactory = async (host, port, cancellationToken) =>
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(host, port, cancellationToken);
                    return transport = new GatedWriteStream(client);
                },
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        await using var peerStream = new NetworkStream(peer, ownsSocket: false);

        transport!.CloseGate();
        var blocker = connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING").AsTask();
        await transport.WriteStarted.WaitAsync(TimeSpan.FromSeconds(5));

        var source = new PausedStream();
        var command = new StreamedSetCommand((RespireValue)"queued", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await Task.Delay(100);
        // Precondition: the streamed SET owns the write path and waits for the stalled write.
        await Assert.That(set.IsCompleted).IsFalse();
        await Assert.That(source.ReadStarted.Task.IsCompleted).IsFalse();

        // Retirement rejects the upload without waiting for the stalled frame or reading the
        // source, so a router can retry it on the replacement with an untouched source.
        var retirement = connection.RetireAsync();
        await Assert.That(async () => await set.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();
        await Assert.That(source.ReadStarted.Task.IsCompleted).IsFalse();

        // The earlier, accepted frame still drains.
        transport.OpenGate();
        using var peerTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await peerStream.ReadExactlyAsync(new byte[14], peerTimeout.Token); // "*1\r\n$4\r\nPING\r\n"
        await peerStream.WriteAsync("+PONG\r\n"u8.ToArray(), peerTimeout.Token);
        using var blockerReply = await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(blockerReply.AsString()).IsEqualTo("PONG");
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
    }

    [Test]
    public async Task SourceReadThatIgnoresTheDeadlineDoesNotQueueTheHeader()
    {
        await using var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
            CommandTimeout = TimeSpan.FromMilliseconds(100),
        });
        // The read blocks synchronously past the deadline and then succeeds, so the completed
        // read task wins over the cancelled token. The header must still not be queued.
        var source = new SynchronouslyBlockingStream(TimeSpan.FromMilliseconds(500));
        var command = new StreamedSetCommand((RespireValue)"late", source, 4, default, SetWhen.Always);

        var error = await Assert.That(async () => await connection.SendCheckedAsync(in command, commandName: "SET")
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireTimeoutException>();
        await Assert.That(error!.Diagnostics.Stage).IsEqualTo(RespireCommandStage.Writing);
        await Assert.That(connection.IsConnected).IsTrue();
        // This PING only verifies the untouched connection. Its wait must not inherit
        // the deliberately tiny deadline used to reject the blocked source read.
        using var ping = await connection.SendCheckedAsync(new Cmd(new Verb("PING")), commandName: "PING",
            commandDeadline: CommandDeadline.After(5_000));
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "PING" });
    }

    [Test]
    public async Task RetirementDuringFirstChunkRestoresSourceAndWritesNoFrame()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var generation = new TestConnectionGeneration();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
                Generation = generation,
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var source = new PausedStream();
        var command = new StreamedSetCommand((RespireValue)"retry", source, 4, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        generation.IsRetired = true;
        source.ContinueReading.TrySetResult();
        await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();

        var replayed = new byte[4];
        await command.SourceStream!.ReadExactlyAsync(replayed);
        await Assert.That(replayed).IsEquivalentTo("data"u8.ToArray());

        // Nothing reached the retired connection's peer: no SET header or payload bytes. The
        // connection is still open, so readability within the window could only mean data.
        await Assert.That(peer.Poll(TimeSpan.FromMilliseconds(500), SelectMode.SelectRead)).IsFalse();
        await Assert.That(peer.Available).IsEqualTo(0);
    }

    [Test]
    public async Task RetirementCancellingPartialFirstChunkReadDoesNotRetryUnknownPosition()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var generation = new TestConnectionGeneration();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
                Generation = generation,
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var source = new PartialThenBlockedStream(payload, maxRead: 6, pauseAfter: 12);
        var command = new StreamedSetCommand((RespireValue)"retry", source, payload.Length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        generation.IsRetired = true;
        _ = connection.RetireAsync();
        await connection.DisposeAsync(); // The retired socket's close cancels the blocked third read.
        await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(source.Position).IsEqualTo(12);
        await Assert.That(peer.Available).IsEqualTo(0);
    }

    [Test]
    public async Task RetirementWaitsForPendingFirstChunkReadBeforeRetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var generation = new TestConnectionGeneration();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
                Generation = generation,
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var source = new PartialThenIgnoringCancellationStream(payload, maxRead: 6, pauseAfter: 12);
        var command = new StreamedSetCommand((RespireValue)"retry", source, payload.Length, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));

        generation.IsRetired = true;
        _ = connection.RetireAsync();
        await connection.DisposeAsync();
        await Task.Delay(50);
        await Assert.That(send.IsCompleted).IsFalse();
        source.ContinueReading.TrySetResult();
        await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();

        var replayed = new byte[payload.Length];
        await command.SourceStream!.ReadExactlyAsync(replayed);
        await Assert.That(replayed).IsEquivalentTo(payload);
        await Assert.That(peer.Available).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RetirementSettlesWholeFirstFillBeforeRestoringPrefix(bool synchronousRead)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var generation = new TestConnectionGeneration();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2, CommandTimeout = null, Generation = generation,
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        using var source = new GatedFirstReadStream(synchronousRead);
        var command = new StreamedSetCommand("retry", source, 4, default, SetWhen.Always);
        var send = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            generation.IsRetired = true;
            _ = connection.RetireAsync();
            await connection.DisposeAsync();
            await Task.Delay(50);
            await Assert.That(send.IsCompleted).IsFalse();
        }
        finally { source.Release.TrySetResult(); }
        await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionRetiredException>();

        var replay = new byte[4];
        await command.SourceStream!.ReadExactlyAsync(replay);
        await Assert.That(replay).IsEquivalentTo(new byte[] { 1, 2, 3, 4 });
        await Assert.That(peer.Available).IsEqualTo(0);
    }

    private sealed class GatedFirstReadStream(bool synchronousRead) : MemoryStream(new byte[] { 1, 2, 3, 4 })
    {
        private bool _first = true;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_first) return base.ReadAsync(buffer, cancellationToken);
            _first = false;
            Entered.TrySetResult();
            if (!synchronousRead) return ReadAfterReleaseAsync(buffer);
            Release.Task.GetAwaiter().GetResult();
            return base.ReadAsync(buffer[..2], CancellationToken.None);
        }
        private async ValueTask<int> ReadAfterReleaseAsync(Memory<byte> buffer)
        {
            await Release.Task;
            return await base.ReadAsync(buffer[..2], CancellationToken.None);
        }
    }

    [Test]
    public async Task SynchronousReadAsyncRetirementWithUnknownPositionDoesNotRetry()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        var generation = new TestConnectionGeneration();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
                Generation = generation,
            });
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        var payload = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var source = new PartialThenRetirementThrowingStream(payload, maxRead: 6, pauseAfter: 12);
        var command = new StreamedSetCommand((RespireValue)"retry", source, payload.Length, default, SetWhen.Always);
        var send = Task.Run(() => connection.SendCheckedAsync(in command, commandName: "SET").AsTask());
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        generation.IsRetired = true;
        _ = connection.RetireAsync();
        await connection.DisposeAsync();
        source.ContinueReading.TrySetResult();
        await Assert.That(async () => await send.WaitAsync(TimeSpan.FromSeconds(5)))
            .Throws<RespireConnectionException>();
        await Assert.That(source.Position).IsEqualTo(12);
        await Assert.That(peer.Available).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingSourceClosesConnectionAndClientRecovers()
    {
        await using var server = new FakeRespServer(2, "+OK\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "PING" ? "+PONG\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        // The source fails after the first chunk is on the wire, so the frame must be abandoned.
        var source = new ThrowingStream(RespireConnection.StreamChunkSize);
        await Assert.That(async () => await client.Strings.SetAsync("broken", source, RespireConnection.StreamChunkSize + 4))
            .Throws<IOException>();
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));

        // The partial frame died with its connection; the client reconnects for later commands.
        using var reconnected = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.Core.Multiplexer.GetHealthyConnectionAsync(reconnected.Token);
        await client.PingAsync(reconnected.Token);
        await Assert.That(server.ReceivedCommands.Contains("PING")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET"))).IsFalse();
    }

    [Test]
    public async Task ThrowingSourceWithinFirstChunkLeavesConnectionOpen()
    {
        await using var server = new FakeRespServer(1, "+OK\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "PING" ? "+PONG\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Strings.SetAsync("broken", new ThrowingStream(), 4))
            .Throws<IOException>();

        await client.PingAsync();
        await Assert.That(server.PeerClosed.IsCompleted).IsFalse();
        await Assert.That(server.ReceivedCommands.Contains("PING")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET"))).IsFalse();
    }

    private sealed class UnknownLengthSeekableStream(byte[] bytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException("Length is unknown.");
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = Math.Min(buffer.Length, bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream(int bytesBeforeFailure = 0) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read >= bytesBeforeFailure)
                return ValueTask.FromException<int>(new IOException("Source failed mid-frame."));
            var count = Math.Min(buffer.Length, bytesBeforeFailure - _read);
            buffer.Span[..count].Fill((byte)'x');
            _read += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GeneratedStream(int length, int maximumRead = int.MaxValue) : Stream
    {
        private int _position;
        private int _maximumReadSize;
        internal int MaximumReadSize => Volatile.Read(ref _maximumReadSize);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateMaximum(ref _maximumReadSize, buffer.Length);
            var count = Math.Min(Math.Min(buffer.Length, maximumRead), length - _position);
            var output = buffer.Span[..count];
            for (var index = 0; index < count; index++) output[index] = (byte)((_position + index) % 251);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static void UpdateMaximum(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var previous = Interlocked.CompareExchange(ref target, value, current);
                if (previous == current) return;
                current = previous;
            }
        }
    }

    // Serves `prefix` generated bytes immediately, then pauses before the final four bytes.
    private sealed class GatedWriteStream(TcpClient client) : Stream
    {
        private readonly NetworkStream _inner = client.GetStream();
        private readonly TaskCompletionSource _writeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _secondWriteGate = CreateOpenGate();
        private TaskCompletionSource _gate = CreateOpenGate();
        private int _writeCount;

        internal Task WriteStarted => _writeStarted.Task;
        internal Task SecondWriteStarted => _secondWriteStarted.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        internal void CloseGate() => _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void OpenGate() => _gate.TrySetResult();

        internal void GateSecondWrite() => _secondWriteGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void OpenSecondWrite() => _secondWriteGate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 2)
            {
                _secondWriteStarted.TrySetResult();
                await _secondWriteGate.Task.WaitAsync(cancellationToken);
            }
            _writeStarted.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken);
            await _inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                OpenGate();
                OpenSecondWrite();
                _inner.Dispose();
                client.Dispose();
            }
            base.Dispose(disposing);
        }

        private static TaskCompletionSource CreateOpenGate()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.TrySetResult();
            return gate;
        }
    }

    private sealed class ReadSignalStream(int length) : Stream
    {
        private int _position;
        private int _reads;
        internal TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 2) SecondReadStarted.TrySetResult();
            var count = Math.Min(buffer.Length, length - _position);
            for (var index = 0; index < count; index++) buffer.Span[index] = (byte)((_position + index) % 251);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FailingSecondReadStream(int length) : Stream
    {
        private int _readCount;
        internal TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                SecondReadStarted.TrySetResult();
                return ValueTask.FromException<int>(new IOException("Prefetched source read failed."));
            }

            for (var index = 0; index < buffer.Length; index++) buffer.Span[index] = (byte)(index % 251);
            return ValueTask.FromResult(buffer.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TrackingArrayPool : ArrayPool<byte>
    {
        internal ConcurrentBag<byte[]> Returned { get; } = [];
        private readonly ConcurrentDictionary<byte[], TaskCompletionSource> _returnSignals = new(ReferenceEqualityComparer.Instance);

        internal Task WaitForReturnAsync(byte[] buffer)
        {
            var signal = _returnSignals.GetOrAdd(buffer, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Returned.Contains(buffer)) signal.TrySetResult();
            return signal.Task;
        }

        public override byte[] Rent(int minimumLength) => new byte[minimumLength];

        public override void Return(byte[] array, bool clearArray = false)
        {
            Returned.Add(array);
            _returnSignals.GetOrAdd(array, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        }
    }

    private sealed class SynchronouslyBlockedSecondReadStream : Stream
    {
        private int _readCount;
        private int _position;
        internal ManualResetEventSlim ContinueSecondRead { get; } = new();
        internal TaskCompletionSource SecondReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? CapturedBuffer { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => RespireConnection.StreamChunkSize * 2L;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _readCount) == 2)
            {
                MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment);
                CapturedBuffer = segment.Array;
                SecondReadStarted.TrySetResult();
                ContinueSecondRead.Wait();
                Fill(buffer.Span);
                SecondReadCompleted.TrySetResult();
                return ValueTask.FromResult(buffer.Length);
            }
            Fill(buffer.Span);
            return ValueTask.FromResult(buffer.Length);
        }

        private void Fill(Span<byte> buffer)
        {
            for (var index = 0; index < buffer.Length; index++) buffer[index] = (byte)((_position + index) % 251);
            _position += buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ShortReadThenBlockedStream : Stream
    {
        private int _readCount;
        internal byte[]? CapturedBuffer { get; private set; }
        internal TaskCompletionSource ThirdReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueThirdRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => RespireConnection.StreamChunkSize * 2L;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment)) CapturedBuffer = segment.Array;
            switch (Interlocked.Increment(ref _readCount))
            {
                case 1:
                    buffer.Span.Fill((byte)'a');
                    return buffer.Length;
                case 2:
                    buffer.Span[0] = (byte)'b';
                    return 1;
                case 3:
                    ThirdReadStarted.TrySetResult();
                    await ContinueThirdRead.Task.ConfigureAwait(false);
                    buffer.Span[0] = (byte)'c';
                    return 1;
                default:
                    buffer.Span.Fill((byte)'d');
                    return buffer.Length;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SynchronouslyBlockingStream(TimeSpan delay) : Stream
    {
        private bool _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        // Ignores the token and completes synchronously after the delay.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return ValueTask.FromResult(0);
            _read = true;
            Thread.Sleep(delay);
            "late"u8.CopyTo(buffer.Span);
            return ValueTask.FromResult(4);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PausedStream(int prefix = 0) : Stream
    {
        private int _read;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueReading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix + 4;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read < prefix)
            {
                var count = Math.Min(buffer.Length, prefix - _read);
                for (var index = 0; index < count; index++) buffer.Span[index] = (byte)((_read + index) % 251);
                _read += count;
                return count;
            }

            if (_read != prefix) return 0;
            ReadStarted.TrySetResult();
            await ContinueReading.Task.WaitAsync(cancellationToken);
            "data"u8.CopyTo(buffer.Span);
            _read += 4;
            return 4;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PartialThenBlockedStream(byte[] payload, int maxRead, int pauseAfter) : Stream
    {
        private int _position;
        private int _blocked;
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= pauseAfter && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Paused.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            var count = Math.Min(Math.Min(buffer.Length, maxRead), payload.Length - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PartialThenIgnoringCancellationStream(byte[] payload, int maxRead, int pauseAfter) : Stream
    {
        private int _position;
        private int _blocked;
        internal TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueReading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal byte[]? CapturedBuffer { get; private set; }
        internal int CapturedBufferLength => CapturedBuffer?.Length ?? throw new InvalidOperationException("No source buffer captured.");
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var segment)) CapturedBuffer = segment.Array;
            if (_position >= pauseAfter && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                Paused.TrySetResult();
                await ContinueReading.Task;
            }

            var count = Math.Min(Math.Min(buffer.Length, maxRead), payload.Length - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            if (_position == payload.Length) Completed.TrySetResult();
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class PartialThenRetirementThrowingStream(byte[] payload, int maxRead, int pauseAfter) : Stream
    {
        private int _position;
        private int _blocked;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueReading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => payload.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position >= pauseAfter && Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                ReadStarted.TrySetResult();
                ContinueReading.Task.GetAwaiter().GetResult();
                throw new OperationCanceledException();
            }

            var count = Math.Min(Math.Min(buffer.Length, maxRead), payload.Length - _position);
            payload.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class TestConnectionGeneration : IConnectionGeneration
    {
        public bool IsRetired { get; set; }
        public ValueTask ValidateAsync(RespireConnection connection, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public void ObserveResponse(RespireConnection connection, string? operation, in RespValue response) { }
        public void ConnectionClosed(RespireConnection connection, bool unexpected) { }
    }

    // The read waits (blocking) until its token is cancelled, then reports cancellation without
    // including that token. No second source call is needed after cancellation.
    private sealed class TokenlessCancellationStream : Stream
    {
        private int _reads;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TokenlessCancellationThrown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ThrewTokenless { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 4;
        public override long Position { get => _reads; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 0)
            {
                ReadStarted.TrySetResult();
                SpinWait.SpinUntil(() => cancellationToken.IsCancellationRequested, TimeSpan.FromSeconds(5));
            }

            if (!cancellationToken.IsCancellationRequested) throw new InvalidOperationException("Token was not cancelled.");
            ThrewTokenless = true;
            TokenlessCancellationThrown.TrySetResult();
            return ValueTask.FromException<int>(new OperationCanceledException("Source observed cancellation."));
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonCooperativeStream : Stream
    {
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CompleteRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReadCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await CompleteRead.Task; // Deliberately ignores cancellationToken.
            buffer.Span[0] = 42;
            ReadCompleted.TrySetResult();
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CancellationCallbackStream(bool blockCallback) : Stream
    {
        private CancellationTokenRegistration _registration;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCallback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(() =>
            {
                CallbackEntered.TrySetResult();
                try
                {
                    if (blockCallback) ReleaseCallback.Task.GetAwaiter().GetResult();
                    else throw new InvalidOperationException("Test cancellation callback failure.");
                }
                finally { CallbackCompleted.TrySetResult(); }
            });
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            buffer.Span[0] = 1;
            return 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _registration.Dispose();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        internal BufferSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        internal BufferSegment Append(ReadOnlyMemory<byte> next)
        {
            var segment = new BufferSegment(next) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }

        internal BufferSegment(byte[] memory) : this((ReadOnlyMemory<byte>)memory) { }
    }

    private sealed class ThrowOnAccessMemoryManager : MemoryManager<byte>
    {
        private readonly byte[] _bytes = new byte[4];
        private int _accessCount;

        public override Span<byte> GetSpan()
            => Interlocked.Increment(ref _accessCount) == 1
                ? _bytes
                : throw new InvalidOperationException("Test memory access failure.");
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private sealed class CountingSetServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _runner;
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly ConcurrentQueue<string> _commands = new();
        private readonly TaskCompletionSource _connectionClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _smallValue;
        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int ValueLength { get; private set; }
        internal byte[]? SmallValue => _smallValue;
        internal string[] Commands => _commands.ToArray();
        internal Task ConnectionClosed => _connectionClosed.Task;

        internal CountingSetServer()
        {
            _listener.Start();
            _runner = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    _connections.Add(HandleConnectionAsync(socket));
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (SocketException) when (_stop.IsCancellationRequested) { }

            await Task.WhenAll(_connections.ToArray());
        }

        private async Task HandleConnectionAsync(Socket socket)
        {
            try
            {
                using (socket)
                await using (var stream = new NetworkStream(socket, ownsSocket: false))
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var marker = await ReadByteAsync(stream, _stop.Token);
                        if (marker < 0)
                        {
                            _connectionClosed.TrySetResult();
                            return;
                        }
                        if (marker != '*') throw new InvalidDataException("Expected RESP array header.");
                        var count = int.Parse(await ReadLineAsync(stream, _stop.Token));
                        string? command = null;
                        for (var index = 0; index < count; index++)
                        {
                            if (await ReadByteAsync(stream, _stop.Token) != '$') throw new InvalidDataException("Expected bulk argument.");
                            var length = int.Parse(await ReadLineAsync(stream, _stop.Token));
                            if (index == 0)
                            {
                                var name = new byte[length];
                                await stream.ReadExactlyAsync(name, _stop.Token);
                                command = Encoding.ASCII.GetString(name);
                            }
                            else if (command == "SET" && index == 2)
                            {
                                ValueLength = length;
                                if (length <= 1024) _smallValue = new byte[length];
                                var scratch = new byte[16 * 1024];
                                var consumed = 0;
                                while (consumed < length)
                                {
                                    var read = Math.Min(scratch.Length, length - consumed);
                                    await stream.ReadExactlyAsync(scratch.AsMemory(0, read), _stop.Token);
                                    if (_smallValue is not null) scratch.AsSpan(0, read).CopyTo(_smallValue.AsSpan(consumed));
                                    if (_smallValue is null)
                                        for (var i = 0; i < read; i++)
                                            if (scratch[i] != (byte)((consumed + i) % 251))
                                                throw new InvalidDataException("Stream payload bytes changed during transmission.");
                                    consumed += read;
                                }
                            }
                            else
                            {
                                var argument = new byte[length];
                                await stream.ReadExactlyAsync(argument, _stop.Token);
                            }

                            var crlf = new byte[2];
                            await stream.ReadExactlyAsync(crlf, _stop.Token);
                            if (crlf[0] != '\r' || crlf[1] != '\n') throw new InvalidDataException("Invalid bulk terminator.");
                        }

                        _commands.Enqueue(command ?? "");
                        var reply = command == "PING" ? "+PONG\r\n"u8.ToArray() : "+OK\r\n"u8.ToArray();
                        await stream.WriteAsync(reply, _stop.Token);
                    }
                }
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or SocketException)
            {
                _connectionClosed.TrySetResult();
            }
        }

        private static async ValueTask<int> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var one = new byte[1];
            var read = await stream.ReadAsync(one, cancellationToken);
            return read == 0 ? -1 : one[0];
        }

        private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
        {
            var line = new ArrayBufferWriter<byte>();
            while (true)
            {
                var value = await ReadByteAsync(stream, cancellationToken);
                if (value < 0) throw new EndOfStreamException();
                if (value == '\r')
                {
                    if (await ReadByteAsync(stream, cancellationToken) != '\n') throw new InvalidDataException("Invalid RESP line ending.");
                    return Encoding.ASCII.GetString(line.WrittenSpan);
                }
                line.GetSpan(1)[0] = (byte)value;
                line.Advance(1);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _runner; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            _stop.Dispose();
        }
    }
}
