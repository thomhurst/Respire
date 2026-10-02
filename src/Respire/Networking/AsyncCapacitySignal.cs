namespace Respire.Networking;

/// <summary>
/// Coalescing pulse used to park any number of producers while the in-flight ring is full.
/// A waiter is allocated only during overload; all producers in one full-ring generation
/// share the same task and retry when the receive loop frees capacity.
/// </summary>
internal sealed class AsyncCapacitySignal
{
    private readonly object _gate = new();
    private volatile TaskCompletionSource? _waiters;

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        var wait = CaptureGeneration();
        return cancellationToken.CanBeCanceled ? wait.WaitAsync(cancellationToken) : wait;
    }

    /// <summary>
    /// Returns the current generation's shared task without registering a cancellation callback.
    /// A caller that re-checks its condition after capturing can abandon the task for free, and
    /// only binds its token (with <see cref="Task.WaitAsync(CancellationToken)"/>) when it parks.
    /// </summary>
    public Task CaptureGeneration()
    {
        lock (_gate)
        {
            return (_waiters ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    public void Signal()
    {
        if (_waiters is null)
        {
            return;
        }

        TaskCompletionSource? waiters;
        lock (_gate)
        {
            waiters = _waiters;
            _waiters = null;
        }

        waiters?.TrySetResult();
    }
}
