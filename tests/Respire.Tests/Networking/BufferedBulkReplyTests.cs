using System.Text;
using System.Threading.Channels;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class BufferedBulkReplyTests
{
    [Test]
    [Arguments(1024, 127)]
    [Arguments(65536, 4095)]
    [Arguments(65536, 0)]
    public async Task LargeUnreadTailAvoidsAnEarlyCopyButStillCompactsAFullBuffer(int bufferSize, int freeTail)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new SegmentedStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ReceiveBufferSize = bufferSize,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var earlier = connection.SendAsync(command, deadline.Token).AsTask();
        var partial = connection.SendAsync(command, deadline.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        var capacity = await stream.NextReadSizeAsync(deadline.Token);
        var firstFrame = "$7\r\nabcdefg\r\n"u8.ToArray();
        var payload = new string('x', capacity - freeTail - firstFrame.Length - 1);
        stream.Publish([.. firstFrame, .. Encoding.ASCII.GetBytes("+" + payload)]);
        var nextWindow = await stream.NextReadSizeAsync(deadline.Token);
        using var first = await earlier.WaitAsync(deadline.Token);
        await Assert.That(nextWindow).IsEqualTo(freeTail == 0 ? firstFrame.Length : freeTail);
        await Assert.That(first.AsSpan().SequenceEqual("abcdefg"u8)).IsTrue();
        await Assert.That(partial.IsCompleted).IsFalse();
        stream.Publish("\r\n:22\r\n"u8.ToArray());
        using var second = await partial.WaitAsync(deadline.Token);
        await Assert.That(second.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(payload))).IsTrue();
        using var last = await following.WaitAsync(deadline.Token);
        await Assert.That(last.AsInteger()).IsEqualTo(22L);
    }

    [Test]
    public async Task UnconsumedFullFrameGrowsBeforeTheNextReceive()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new SegmentedStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ReceiveBufferSize = 1024,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var pending = connection.SendAsync(command, deadline.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        var capacity = await stream.NextReadSizeAsync(deadline.Token);
        var payload = new string('x', capacity - 1);
        stream.Publish(Encoding.ASCII.GetBytes("+" + payload));
        await Assert.That(await stream.NextReadSizeAsync(deadline.Token)).IsEqualTo(capacity);
        await Assert.That(pending.IsCompleted).IsFalse();
        stream.Publish("y\r\n:22\r\n"u8.ToArray());
        using var first = await pending.WaitAsync(deadline.Token);
        await Assert.That(first.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(payload + "y"))).IsTrue();
        using var last = await following.WaitAsync(deadline.Token);
        await Assert.That(last.AsInteger()).IsEqualTo(22L);
    }

    [Test]
    [Arguments(65536, 4096, false, false, false)]
    [Arguments(65536, 4095, false, true, false)]
    [Arguments(65536, 1, false, true, false)]
    [Arguments(65536, 4096, true, false, false)]
    [Arguments(65536, 4095, true, true, false)]
    [Arguments(65536, 1, true, true, false)]
    [Arguments(1024, 128, false, false, false)]
    [Arguments(1024, 127, false, true, false)]
    [Arguments(1024, 1, true, true, false)]
    [Arguments(65536, 1, true, true, true)]
    public async Task SmallReceiveTailIsReclaimedBeforeReadingAnIncompleteReply(
        int bufferSize, int freeTail, bool nested, bool compact, bool malformed)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new SegmentedStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ReceiveBufferSize = bufferSize,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var earlier = connection.SendAsync(command, deadline.Token).AsTask();
        var partial = connection.SendAsync(command, deadline.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        var capacity = await stream.NextReadSizeAsync(deadline.Token);
        await Assert.That(capacity).IsEqualTo(bufferSize);
        var prefix = nested ? "|1\r\n+source\r\n+test\r\n*2\r\n:11\r\n$7\r\nab" : "$7\r\nab";
        var frameBudget = capacity - freeTail - prefix.Length;
        var length = frameBudget - 10;
        length = frameBudget - $"${length}\r\n".Length - 2;
        var header = $"${length}\r\n";
        await Assert.That(header.Length + length + 2).IsEqualTo(frameBudget);
        var payload = new string('x', length);
        stream.Publish(Encoding.ASCII.GetBytes($"{header}{payload}\r\n{prefix}"));
        var nextWindow = await stream.NextReadSizeAsync(deadline.Token);
        // A real outstanding read observes the destination length; no timing or TCP
        // packet assumptions determine whether compaction ran.
        using var first = await earlier.WaitAsync(deadline.Token);
        await Assert.That(nextWindow).IsEqualTo(compact ? capacity - 2 : freeTail);
        await Assert.That(first.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes(payload))).IsTrue();
        await Assert.That(partial.IsCompleted).IsFalse();
        await Assert.That(following.IsCompleted).IsFalse();
        stream.Publish(malformed ? "cdefgXX:22\r\n"u8.ToArray() : "cdefg\r\n:22\r\n"u8.ToArray());
        if (malformed)
        {
            await Assert.That(async () => await partial.WaitAsync(deadline.Token)).Throws<RespireProtocolException>();
            await Assert.That(async () => await following.WaitAsync(deadline.Token)).Throws<RespireProtocolException>();
            await Assert.That(connection.IsConnected).IsFalse();
            return;
        }
        using var second = await partial.WaitAsync(deadline.Token);
        if (nested)
        {
            await Assert.That(second.AsArray()[0].AsInteger()).IsEqualTo(11L);
            await Assert.That(second.AsArray()[1].AsSpan().SequenceEqual("abcdefg"u8)).IsTrue();
        }
        else await Assert.That(second.AsSpan().SequenceEqual("abcdefg"u8)).IsTrue();
        using var last = await following.WaitAsync(deadline.Token);
        await Assert.That(last.AsInteger()).IsEqualTo(22L);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    [Test]
    [Arguments(4096, 0)]
    [Arguments(16384, 0)]
    [Arguments(64512, 0)]
    [Arguments(4096, 1)]
    [Arguments(16384, 1)]
    [Arguments(64512, 1)]
    [Arguments(4096, 2)]
    [Arguments(16384, 2)]
    [Arguments(64512, 2)]
    public async Task CompleteAndSplitBulksPreservePipelineOrder(int length, int replyKind)
    {
        // Script exact receive boundaries; TCP writes alone cannot guarantee fragmentation.
        foreach (var missing in new[] { 0, 1, length / 2 })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stream = new SegmentedStream();
            await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
                new RespireConnectionOptions
                {
                    Protocol = RespProtocol.Resp2, ReceiveBufferSize = 64 * 1024,
                    TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
                });
            var command = new Cmd1(Verbs.Get, "key");
            var first = connection.SendAsync(command, deadline.Token).AsTask();
            var text = replyKind == 0 ? connection.SendStringAsync(command, deadline.Token).AsTask() : null;
            var general = replyKind != 0 ? connection.SendAsync(command, deadline.Token).AsTask() : null;
            var last = connection.SendAsync(command, deadline.Token).AsTask();
            await stream.NextReadAsync(deadline.Token);

            var payload = new string('x', length);
            var bulk = Encoding.ASCII.GetBytes($"${length}\r\n{payload}\r\n");
            if (replyKind == 2)
            {
                // Force the connection into its resumable aggregate parser before the bulk.
                stream.Publish(":11\r\n*1\r\n"u8.ToArray());
                await stream.NextReadAsync(deadline.Token);
            }
            var prefix = replyKind == 2 ? Array.Empty<byte>() : ":11\r\n"u8.ToArray();
            stream.Publish([.. prefix, .. bulk.AsSpan(0, bulk.Length - missing),
                .. (missing == 0 ? ":22\r\n"u8.ToArray() : Array.Empty<byte>())]);
            if (missing != 0)
            {
                await stream.NextReadAsync(deadline.Token);
                // An earlier reply must be flushed before direct-fill waits for more bytes.
                using var earlier = await first.WaitAsync(deadline.Token);
                await Assert.That(earlier.AsInteger()).IsEqualTo(11);
                await Assert.That(text?.IsCompleted ?? general!.IsCompleted).IsFalse();
                await Assert.That(last.IsCompleted).IsFalse();
                stream.Publish([.. bulk.AsSpan(bulk.Length - missing), .. ":22\r\n"u8]);
            }
            else
            {
                using var earlier = await first.WaitAsync(deadline.Token);
                await Assert.That(earlier.AsInteger()).IsEqualTo(11);
            }

            if (text is not null)
                await Assert.That(await text.WaitAsync(deadline.Token)).IsEqualTo(payload);
            else
            {
                using var value = await general!.WaitAsync(deadline.Token);
                var actual = replyKind == 2 ? value.AsArray()[0].AsString() : value.AsString();
                await Assert.That(actual).IsEqualTo(payload);
            }
            using var following = await last.WaitAsync(deadline.Token);
            await Assert.That(following.AsInteger()).IsEqualTo(22);
            await Assert.That(connection.IsConnected).IsTrue();
        }
    }

    [Test]
    public async Task ExcessiveAggregateDepthFailsRemainingFifoWithoutLosingEarlierReply()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new SegmentedStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var earlier = connection.SendAsync(command, deadline.Token).AsTask();
        var malformed = connection.SendAsync(command, deadline.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        await stream.NextReadAsync(deadline.Token);
        // Retain a real pooled child before the later depth error exercises parser cleanup.
        stream.Publish(":11\r\n*2\r\n$7\r\npayload\r\n"u8.ToArray());
        await stream.NextReadAsync(deadline.Token);
        using var first = await earlier.WaitAsync(deadline.Token);
        await Assert.That(first.AsInteger()).IsEqualTo(11);
        stream.Publish(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("*1\r\n", 512)) + "*0\r\n:22\r\n"));
        await Assert.That(async () => await malformed.WaitAsync(deadline.Token)).Throws<RespireProtocolException>();
        await Assert.That(async () => await following.WaitAsync(deadline.Token)).Throws<RespireProtocolException>();
        await Assert.That(connection.IsConnected).IsFalse();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CanceledAggregateDrainsAcrossGrowthBeforeFollowingReply(bool cancelBeforeHeader)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var canceled = new CancellationTokenSource();
        var stream = new SegmentedStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var abandoned = connection.SendAsync(command, canceled.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        // Cancellation must abandon a submitted response, not remove an unsent command.
        await stream.WaitForWrittenAsync(2 * "*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8.Length, deadline.Token);
        await stream.NextReadAsync(deadline.Token);
        if (cancelBeforeHeader) canceled.Cancel();
        stream.Publish(Encoding.ASCII.GetBytes("*33\r\n" + string.Concat(Enumerable.Repeat(":7\r\n", 16))));
        await stream.NextReadAsync(deadline.Token);
        if (!cancelBeforeHeader) canceled.Cancel();
        await Assert.That(async () => await abandoned.WaitAsync(deadline.Token)).Throws<OperationCanceledException>();
        await Assert.That(following.IsCompleted).IsFalse();
        stream.Publish(Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(":7\r\n", 17)) + ":99\r\n"));
        using var reply = await following.WaitAsync(deadline.Token);
        await Assert.That(reply.AsInteger()).IsEqualTo(99);
        await Assert.That(connection.IsConnected).IsTrue();
    }

    private sealed class SegmentedStream : Stream
    {
        private readonly Channel<ReadOnlyMemory<byte>> _segments = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();
        private long _writtenBytes;
        private ReadOnlyMemory<byte> _remaining;

        public void Publish(byte[] bytes) => _segments.Writer.TryWrite(bytes);
        public async Task NextReadAsync(CancellationToken token) => await NextReadSizeAsync(token);
        public async Task<int> NextReadSizeAsync(CancellationToken token) => await _reads.Reader.ReadAsync(token);
        public async Task WaitForWrittenAsync(int count, CancellationToken token)
        {
            while (Interlocked.Read(ref _writtenBytes) < count)
                await _writes.Reader.ReadAsync(token);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(buffer.Length);
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
        {
            Interlocked.Add(ref _writtenBytes, buffer.Length);
            _writes.Writer.TryWrite(true);
            return ValueTask.CompletedTask;
        }
        protected override void Dispose(bool disposing)
        {
            _segments.Writer.TryComplete();
            _writes.Writer.TryComplete();
            base.Dispose(disposing);
        }
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
