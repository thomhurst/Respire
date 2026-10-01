using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Respire;
using Respire.Commands;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public sealed class StreamedSetTests
{
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
        await Assert.That(source.MaximumReadSize).IsLessThanOrEqualTo(32 * 1024);
        await Assert.That(connection.WriteBufferCapacity).IsLessThanOrEqualTo(64 * 1024);
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
    public async Task EarlyEndOfStreamClosesConnectionBeforeAnotherFrameCanFollow()
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

        var source = new GeneratedStream(50);
        await Assert.That(async () => await client.Strings.SetAsync("partial", source, 100))
            .Throws<EndOfStreamException>();
        await server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
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

        holder.ContinueReading.TrySetResult();
        using var reply = await firstSet;
        await Assert.That(reply.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task RetirementDrainsAcceptedStreamedSet()
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

        var retirement = connection.RetireAsync();
        await Task.Delay(100);
        await Assert.That(retirement.IsCompleted).IsFalse();

        source.ContinueReading.TrySetResult();
        using var reply = await set.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("OK");
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.DrainedSuccessfully).IsTrue();
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET" });
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

        activeSource.ContinueReading.TrySetResult();
        using var reply = await activeSet.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(reply.AsString()).IsEqualTo("OK");
        await Assert.That(server.Commands).IsEquivalentTo(new[] { "SET" });
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
        var source = new PausedStream();
        var set = client.Strings.SetAsync("cancel", source, 4, cancellationToken: cancellation.Token).AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var error = await Assert.That(async () => await set).Throws<OperationCanceledException>();
        await Assert.That(error!.CancellationToken).IsEqualTo(cancellation.Token);
        await server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
    }

    [Test]
    public async Task ConcurrentCommandWaitsUntilStreamedFrameCompletes()
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
        await Task.Delay(100);
        await Assert.That(server.Commands.Contains("PING")).IsFalse();

        source.ContinueReading.TrySetResult();
        await Assert.That(await set).IsTrue();
        await ping;
        await Assert.That(server.Commands.TakeLast(2).SequenceEqual(new[] { "SET", "PING" })).IsTrue();
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
        var server = new CountingSetServer();
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port, new()
        {
            Protocol = RespProtocol.Resp2,
        });
        var source = new CancellationCallbackStream(blockCallback);
        var command = new StreamedSetCommand((RespireValue)"callback", source, 1, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await server.DisposeAsync();
        await Assert.That(async () => { using var _ = await set.WaitAsync(TimeSpan.FromSeconds(5)); })
            .Throws<RespireConnectionException>();
        await source.CallbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(connection.IsConnected).IsFalse();

        source.ReleaseCallback.TrySetResult();
        await source.CallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await source.DisposeAsync();
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
        await Assert.That(client.IsConnected).IsFalse();

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
    public async Task RetirementDrainsStreamedSetQueuedBehindStalledEarlierWrite()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptSocketAsync();
        await using var connection = await RespireConnection.ConnectAsync(
            "127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, new()
            {
                Protocol = RespProtocol.Resp2,
                CommandTimeout = null,
            });
        // The peer holds an earlier large frame until retirement, then accepts both frames.
        using var peer = await accept.WaitAsync(TimeSpan.FromSeconds(5));
        await using var peerStream = new NetworkStream(peer, ownsSocket: false);
        const int payload = 32 * 1024 * 1024;
        var header = Encoding.ASCII.GetBytes($"*3\r\n$3\r\nSET\r\n$7\r\nblocker\r\n${payload}\r\n");
        var frame = new byte[header.Length + payload + 2];
        header.CopyTo(frame, 0);
        "\r\n"u8.CopyTo(frame.AsSpan(frame.Length - 2));
        var blocker = connection.SendAsync(new RawCommand(frame)).AsTask();

        var source = new PausedStream();
        var command = new StreamedSetCommand((RespireValue)"queued", source, 4, default, SetWhen.Always);
        var set = connection.SendCheckedAsync(in command, commandName: "SET").AsTask();
        await Task.Delay(200);
        await Assert.That(set.IsCompleted).IsFalse();

        var retirement = connection.RetireAsync();
        await peerStream.ReadExactlyAsync(frame);
        await peerStream.WriteAsync("+OK\r\n"u8.ToArray());
        await source.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.ContinueReading.TrySetResult();
        await peerStream.ReadExactlyAsync("*3\r\n$3\r\nSET\r\n$6\r\nqueued\r\n$4\r\n"u8.ToArray());
        await peerStream.ReadExactlyAsync(new byte[6]); // "data\r\n"
        await peerStream.WriteAsync("+OK\r\n"u8.ToArray());

        using var blockerReply = await blocker.WaitAsync(TimeSpan.FromSeconds(5));
        using var streamedReply = await set.WaitAsync(TimeSpan.FromSeconds(5));
        await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(blockerReply.AsString()).IsEqualTo("OK");
        await Assert.That(streamedReply.AsString()).IsEqualTo("OK");
    }

    [Test]
    public async Task ThrowingSourceClosesConnectionAndClientRecovers()
    {
        await using var server = new FakeRespServer(2, "+OK\r\n"u8.ToArray())
        {
            ReplyOverride = (_, command) => command == "PING" ? "+PONG\r\n"u8.ToArray() : null,
        };
        await using var client = await FakeRespServer.ConnectClientAsync(server.Port);

        await Assert.That(async () => await client.Strings.SetAsync("broken", new ThrowingStream(), 4))
            .Throws<IOException>();
        await server.PeerClosed.WaitAsync(TimeSpan.FromSeconds(5));

        // The partial frame died with its connection; the client reconnects for later commands.
        using var reconnected = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.Core.Multiplexer.GetHealthyConnectionAsync(reconnected.Token);
        await client.PingAsync(reconnected.Token);
        await Assert.That(server.ReceivedCommands.Contains("PING")).IsTrue();
        await Assert.That(server.ReceivedCommands.Any(command => command.StartsWith("SET"))).IsFalse();
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Source failed mid-frame."));
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class GeneratedStream(int length) : Stream
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
            var count = Math.Min(buffer.Length, length - _position);
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

    private sealed class PausedStream : Stream
    {
        private int _read;
        internal TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ContinueReading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 4;
        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read != 0) return 0;
            ReadStarted.TrySetResult();
            await ContinueReading.Task.WaitAsync(cancellationToken);
            "data"u8.CopyTo(buffer.Span);
            _read = 4;
            return 4;
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
                using var socket = await _listener.AcceptSocketAsync(_stop.Token);
                await using var stream = new NetworkStream(socket, ownsSocket: false);
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
