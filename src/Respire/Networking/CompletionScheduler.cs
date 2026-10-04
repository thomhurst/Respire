using Respire.Protocol;

namespace Respire.Networking;

/// <summary>
/// Delivers response completions for one connection on the thread pool, one work item per
/// receive drain instead of one per command. Under pipelined load a single socket read
/// carries many replies; waking a pool thread for each burns far more CPU in thread-pool
/// semaphore churn than the completions themselves cost. Batches also execute serially in
/// wire order, which multi-reply sources (transactions) require now that
/// <see cref="PendingResponse"/> cores no longer force asynchronous continuations.
/// </summary>
/// <remarks>
/// Single producer: only the receive loop calls <see cref="Add"/> and <see cref="Flush"/>,
/// so the filling buffer needs no synchronization; only the handoff does. Processed buffers
/// return to a small spare list, so steady state allocates nothing.
/// </remarks>
internal sealed class CompletionScheduler : IThreadPoolWorkItem
{
    private const int InitialBatchSize = 64;

    /// <summary>Deferring more than this inside one drain flushes early, bounding both the
    /// first reply's added latency and the recycled buffer size.</summary>
    private const int MaxBatchSize = 256;

    private readonly Lock _gate = new();

    // Producer-owned; touched only by the receive loop, never under the gate.
    private Entry[] _filling = new Entry[InitialBatchSize];
    private int _fillingCount;

    // Gate-protected: batches awaiting the runner (oldest first) and recycled buffers.
    private Batch[] _pending = new Batch[4];
    private int _pendingHead;
    private int _pendingCount;
    private Entry[]?[] _spares = new Entry[]?[4];
    private int _spareCount;
    private bool _running;
    private bool _executing;
    private TaskCompletionSource? _idleWaiter;

    // Gate-protected: the batch being delivered and the delivery generation, which advances
    // for every batch and every handoff.
    private Entry[]? _activeItems;
    private int _activeCount;
    private int _generation;

    // (generation << 32) | next unclaimed index of the active batch. The runner claims each
    // reply by CAS before delivering it, so a handoff takes the unclaimed tail atomically even
    // while the runner is inside a caller continuation.
    private long _claim;

    // Touched only by the periodic stall check.
    private long _observedClaim = -1;
    private long _observedSince;

    [ThreadStatic]
    private static CompletionScheduler? t_runnerScheduler;

    [ThreadStatic]
    private static int t_runnerGeneration;

    private struct Entry
    {
        public PendingResponse Source;
        public RespValue Value;
    }

    private readonly struct Batch(Entry[] items, int count)
    {
        public readonly Entry[] Items = items;
        public readonly int Count = count;
    }

    /// <summary>Defers one completion. Receive loop only.</summary>
    public void Add(PendingResponse source, in RespValue value)
    {
        if (!source.TryReserveResult())
        {
            value.Dispose();
            source.ReleaseRef();
            return;
        }
        if (_fillingCount == _filling.Length)
        {
            Array.Resize(ref _filling, _filling.Length * 2);
        }

        ref var entry = ref _filling[_fillingCount++];
        entry.Source = source;
        entry.Value = value;

        if (_fillingCount >= MaxBatchSize)
        {
            Flush();
        }
    }

