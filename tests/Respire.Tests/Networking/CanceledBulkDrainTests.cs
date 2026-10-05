using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class CanceledBulkDrainTests
{
    private const int ReceiveSize = 4096;
    private const int PayloadSize = 1024 * 1024;

    [Test]
    [Arguments(0, false, false)]
    [Arguments(1, false, false)]
    [Arguments(2, false, false)]
    [Arguments(0, true, false)]
    [Arguments(1, true, false)]
    [Arguments(2, true, false)]
    [Arguments(0, false, true)]
    [Arguments(1, false, true)]
    [Arguments(2, false, true)]
    [Arguments(0, true, true)]
    [Arguments(1, true, true)]
    [Arguments(2, true, true)]
    public async Task CanceledPayloadUsesReceiveBufferAndPreservesFollowingReply(int mode, bool duringPayload, bool attributes)
        => await CheckDrain(mode, duringPayload, attributes, termination: 0);

    [Test]
    [Arguments(0, false, 1)]
    [Arguments(1, false, 1)]
    [Arguments(0, true, 1)]
    [Arguments(1, true, 1)]
    [Arguments(0, false, 2)]
    [Arguments(1, false, 2)]
    [Arguments(0, true, 2)]
    [Arguments(1, true, 2)]
    [Arguments(0, false, 3)]
    [Arguments(1, false, 3)]
    [Arguments(0, true, 3)]
    [Arguments(1, true, 3)]
    public async Task CanceledPayloadStillRejectsMalformedOrTruncatedFrames(int mode, bool duringPayload, int termination)
        => await CheckDrain(mode, duringPayload, attributes: true, termination);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CanceledHeadDoesNotDiscardNestedPushErrorOrVerbatimPayloads(int shape)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        var pushed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ScriptedStream();
        await using var connection = await Connect(stream,
            (in RespValue value) => pushed.TrySetResult(value.AsArray()[1].AsSpan().Length));
        var command = new Cmd1(Verbs.Get, "key");
        var canceled = Read(connection, command, 0, cancellation.Token);
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        await stream.NextRead(deadline.Token);
        cancellation.Cancel();
        await AssertCanceled(canceled, deadline.Token);
        var prefix = shape switch { 0 => "*1\r\n$", 1 => ">2\r\n+notice\r\n$", 2 => "!", _ => "=" };
        stream.Publish(Encoding.ASCII.GetBytes($"{prefix}{PayloadSize}\r\n"));
        var request = await stream.NextRead(deadline.Token);
        await Assert.That(request.BackingCapacity >= PayloadSize).IsTrue();
        var chunk = new byte[ReceiveSize];
        "txt:"u8.CopyTo(chunk);
        var remaining = PayloadSize;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, Math.Min(chunk.Length, request.Length));
            stream.Publish(chunk.AsMemory(0, count));
            remaining -= count;
            request = await stream.NextRead(deadline.Token);
        }
        // A push does not consume the canceled command's FIFO slot.
        stream.Publish(shape == 1 ? "\r\n:0\r\n:42\r\n"u8.ToArray() : "\r\n:42\r\n"u8.ToArray());
        using var reply = await following.WaitAsync(deadline.Token);
        await Assert.That(reply.AsInteger()).IsEqualTo(42);
        if (shape == 1) await Assert.That(await pushed.Task.WaitAsync(deadline.Token)).IsEqualTo(PayloadSize);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanceledPayloadStillEnforcesResponseSizeIncludingAttributes(bool attributes)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        var stream = new ScriptedStream();
        await using var connection = await Connect(stream);
        var command = new Cmd1(Verbs.Get, "key");
        var canceled = Read(connection, command, 0, cancellation.Token);
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        await stream.NextRead(deadline.Token);
        cancellation.Cancel();
        await AssertCanceled(canceled, deadline.Token);
        var metadata = attributes ? "|1\r\n+meta\r\n+value\r\n" : "";
        // Payload alone fits the limit; framing and preceding attributes make it oversized.
        stream.Publish(Encoding.ASCII.GetBytes(metadata + "$536870912\r\n"));
        var error = await Assert.That(async () => { using var reply = await following.WaitAsync(deadline.Token); })
            .Throws<RespireProtocolException>();
        await Assert.That(error!.Message).Contains("byte limit");
    }

    private static async Task CheckDrain(int mode, bool duringPayload, bool attributes, int termination)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = new CancellationTokenSource();
        var stream = new ScriptedStream();
        await using var connection = await Connect(stream);
        var command = new Cmd1(Verbs.Get, "key");
        var canceled = Read(connection, command, mode, cancellation.Token);
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        var receiveCapacity = (await stream.NextRead(deadline.Token)).BackingCapacity;
        if (!duringPayload)
        {
            cancellation.Cancel();
            await AssertCanceled(canceled, deadline.Token);
        }
        if (attributes)
        {
            stream.Publish("|1\r\n+meta\r\n+value\r\n"u8.ToArray());
            await stream.NextRead(deadline.Token);
        }
        // Split the header as well as the payload, independently of socket coalescing.
        stream.Publish("$"u8.ToArray());
        await stream.NextRead(deadline.Token);
        stream.Publish(Encoding.ASCII.GetBytes($"{PayloadSize}\r\n"));
        var request = await stream.NextRead(deadline.Token);
        var remaining = PayloadSize;
        var chunk = new byte[ReceiveSize];
        if (duringPayload)
        {
            // An active I/O retains the original allocation until that read completes.
            // This is also the positive control for backing-array capacity observations.
            await Assert.That(request.BackingCapacity >= PayloadSize).IsTrue();
            cancellation.Cancel();
            await AssertCanceled(canceled, deadline.Token);
            stream.Publish(chunk);
            remaining -= chunk.Length;
            request = await stream.NextRead(deadline.Token);
        }
        while (remaining > 0)
        {
            await Assert.That(request.BackingCapacity).IsEqualTo(receiveCapacity);
            var count = Math.Min(remaining, Math.Min(chunk.Length, request.Length));
            stream.Publish(chunk.AsMemory(0, count));
            remaining -= count;
            request = await stream.NextRead(deadline.Token);
            if (termination == 3 && remaining <= PayloadSize / 2) break;
        }
        if (termination is 2 or 3)
            stream.EndInput();
        else if (termination == 1)
            stream.Publish("\rX:42\r\n"u8.ToArray());
        else
        {
            stream.Publish("\r"u8.ToArray());
            await stream.NextRead(deadline.Token);
            stream.Publish("\n:42\r\n"u8.ToArray());
        }

        if (termination == 0)
        {
            using var reply = await following.WaitAsync(deadline.Token);
            await Assert.That(reply.AsInteger()).IsEqualTo(42);
            await Assert.That(connection.IsConnected).IsTrue();
        }
        else
            await Assert.That(async () => { using var reply = await following.WaitAsync(deadline.Token); })
                .Throws<RespireException>();
    }

    private static async Task AssertCanceled(Task pending, CancellationToken deadline)
        => await Assert.That(async () => await pending.WaitAsync(deadline)).Throws<OperationCanceledException>();

    private static async Task Read(RespireConnection connection, Cmd1 command, int mode, CancellationToken token)
    {
        if (mode == 0) await connection.SendBytesAsync(command, token, "GET");
        else if (mode == 2) await connection.SendStringAsync(command, token);
        else { using var value = await connection.SendAsync(command, token); }
    }

    private static Task<RespireConnection> Connect(ScriptedStream stream, RespirePushHandler? pushHandler = null) => RespireConnection.ConnectAsync("scripted", 6379,
        new RespireConnectionOptions
        {
            Protocol = RespProtocol.Resp2,
            ReceiveBufferSize = ReceiveSize,
            PushHandler = pushHandler,
            TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
        });

    private readonly record struct ReadRequest(int Length, int BackingCapacity);

    private sealed class ScriptedStream : Stream
    {
        private readonly Channel<ReadOnlyMemory<byte>> _segments = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<ReadRequest> _reads = Channel.CreateUnbounded<ReadRequest>();
        private ReadOnlyMemory<byte> _remaining;

        internal void Publish(ReadOnlyMemory<byte> bytes) => _segments.Writer.TryWrite(bytes);
        internal void EndInput() => _segments.Writer.TryComplete();
        internal async Task<ReadRequest> NextRead(CancellationToken token) => await _reads.Reader.ReadAsync(token);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer, out var array))
                throw new InvalidOperationException("An array-backed receive buffer was expected.");
            _reads.Writer.TryWrite(new(buffer.Length, array.Array!.Length));
            if (_remaining.IsEmpty)
            {
                try { _remaining = await _segments.Reader.ReadAsync(cancellationToken); }
                catch (ChannelClosedException) { return 0; }
            }
            var count = Math.Min(buffer.Length, _remaining.Length);
            _remaining[..count].CopyTo(buffer);
            _remaining = _remaining[count..];
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
        protected override void Dispose(bool disposing) { EndInput(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
