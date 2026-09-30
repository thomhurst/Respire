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

        var source = new MemoryStream(new byte[50]);
        await Assert.That(async () => await client.Strings.SetAsync("partial", source, 100))
            .Throws<EndOfStreamException>();
        await server.ConnectionClosed.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(source.CanRead).IsTrue();
        await Assert.That(server.Commands.Contains("SET")).IsFalse();
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
        await Assert.That(async () => await set).Throws<OperationCanceledException>();
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
        await Assert.That(server.Commands.TakeLast(2).ToArray()).IsEquivalentTo(new[] { "SET", "PING" });
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
