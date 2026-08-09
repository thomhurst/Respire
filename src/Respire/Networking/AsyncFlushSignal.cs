using System.Threading.Tasks.Sources;

namespace Respire.Networking;

/// <summary>
/// A reusable, allocation-free auto-reset signal for exactly one waiter (the connection's
/// flush loop) and many signalers (command writers). Replaces spawning a Task per flush:
/// the flush loop is persistent and parks here between batches.
/// </summary>
/// <remarks>
/// A signaler that just wrote the only pending command may request an inline wake: the parked
/// flush loop resumes on the signaling thread, so the send syscall starts without first paying
/// a thread-pool dispatch. That hop is pure latency for a command written into an empty
/// buffer — the dominant case for non-pipelined callers — but under pipelined load an inline
/// wake would capture producer threads without improving batching, so busy signalers dispatch
/// the wake to the pool instead. The flush loop bounds how long it keeps an inline thread
/// (see <c>RespireConnection.FlushLoopAsync</c>), and it never runs caller continuations
/// itself, so a captured thread only ever executes connection-owned send code.
/// </remarks>
internal sealed class AsyncFlushSignal : IValueTaskSource
{
    private const int Idle = 0;
    private const int Signaled = 1;
    private const int Waiting = 2;

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
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
    /// <param name="preferInline">
    /// When true and the waiter is parked, it resumes synchronously on this thread; otherwise
    /// the wake is dispatched to the thread pool.
    /// </param>
    public void Signal(bool preferInline = false)
    {
        if (Interlocked.Exchange(ref _state, Signaled) != Waiting)
        {
            return;
        }

        if (preferInline)
        {
            _core.SetResult(true);
        }
        else
        {
            ThreadPool.UnsafeQueueUserWorkItem(static signal => signal._core.SetResult(true), this, preferLocal: true);
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
