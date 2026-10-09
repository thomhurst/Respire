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
internal sealed class AsyncFlushSignal : IValueTaskSource, IThreadPoolWorkItem
{
    private const int Idle = 0;
    private const int Signaled = 1;
    private const int Waiting = 2;

    private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
    private int _state;

    // Read-only diagnostic seam; observing it adds no work to the signaling path.
    internal bool IsWaiting => Volatile.Read(ref _state) == Waiting;

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
        // Publish preceding work before checking whether an existing signal covers it.
        // A release (including leaving the write gate) followed by an acquire read does
        // not prevent store/load reordering. The full fence keeps this read after the
        // publication, even for callers that publish without the write gate.
        Interlocked.MemoryBarrier();
        // The consumer drains authoritative work after consuming a signal. An existing
        // signal covers this publication too, so busy producers need not write its cache line.
        if (Volatile.Read(ref _state) == Signaled)
        {
            return;
        }

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
            // Queue this instance rather than a delegate and state: the state overload wraps
            // them in a new work item on every wake. Only one wake can be outstanding, because
            // the waiter cannot re-arm until this one has run.
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
        }
    }

    void IThreadPoolWorkItem.Execute() => _core.SetResult(true);

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
