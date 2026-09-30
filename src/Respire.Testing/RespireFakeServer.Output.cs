namespace Respire.Testing;

public sealed partial class RespireFakeServer
{
    private const int MaximumPendingPushBytes = 16 * 1024 * 1024;

    private sealed class Outbound(byte[] bytes, bool push)
    {
        internal byte[] Bytes { get; } = bytes;
        internal bool Push { get; } = push;
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Flushed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // All enqueue operations hold _gate, giving commands and publications one wire order.
    // Each command awaits its flush; only pushes can accumulate behind a slow reader.
    private Outbound? QueueOutputLocked(Connection connection, byte[] bytes, bool push)
    {
        if (_disposed || connection.Closed || connection.Lifetime.IsCancellationRequested) return null;
        if (push && bytes.Length > MaximumPendingPushBytes - connection.PendingPushBytes)
        {
            StopConnectionLocked(connection);
            return null;
        }
        var output = new Outbound(bytes, push);
        if (!connection.Output.Writer.TryWrite(output)) return null;
        if (push) connection.PendingPushBytes += bytes.Length;
        return output;
    }

    private async Task SendRepliesAsync(Connection connection)
    {
        var token = connection.Lifetime.Token;
        try
        {
            await foreach (var output in connection.Output.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await output.Ready.Task.WaitAsync(token).ConfigureAwait(false);
                    await connection.Stream.WriteAsync(output.Bytes, token).ConfigureAwait(false);
                    output.Flushed.TrySetResult();
                }
                finally { ReleaseOutput(connection, output); }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
        catch (Exception error)
        {
            lock (_gate) _failures.Add(error);
        }
        finally
        {
            lock (_gate) StopConnectionLocked(connection);
            while (connection.Output.Reader.TryRead(out var abandoned)) ReleaseOutput(connection, abandoned);
        }
    }

    // Called outside _gate after sending, or after the stop operation releases _gate.
    // Keep byte accounting on the same gate as enqueue and the overflow decision.
    private void ReleaseOutput(Connection connection, Outbound output)
    {
        output.Flushed.TrySetCanceled(connection.Lifetime.Token);
        if (output.Push)
            lock (_gate) connection.PendingPushBytes -= output.Bytes.Length;
    }

    private void StopConnectionLocked(Connection connection)
    {
        if (connection.Closed) return;
        connection.Closed = true;
        RemoveSubscriptionsLocked(connection);
        connection.Output.Writer.TryComplete();
        // Cancellation changes token state now but runs callbacks asynchronously. ServeAsync
        // joins these callbacks and the writer before disposing the shared stream.
        connection.Stopping = connection.Lifetime.CancelAsync();
    }
}
