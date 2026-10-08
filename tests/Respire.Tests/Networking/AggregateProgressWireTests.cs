using System.Text;
using System.Threading.Channels;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class AggregateProgressWireTests
{
    [Test]
    [Arguments("*2\r\n+saved\r\n$5\r\nhe", 0)]
    [Arguments("*2\r\n*2\r\n+saved\r\n$3\r\nold\r\n$5\r\nhe", 0)]
    [Arguments("*1\r\n*3\r\n:1\r\n:2\r\n$5\r\nhe", 0)]
    [Arguments("%2\r\n+k\r\n~2\r\n:1\r\n:2\r\n+v\r\n$5\r\nhe", 0)]
    [Arguments("|1\r\n+k\r\n:9\r\n*2\r\n+saved\r\n$5\r\nhe", 2)]
    [Arguments("*2\r\n|1\r\n+k\r\n:9\r\n+saved\r\n$5\r\nhe", 0)]
    public async Task ConnectionTransfersCompletedChildrenBeforeReceiveBufferCompaction(string prefix, int metadataScalars)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new ControlledStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ReceiveBufferSize = 1024,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var command = new Cmd1(Verbs.Get, "key");
        var earlier = connection.SendAsync(command, deadline.Token).AsTask();
        var partial = connection.SendAsync(command, deadline.Token).AsTask();
        var following = connection.SendAsync(command, deadline.Token).AsTask();
        await stream.WaitForWrittenAsync(3 * "*2\r\n$3\r\nGET\r\n$3\r\nkey\r\n"u8.Length, deadline.Token);
        await Assert.That(await stream.NextReadSizeAsync(deadline.Token)).IsEqualTo(1024);

        var padding = new string('x', 900);
        stream.Publish(Encoding.ASCII.GetBytes("+" + padding + "\r\n" + prefix));
        // Next read parks after parsing and compaction. Only the two bulk bytes remain.
        await Assert.That(await stream.NextReadSizeAsync(deadline.Token)).IsEqualTo(1022);
        using var first = await earlier.WaitAsync(deadline.Token);
        await Assert.That(first.AsString()).IsEqualTo(padding);
        await Assert.That(partial.IsCompleted).IsFalse();
#if DEBUG
        var resumedReads = connection.InspectForTests().ResumedScalarCount;
        // Top-level metadata intentionally uses resumable parsing before the reply.
        // No completed reply child should enter that scalar path again.
        await Assert.That(resumedReads).IsEqualTo(metadataScalars);
#endif
        var expectedPosition = 0;
        var expectedWire = Encoding.ASCII.GetBytes(prefix + "llo\r\n");
        var expectedStatus = RespParser.TryParseValue(expectedWire, ref expectedPosition, out var expected);
        using (expected)
        {
            await Assert.That(expectedStatus).IsEqualTo(RespParseStatus.Done);
            await Assert.That(expectedPosition).IsEqualTo(expectedWire.Length);
            var followingText = "after" + new string('y', 880);
            // This read overwrites the original prefix's receive-buffer storage.
            stream.Publish(Encoding.ASCII.GetBytes("llo\r\n+" + followingText + "\r\n"));
            using var actual = await partial.WaitAsync(deadline.Token);
            using var last = await following.WaitAsync(deadline.Token);
            await Assert.That(actual.Equals(expected)).IsTrue();
            await Assert.That(last.AsString()).IsEqualTo(followingText);
            await Assert.That(connection.IsConnected).IsTrue();
        }
    }

    private sealed class ControlledStream : Stream
    {
        private readonly Channel<byte[]> _segments = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<int> _reads = Channel.CreateUnbounded<int>();
        private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();
        private long _writtenBytes;

        public void Publish(byte[] bytes) => _segments.Writer.TryWrite(bytes);
        public async Task<int> NextReadSizeAsync(CancellationToken token) => await _reads.Reader.ReadAsync(token);
        public async Task WaitForWrittenAsync(int count, CancellationToken token)
        {
            while (Interlocked.Read(ref _writtenBytes) < count)
                await _writes.Reader.ReadAsync(token);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _reads.Writer.TryWrite(buffer.Length);
            byte[] segment;
            try { segment = await _segments.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return 0; }
            if (segment.Length > buffer.Length) throw new InvalidOperationException("Controlled segment exceeds receive window.");
            segment.CopyTo(buffer);
            return segment.Length;
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
            _reads.Writer.TryComplete();
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
