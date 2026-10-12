using System.Net.Sockets;

namespace Respire.Benchmarks;

// Only diagnostic clients use this transport. Hold a real read or write after it
// reaches the socket, without inserting hooks into the production send owners.
internal sealed class StreamTransportGate(bool writes)
{
    private readonly bool _writes = writes;
    private TaskCompletionSource? _release;
    private TaskCompletionSource? _entered;
    private int _writesBeforeHold;

    internal Task Arm(int writesBeforeHold = 0)
    {
        if (_release is not null) throw new InvalidOperationException("Stream gate is already armed.");
        _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _writesBeforeHold = writesBeforeHold;
        Volatile.Write(ref _release, new(TaskCreationOptions.RunContinuationsAsynchronously));
        return _entered.Task;
    }

    internal void Release() => Interlocked.Exchange(ref _release, null)?.TrySetResult();

    private async ValueTask HoldAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _release) is not { } release) return;
        _entered!.TrySetResult();
        await release.Task.WaitAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken token)
    {
        var socket = new TcpClient { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, token).ConfigureAwait(false);
            return new GatedStream(socket, this);
        }
        catch { socket.Dispose(); throw; }
    }

    private sealed class GatedStream(TcpClient socket, StreamTransportGate gate) : Stream
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
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            await _stream.WriteAsync(buffer, token).ConfigureAwait(false);
            if (gate._writes)
            {
                // Skip the SET header to force suspension inside CopyStreamPayloadAsync.
                if (Interlocked.Decrement(ref gate._writesBeforeHold) >= 0) return;
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _disposed.Token);
                await gate.HoldAsync(cancellation.Token).ConfigureAwait(false);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await _stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read != 0 && !gate._writes)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _disposed.Token);
                await gate.HoldAsync(cancellation.Token).ConfigureAwait(false);
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
