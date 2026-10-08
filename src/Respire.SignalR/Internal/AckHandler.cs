using System.Collections.Concurrent;

namespace Respire.SignalR.Internal;

internal sealed class AckHandler : IDisposable
{
    private readonly ConcurrentDictionary<int, TaskCompletionSource> _pending = new();
    private readonly object _gate = new();
    private bool _disposed;

    internal Task CreateAck(int id)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate group acknowledgement.");
            return completion.Task;
        }
    }

    internal void TriggerAck(int id)
    {
        if (_pending.TryRemove(id, out var completion)) completion.TrySetResult();
    }

    internal void RemoveAck(int id) => _pending.TryRemove(id, out _);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var completion)) completion.TrySetCanceled();
        }
    }
}
