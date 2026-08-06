using System.Threading.Tasks.Sources;

namespace Respire.Networking;

/// <summary>
/// A reusable, allocation-free auto-reset signal for exactly one waiter (the connection's
/// flush loop) and many signalers (command writers). Replaces spawning a Task per flush:
/// the flush loop is persistent and parks here between batches.
/// </summary>
internal sealed class AsyncFlushSignal : IValueTaskSource
{
    private const int Idle = 0;
    private const int Signaled = 1;
    private const int Waiting = 2;

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };
    private int _state;

    /// <summary>Single consumer only.</summary>
    public ValueTask WaitAsync()
    {
        // Consume a pending signal without arming.
        if (Interlocked.CompareExchange(ref _state, Idle, Signaled) == Signaled)
        {
            return default;
        }

        _core.Reset();
        var previous = Interlocked.CompareExchange(ref _state, Waiting, Idle);
        if (previous == Signaled)
        {
            // A signal landed between the fast path and arming; consume it.
            Interlocked.Exchange(ref _state, Idle);
            return default;
        }

        return new ValueTask(this, _core.Version);
    }

    /// <summary>Any thread. Coalesces: signaling an already-signaled instance is a no-op.</summary>
    public void Signal()
    {
        if (Interlocked.Exchange(ref _state, Signaled) == Waiting)
        {
            _core.SetResult(true);
        }
    }

    void IValueTaskSource.GetResult(short token)
    {
        _core.GetResult(token);
        // The wake consumed the signal.
        Interlocked.CompareExchange(ref _state, Idle, Signaled);
    }

    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    void IValueTaskSource.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}
