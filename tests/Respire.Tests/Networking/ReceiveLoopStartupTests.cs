using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Respire.Tests.Networking;

[Category(TestCategories.ConstrainedRetirement)]
public class ReceiveLoopStartupTests
{
    // The scripted first read and reply continuation deliberately block pool workers.
    [Test, NotInParallel]
    public async Task RetiringFromTheFirstInlineDeliveryCompletesDisposal()
    {
        // The receive loop's first read completes synchronously, so its first suspension is the
        // inline delivery of that reply. A continuation that then waits for retirement must not
        // keep the receive task from completing.
        var stream = new ScriptedStream();
        var connection = await RespireConnection.ConnectAsync("scripted", 6379,
            new RespireConnectionOptions
            {
                Protocol = RespProtocol.Resp2,
                TestingStreamFactory = (_, _, _) => ValueTask.FromResult<Stream>(stream),
            });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var awaiter = connection.SendAsync(new RawCommand(FakeRespServer.PingFrame)).ConfigureAwait(false).GetAwaiter();
        awaiter.UnsafeOnCompleted(() =>
        {
            try
            {
                using var value = awaiter.GetResult();
                connection.RetireAsync().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                completed.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); }
        });

        stream.ReleaseReply();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Assert.That(stream.FirstReadCompletedSynchronously).IsTrue();
    }

    /// <summary>
    /// Answers PING with PONG on a first read that blocks until the test releases the reply and
    /// then completes synchronously; later reads stay pending until the stream is disposed.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private static readonly byte[] Pong = "+PONG\r\n"u8.ToArray();
        private readonly ManualResetEventSlim _pingWritten = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;

        public bool FirstReadCompletedSynchronously { get; private set; }

        public void ReleaseReply() => _release.Set();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _reads) == 1)
            {
                if (!_pingWritten.Wait(TimeSpan.FromSeconds(10)) || !_release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The PING was never written or the reply never released.");
                Pong.CopyTo(buffer);
                FirstReadCompletedSynchronously = true;
                return new ValueTask<int>(Pong.Length);
            }

            return new ValueTask<int>(_closed.Task);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Span.IndexOf("PING"u8) >= 0) _pingWritten.Set();
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult(0);
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
