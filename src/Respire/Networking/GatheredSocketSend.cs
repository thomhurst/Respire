using System.Net.Sockets;
using System.Threading.Tasks.Sources;

namespace Respire.Networking;

/// <summary>One reusable vector send, owned exclusively by a buffer's persistent sender.</summary>
internal sealed class GatheredSocketSend : SocketAsyncEventArgs, IValueTaskSource<int>
{
    private ManualResetValueTaskSourceCore<int> _core;

    internal ValueTask<int> SendAsync(Socket socket, List<ArraySegment<byte>> segments)
    {
        BufferList = segments;
        try
        {
            if (socket.SendAsync(this)) return new(this, _core.Version);
            var error = SocketError;
            var sent = BytesTransferred;
            BufferList = null;
            return error == SocketError.Success
                ? ValueTask.FromResult(sent)
                : ValueTask.FromException<int>(new SocketException((int)error));
        }
        catch
        {
            BufferList = null;
            throw;
        }
    }

    protected override void OnCompleted(SocketAsyncEventArgs e)
    {
        // Only the persistent sender awaits this source. Resume that internal pump directly;
        // the write lease separately queues public continuations away from the sender.
        if (SocketError == SocketError.Success) _core.SetResult(BytesTransferred);
        else _core.SetException(new SocketException((int)SocketError));
    }

    int IValueTaskSource<int>.GetResult(short token)
    {
        // One persistent sender awaits each send once; no concurrent or copied awaiters may
        // consume this reusable source before BufferList is cleared and its version advances.
        if (_core.GetStatus(token) == ValueTaskSourceStatus.Pending)
            throw new InvalidOperationException("The gathered socket send has not completed.");
        try { return _core.GetResult(token); }
        finally { BufferList = null; _core.Reset(); }
    }
    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _core.GetStatus(token);
    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token,
        ValueTaskSourceOnCompletedFlags flags) => _core.OnCompleted(continuation, state, token, flags);
}
