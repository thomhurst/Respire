using System.Threading.Channels;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

/// <summary>
/// Commands written from threads outside the pool dispatch the flush wake to the pool instead
/// of sending inline; they must still be sent and answered.
/// </summary>
[Category(TestCategories.ConstrainedRetirement)]
public class FlushWakeTests
{
    [Test]
    public async Task LaterProducerFlushesWhenFirstProducerPausesAfterPublication()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Wait for the setup precondition; publication/release below controls the race itself.
        while (!connection.IsFlushLoopWaiting)
            await Task.Delay(1, deadline.Token);

        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var first = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var producer = new Thread(() =>
        {
            try
            {
                using var reply = connection.SendAsync(new PausedProducer(published, release))
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                first.TrySetResult(reply.AsString());
            }
            catch (Exception error) { first.TrySetException(error); }
        }) { IsBackground = true };
        producer.Start();
        try
        {
            await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // The second command did not make the buffer non-empty, but must still wake it.
            using var reply = await connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(reply.AsString()).IsEqualTo("PONG");
            await Assert.That(first.Task.IsCompleted).IsFalse();
        }
        finally
        {
            release.Set();
            await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            producer.Join(TimeSpan.FromSeconds(10));
        }
        await Assert.That(await first.Task.WaitAsync(TimeSpan.FromSeconds(5))).IsEqualTo("PONG");
    }

    private readonly struct PausedProducer(TaskCompletionSource published, ManualResetEventSlim release) : IRespCommand
    {
        public ReadCommandKind ReadKind => ReadCommandKind.None;
        public void Write(ref RespWriter writer) => writer.WriteRaw(FakeRespServer.PingFrame);
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken)
        {
            // This callback runs after the write gate is released and before ScheduleFlush.
            published.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Producer was not released.");
            return admissionToken;
        }
    }

    [Test]
    public async Task CommandWrittenDuringASendIsFlushedByTheLoopsRecheck()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var stream = new HeldWriteStream();
        await using var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var first = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await stream.FirstWriteStarted.WaitAsync(deadline.Token);

        // The flush loop is parked inside the first send. This append is covered by the
        // loop's re-check after that send, so it leaves no wake behind.
        var second = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame), deadline.Token).AsTask();
        await Assert.That(connection.InspectForTests().FlushSignalPending).IsFalse();
        await Assert.That(connection.IsFlushLoopWaiting).IsFalse();
        await Assert.That(stream.WrittenBytes).IsEqualTo(FakeRespServer.PingFrame.Length);

        // No other producer runs: only the re-check can send the second command.
        stream.ReleaseFirstWrite();
        await stream.WaitForWrittenAsync(2 * FakeRespServer.PingFrame.Length, deadline.Token);
        stream.Publish([.. FakeRespServer.PongReply, .. FakeRespServer.PongReply]);
        using var firstReply = await first.WaitAsync(deadline.Token);
        using var secondReply = await second.WaitAsync(deadline.Token);
        await Assert.That(firstReply.AsString()).IsEqualTo("PONG");
        await Assert.That(secondReply.AsString()).IsEqualTo("PONG");
    }

    /// <summary>Holds the first write open until released; replies come from <see cref="Publish"/>.</summary>
    private sealed class HeldWriteStream : Stream
    {
        private readonly Channel<byte[]> _segments = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<bool> _writes = Channel.CreateUnbounded<bool>();
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        private long _writtenBytes;

        public Task FirstWriteStarted => _firstWriteStarted.Task;
        public long WrittenBytes => Interlocked.Read(ref _writtenBytes);
        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();
        public void Publish(byte[] bytes) => _segments.Writer.TryWrite(bytes);

        public async Task WaitForWrittenAsync(int count, CancellationToken token)
        {
            while (WrittenBytes < count)
                await _writes.Reader.ReadAsync(token);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] segment;
            try { segment = await _segments.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return 0; }
            segment.CopyTo(buffer);
            return segment.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _writtenBytes, buffer.Length);
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                _firstWriteStarted.TrySetResult();
                await _releaseFirstWrite.Task;
            }

            _writes.Writer.TryWrite(true);
        }

        protected override void Dispose(bool disposing)
        {
            _releaseFirstWrite.TrySetResult();
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

    [Test]
    public async Task ConcurrentSmallBurstsNeverStrandACommand()
    {
        // Appends that land while a send is in progress skip the wake and rely on the flush
        // loop's re-check. Short bursts from many producers keep the loop moving between
        // sending, re-checking and parking, so a lost wake would leave a reply missing.
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        const int producers = 16;
        const int rounds = 150;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = new Task[producers];
        for (var p = 0; p < producers; p++)
        {
            var burst = 1 + p % 3;
            workers[p] = Task.Run(async () =>
            {
                await start.Task;
                var pending = new Task<RespValue>[burst];
                for (var round = 0; round < rounds; round++)
                {
                    for (var i = 0; i < burst; i++)
                        pending[i] = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).AsTask();
                    foreach (var reply in pending)
                    {
                        using var value = await reply.WaitAsync(TimeSpan.FromSeconds(10));
                        if (value.AsString() != "PONG") throw new InvalidOperationException("Unexpected reply.");
                    }

                    // Let the loop drain and park between some rounds, not only mid-send.
                    if (round % 16 == 0) await Task.Yield();
                }
            });
        }

        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(60));
        await Assert.That(workers.All(worker => worker.IsCompletedSuccessfully)).IsTrue();
    }

    [Test]
    public async Task DedicatedThreadWriterOnAnIdleConnectionGetsItsReply()
    {
        await using var server = new FakeRespServer(FakeRespServer.PongReply);
        await using var connection = await RespireConnection.ConnectAsync("127.0.0.1", server.Port);
        var replies = new List<string>();
        Exception? failure = null;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new Thread(() =>
        {
            try
            {
                for (var i = 0; i < 20; i++)
                {
                    if (Thread.CurrentThread.IsThreadPoolThread)
                        throw new InvalidOperationException("The writer must not be a pool thread.");
                    // Each command starts a new batch on an idle connection, the dispatched path.
                    using var reply = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame))
                        .AsTask().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                    replies.Add(reply.AsString()!);
                }
            }
            catch (Exception error) { failure = error; }
            finally { finished.TrySetResult(); }
        }) { IsBackground = true };
        writer.Start();

        // Waiting for the dedicated writer must not occupy a pool worker needed by its replies.
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(failure).IsNull();
        await Assert.That(replies.Count).IsEqualTo(20);
        await Assert.That(replies.All(reply => reply == "PONG")).IsTrue();
    }
}
