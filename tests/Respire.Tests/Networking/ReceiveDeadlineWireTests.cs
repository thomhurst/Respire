using System.Threading.Channels;
using Respire.Commands;
using Respire.Networking;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

public class ReceiveDeadlineWireTests
{
    [Test]
    public async Task ReplyBeforeWriteCompletionDoesNotHideTheNextPendingDeadline()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new EarlyReplyStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2, ResponseTimeout = TimeSpan.FromMilliseconds(50),
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await Assert.That(await stream.NextWriteAsync(deadline.Token)).IsEqualTo(1);
        stream.Publish(":1\r\n"u8.ToArray());
        using var reply = await first.WaitAsync(deadline.Token);
        await Assert.That(reply.AsInteger()).IsEqualTo(1L);
        await Assert.That(stream.FirstWriteCompleted).IsFalse();
        stream.CompleteFirstWrite();

        var next = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await Assert.That(await stream.NextWriteAsync(deadline.Token)).IsEqualTo(2);
        // No second reply is published: only the actual watchdog can complete it.
        var error = await Assert.That(async () => await next.WaitAsync(deadline.Token))
            .Throws<RespireConnectionException>();
        await Assert.That(error!.Message).Contains("received no data");
        await Assert.That(connection.IsConnected).IsFalse();
    }

    private sealed class EarlyReplyStream : Stream
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<int> _writes = Channel.CreateUnbounded<int>();
        private readonly TaskCompletionSource _firstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        public bool FirstWriteCompleted => _firstWrite.Task.IsCompleted;
        public void CompleteFirstWrite() => _firstWrite.TrySetResult();
        public void Publish(byte[] frame) => _replies.Writer.TryWrite(frame);
        public ValueTask<int> NextWriteAsync(CancellationToken token) => _writes.Reader.ReadAsync(token);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var number = Interlocked.Increment(ref _writeCount);
            _writes.Writer.TryWrite(number);
            return number == 1 ? new ValueTask(_firstWrite.Task) : ValueTask.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                var frame = await _replies.Reader.ReadAsync(cancellationToken);
                frame.CopyTo(buffer);
                return frame.Length;
            }
            catch (ChannelClosedException) { return 0; }
        }

        protected override void Dispose(bool disposing)
        {
            _firstWrite.TrySetException(new ObjectDisposedException(nameof(EarlyReplyStream)));
            _replies.Writer.TryComplete();
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
