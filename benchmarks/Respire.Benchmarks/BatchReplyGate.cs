using System.Net.Sockets;

namespace Respire.Benchmarks;

// Diagnostic-only boundary: real socket writes continue, but received bytes cannot
// reach the RESP parser until the controller releases them. No library hooks change.
internal sealed class BatchReplyGate
{
    private TaskCompletionSource? _release;
    private TaskCompletionSource? _received;

    internal Task Arm()
    {
        if (_release is not null) throw new InvalidOperationException("Reply gate is already armed.");
        _received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _release, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _received.Task;
    }

    internal void Release()
    {
        Interlocked.Exchange(ref _release, null)?.TrySetResult();
    }

    internal async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken token)
    {
        var socket = new TcpClient { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, token).ConfigureAwait(false);
            return new GatedStream(socket, this);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private sealed class GatedStream(TcpClient socket, BatchReplyGate gate) : Stream
    {
        private readonly NetworkStream _stream = socket.GetStream();
        private readonly CancellationTokenSource _disposed = new();
        private int _disposeStarted;
        public override bool CanRead => _stream.CanRead;
        public override bool CanWrite => _stream.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _stream.Flush();
        public override Task FlushAsync(CancellationToken token) => _stream.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _stream.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            => _stream.WriteAsync(buffer, token);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await _stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read != 0 && Volatile.Read(ref gate._release) is { } release)
            {
                gate._received!.TrySetResult();
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _disposed.Token);
                await release.Task.WaitAsync(cancellation.Token).ConfigureAwait(false);
            }
            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposeStarted, 1) == 0)
            {
                _disposed.Cancel();
                socket.Dispose();
                _disposed.Dispose();
            }
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