    /// <summary>
    /// Hands deferred completions to the runner. The receive loop calls this before every
    /// await so parsed replies never wait on socket readiness.
    /// </summary>
    public void Flush()
    {
        if (_fillingCount == 0)
        {
            return;
        }

        var batch = new Batch(_filling, _fillingCount);
        Entry[]? replacement = null;
        bool schedule;
        lock (_gate)
        {
            if (_pendingCount == _pending.Length)
            {
                GrowPending();
            }

            _pending[(_pendingHead + _pendingCount) % _pending.Length] = batch;
            _pendingCount++;
            if (_spareCount > 0)
            {
                replacement = _spares[--_spareCount];
                _spares[_spareCount] = null;
            }

            schedule = !_running;
            _running = true;
        }

        _filling = replacement ?? new Entry[InitialBatchSize];
        _fillingCount = 0;
        if (schedule)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
        }
    }

    public void Execute()
    {
        var previousScheduler = t_runnerScheduler;
        var previousGeneration = t_runnerGeneration;
        try { ExecuteCore(); }
        finally
        {
            t_runnerScheduler = previousScheduler;
            t_runnerGeneration = previousGeneration;
        }
    }

    private void ExecuteCore()
    {
        Entry[]? delivered = null;
        var generation = 0;
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                if (delivered is not null)
                {
                    if (_spareCount < _spares.Length)
                    {
                        _spares[_spareCount++] = delivered;
                    }

                    // Handed off while delivering: the replacement runner owns what is left.
                    if (_generation != generation) return;
                }

                _activeItems = null;
                _activeCount = 0;
                if (_pendingCount == 0)
                {
                    _running = false;
                    _executing = false;
                    _idleWaiter?.TrySetResult();
                    _idleWaiter = null;
                    return;
                }

                _executing = true;
                batch = _pending[_pendingHead];
                _pending[_pendingHead] = default;
                _pendingHead = (_pendingHead + 1) % _pending.Length;
                _pendingCount--;
                generation = ++_generation;
                _activeItems = batch.Items;
                _activeCount = batch.Count;
                Volatile.Write(ref _claim, (long)generation << 32);
            }

            t_runnerScheduler = this;
            t_runnerGeneration = generation;
            var items = batch.Items;
            var claim = (long)generation << 32;
            for (var i = 0; i < batch.Count; i++, claim++)
            {
                if (Interlocked.CompareExchange(ref _claim, claim + 1, claim) != claim)
                {
                    // Handed off; the unclaimed tail moved to the replacement runner.
                    break;
                }

                ref var entry = ref items[i];
                var source = entry.Source;
                var value = entry.Value;
                entry = default;
                if (!source.CompleteReservedResult(in value))
                {
                    // Lost to cancellation or connection failure; the reply still had to be
                    // consumed from the wire.
                    value.Dispose();
                }

                source.ReleaseRef();
            }

            delivered = items;
        }
    }

    /// <summary>
    /// A delivered reply's continuation may synchronously wait for retirement. Transfer the
    /// remaining replies to another runner so retirement never waits for that continuation.
    /// </summary>
    internal void ReleaseCurrentRunner()
    {
        if (!ReferenceEquals(t_runnerScheduler, this)) return;
        lock (_gate)
        {
            if (_generation == t_runnerGeneration)
            {
                HandOffLocked();
            }
        }
    }

    /// <summary>
    /// Hands delivery to a new runner when the executing one has made no progress for at
    /// least <paramref name="stallMilliseconds"/> while replies wait behind it. Delivery is
    /// serial, so a continuation that blocks on another reply from this connection
    /// (sync-over-async, a blocking join) would otherwise wait on itself forever. Call
    /// periodically from a single thread; a call is cheap while delivery is idle or moving.
    /// </summary>
    /// <returns><see langword="true"/> when delivery was handed off.</returns>
    internal bool RescueStalledRunner(long nowMilliseconds, long stallMilliseconds)
    {
        var claim = Volatile.Read(ref _claim);
        if (claim != _observedClaim || !Volatile.Read(ref _executing))
        {
            _observedClaim = claim;
            _observedSince = nowMilliseconds;
            return false;
        }

        if (nowMilliseconds - _observedSince < stallMilliseconds)
        {
            return false;
        }

        lock (_gate)
        {
            // Only an executing runner can be stuck in caller code. A runner still queued
            // behind a starved pool must remain the single owner, or two would deliver.
            if (!_executing || Volatile.Read(ref _claim) != claim) return false;
            var waiting = _pendingCount > 0 || (_activeItems is not null && (int)claim < _activeCount);
            if (!waiting) return false;
            HandOffLocked();
        }

        _observedSince = nowMilliseconds;
        return true;
    }

    /// <summary>
    /// Transfers ownership from the executing runner to a newly queued one, moving the active
    /// batch's unclaimed replies to the front of the queue. The old runner sees the new
    /// generation and exits once its current continuation returns. Caller holds the gate.
    /// </summary>
    private void HandOffLocked()
    {
        var generation = ++_generation;
        var claim = Interlocked.Exchange(ref _claim, (long)generation << 32);
        var next = (int)claim;
        var remaining = _activeItems is null ? 0 : _activeCount - next;
        if (remaining > 0)
        {
            var tail = new Entry[remaining];
            Array.Copy(_activeItems!, next, tail, 0, remaining);
            Array.Clear(_activeItems!, next, remaining);
            if (_pendingCount == _pending.Length) GrowPending();
            _pendingHead = (_pendingHead + _pending.Length - 1) % _pending.Length;
            _pending[_pendingHead] = new Batch(tail, remaining);
            _pendingCount++;
        }

        _activeItems = null;
        _activeCount = 0;
        _executing = false;
        _running = _pendingCount != 0;
        if (_running)
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        }
        else
        {
            _idleWaiter?.TrySetResult();
            _idleWaiter = null;
        }
    }

    /// <summary>Called after the receive producer exits and flushes its final batch.</summary>
    internal Task WaitForIdleAsync()
    {
        lock (_gate)
        {
            return _running
                ? (_idleWaiter ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task
                : Task.CompletedTask;
        }
    }

    private void GrowPending()
    {
        var grown = new Batch[_pending.Length * 2];
        for (var i = 0; i < _pendingCount; i++)
        {
            grown[i] = _pending[(_pendingHead + i) % _pending.Length];
        }

        _pending = grown;
        _pendingHead = 0;
    }
}
