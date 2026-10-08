using System.Runtime.CompilerServices;
using Respire.Internal;
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
/// <para>
/// Single producer: only the receive loop calls <see cref="Add"/> and <see cref="Flush"/>,
/// so the filling buffer needs no synchronization; only the handoff does. Processed buffers
/// return to a small spare list, so steady state allocates nothing.
/// </para>
/// <para>
/// Serial delivery has one escape hatch. When replies have waited behind a runner that made
/// no progress for the stall threshold, <see cref="RescueStalledRunner(long, long)"/> moves
/// them to a new runner so a continuation blocking on another reply from this connection
/// cannot deadlock it. Rescued replies are still delivered in wire order among themselves,
/// but they then run concurrently with the continuation that stalled, so a continuation that
/// is merely slow (not blocked) for longer than the threshold also loses its ordering
/// relative to later replies. Callers must not block inside continuations; the rescue exists
/// so that doing so degrades instead of hanging.
/// </para>
/// </remarks>
internal sealed class CompletionScheduler : IThreadPoolWorkItem
{
    private const int InitialBatchSize = 64;

    /// <summary>Deferring more than this inside one drain flushes early, bounding both the
    /// first reply's added latency and the recycled buffer size.</summary>
    private const int MaxBatchSize = 256;

    // Deep drains can reuse more small batches without retaining more entry storage
    // than four maximum-size batches. Both limits apply under the handoff gate.
    // Only cleared spares count here; pending, filling and active arrays own live
    // work. Array headers and the reference list are additional storage.
    private const int MaxSpareBuffers = 16;
    private const int MaxSpareEntries = 4 * MaxBatchSize;

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
    private int _spareEntryCount;

    /// <summary>Copies spare-storage facts for friend tests without exposing pooled arrays or entries.</summary>
    internal SpareStorageSnapshot InspectSpareStorageForTests()
    {
        lock (_gate)
        {
            var lengths = new List<int>();
            var cleared = true;
            foreach (var buffer in _spares)
            {
                if (buffer is null) continue;
                lengths.Add(buffer.Length);
                foreach (var entry in buffer)
                    if (entry.Source is not null || !entry.Value.Equals(default(RespValue)))
                        cleared = false;
            }
            return new(lengths.ToArray(), Unsafe.SizeOf<Entry>(), cleared, _spareCount, _spareEntryCount);
        }
    }

    internal readonly record struct SpareStorageSnapshot(
        int[] BufferLengths, int EntrySize, bool EntriesCleared, int TrackedBufferCount, int TrackedEntryCount);

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

    // Touched only by the stall watcher.
    private long _observedClaim = -1;
    private long _observedSince;

    // Wakes the connection's stall watcher when replies may be waiting behind a runner, so
    // idle connections pay nothing for the rescue.
    private readonly AsyncFlushSignal _stallWatch = new();

    // A runner thread keeps these after its delivery is handed off. That is safe: the stale
    // generation no longer matches, so a later ReleaseCurrentRunner on this thread is a no-op.
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
            RespireTelemetry.RecordDiscardedError(in value, source.CommandName, source.ErrorAttempts);
            value.Dispose();
            source.ReleaseRef();
            return;
        }
        if (_fillingCount == _filling.Length)
        {
            Array.Resize(ref _filling, Math.Min(MaxBatchSize, _filling.Length * 2));
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
        if (FlushDeferred())
        {
            ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
        }
    }

    /// <summary>
    /// Hands deferred completions to the runner like <see cref="Flush"/>, but when that makes
    /// a runner necessary it returns <see langword="true"/> instead of queueing one. The
    /// caller then owns the runner and must call <see cref="Execute"/> or
    /// <see cref="ScheduleRunner"/> exactly once.
    /// </summary>
    public bool FlushDeferred()
    {
        if (_fillingCount == 0)
        {
            return false;
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
                _spareEntryCount -= replacement!.Length;
            }

            schedule = !_running;
            _running = true;
        }

        _filling = replacement ?? new Entry[InitialBatchSize];
        _fillingCount = 0;
        if (!schedule)
        {
            // Queued behind a runner that is already delivering.
            _stallWatch.Signal();
        }

        return schedule;
    }

    /// <summary>Queues the runner that <see cref="FlushDeferred"/> handed to the caller.</summary>
    public void ScheduleRunner() => ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);

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
            bool armStallWatch;
            lock (_gate)
            {
                if (delivered is not null)
                {
                    RetainSpareLocked(delivered);

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
                // A reply can only wait behind this runner if more than one is due.
                armStallWatch = batch.Count > 1 || _pendingCount > 0;
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

                if (armStallWatch)
                {
                    // Arm after the first claim, so the watcher's first observation already sees
                    // the claim a blocking continuation would hold.
                    armStallWatch = false;
                    _stallWatch.Signal();
                }

                ref var entry = ref items[i];
                var source = entry.Source;
                var value = entry.Value;
                if (!source.CompleteReservedResult(in value))
                {
                    // Lost to cancellation or connection failure; the reply still had to be
                    // consumed from the wire.
                    value.Dispose();
                }

                source.ReleaseRef();
                // Cleared only once delivered: a rescue treats a claimed entry that is still
                // populated as possibly mid-delivery (see TryRescueHandOffLocked).
                entry = default;
            }

            delivered = items;
        }
    }

    private void RetainSpareLocked(Entry[] delivered)
    {
        if (_spareCount < MaxSpareBuffers && delivered.Length <= MaxSpareEntries - _spareEntryCount)
        {
            if (_spareCount == _spares.Length)
                Array.Resize(ref _spares, Math.Min(MaxSpareBuffers, _spares.Length * 2));
            _spares[_spareCount++] = delivered;
            _spareEntryCount += delivered.Length;
            return;
        }

        // A prior large drain must not permanently consume the budget needed by
        // later small drains. Replace only a cleared, larger spare; live arrays
        // remain with their runner. Scan at most 16 slots, only on a cache miss.
        var largest = -1;
        var largestLength = delivered.Length;
        for (var index = 0; index < _spareCount; index++)
        {
            if (_spares[index]!.Length <= largestLength) continue;
            largest = index;
            largestLength = _spares[index]!.Length;
        }
        if (largest >= 0)
        {
            _spareEntryCount += delivered.Length - largestLength;
            _spares[largest] = delivered;
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
    /// Hands delivery to a new runner when replies have waited at least
    /// <paramref name="stallMilliseconds"/> behind an executing runner that made no progress
    /// meanwhile. Delivery is serial, so a continuation that blocks on another reply from this
    /// connection (sync-over-async, a blocking join) would otherwise wait on itself forever.
    /// Call periodically from a single thread; a call is cheap while delivery is idle or moving.
    /// </summary>
    /// <returns><see langword="true"/> when delivery was handed off.</returns>
    internal bool RescueStalledRunner(long nowMilliseconds, long stallMilliseconds)
        => RescueStalledRunner(nowMilliseconds, stallMilliseconds, out _);

    /// <inheritdoc cref="RescueStalledRunner(long, long)"/>
    /// <param name="nowMilliseconds">The current monotonic time.</param>
    /// <param name="stallMilliseconds">How long replies may wait behind a runner that is not moving.</param>
    /// <param name="nextCheckMilliseconds">
    /// How soon the caller should check again while replies wait, or -1 when none do and the
    /// caller can park on <see cref="WaitForPossibleStallAsync"/>.
    /// </param>
    internal bool RescueStalledRunner(long nowMilliseconds, long stallMilliseconds, out long nextCheckMilliseconds)
    {
        var claim = Volatile.Read(ref _claim);
        var executing = Volatile.Read(ref _executing);
        var waiting = executing && MayHaveWaitingReplies(claim);
        // The stall clock starts only once replies are waiting, so a slow continuation with
        // nothing behind it is never handed off as soon as the next reply arrives.
        if (claim != _observedClaim || !waiting)
        {
            // An observation without waiting replies must not seed the clock: forget the claim
            // so the next check that finds replies waiting starts a fresh threshold.
            _observedClaim = waiting ? claim : -1;
            _observedSince = nowMilliseconds;
            // Delivery moved (or replies just started waiting) at some unknown point since the
            // last check, so check again soon: once the claim holds still, the clock then
            // starts at most a fifth of the threshold after the last progress. With nothing
            // waiting, nothing can be stuck until a reply queues behind a runner and wakes the
            // watcher again.
            nextCheckMilliseconds = waiting ? Math.Max(1, stallMilliseconds / 5) : -1;
            return false;
        }

        var remaining = stallMilliseconds - (nowMilliseconds - _observedSince);
        if (remaining > 0)
        {
            nextCheckMilliseconds = remaining;
            return false;
        }

        // Keep watching, and soon: the replacement runner may block as well, and its first
        // claim should start the next clock promptly rather than a full threshold later.
        nextCheckMilliseconds = Math.Max(1, stallMilliseconds / 5);

        lock (_gate)
        {
            // Only an executing runner can be stuck in caller code. A runner still queued
            // behind a starved pool must remain the single owner, or two would deliver.
            if (!_executing || Volatile.Read(ref _claim) != claim) return false;
            var stillWaiting = _pendingCount > 0 || (_activeItems is not null && (int)claim < _activeCount);
            if (!stillWaiting || !TryRescueHandOffLocked(claim)) return false;
        }

        _observedSince = nowMilliseconds;
        return true;
    }

    /// <summary>
    /// Completes when replies may have started waiting behind a runner (single waiter). The
    /// stall watcher then polls <see cref="RescueStalledRunner(long, long, out long)"/> until
    /// nothing waits.
    /// </summary>
    internal ValueTask WaitForPossibleStallAsync() => _stallWatch.WaitAsync();

    /// <summary>Whether a runner is executing (not merely queued) right now.</summary>
    internal bool IsDeliveryExecuting => Volatile.Read(ref _executing);

    /// <summary>Wakes the stall watcher, for example so it can observe connection teardown.</summary>
    internal void WakeStallWatcher() => _stallWatch.Signal();

    /// <summary>
    /// Whether parsed replies are queued or unclaimed. Callers that keep rescuing during
    /// teardown poll this until delivery no longer depends on the rescue.
    /// </summary>
    internal bool HasWaitingReplies
    {
        get
        {
            lock (_gate)
            {
                return _pendingCount > 0
                    || (_activeItems is not null && (int)Volatile.Read(ref _claim) < _activeCount);
            }
        }
    }

    /// <summary>How many parsed replies are queued or unclaimed.</summary>
    internal int WaitingReplyCount
    {
        get
        {
            lock (_gate)
            {
                var count = _activeItems is null ? 0 : Math.Max(0, _activeCount - (int)Volatile.Read(ref _claim));
                for (var i = 0; i < _pendingCount; i++)
                {
                    count += _pending[(_pendingHead + i) % _pending.Length].Count;
                }

                return count;
            }
        }
    }

    /// <summary>
    /// Changes whenever a reply is claimed, a batch starts or delivery is handed off. Teardown
    /// uses it to tell a starved runner from a chain of runners that keep moving.
    /// </summary>
    internal long DeliveryProgress => Volatile.Read(ref _claim);

    // Unsynchronized pre-filter for the stall clock; the handoff re-checks under the gate.
    private bool MayHaveWaitingReplies(long claim)
        => Volatile.Read(ref _pendingCount) > 0 || (int)claim < Volatile.Read(ref _activeCount);

    /// <summary>
    /// Transfers ownership from the executing runner to a newly queued one, moving the active
    /// batch's unclaimed replies to the front of the queue. The old runner sees the new
    /// generation and exits once its current continuation returns. Caller holds the gate.
    /// Used only by <see cref="ReleaseCurrentRunner"/>, so the caller is the runner itself,
    /// blocked inside a continuation. That is always a source's final reply, so unlike
    /// <see cref="TryRescueHandOffLocked"/> it needs no multi-reply guard.
    /// </summary>
    private void HandOffLocked()
    {
        var generation = ++_generation;
        var claim = Interlocked.Exchange(ref _claim, (long)generation << 32);
        TransferUnclaimedLocked((int)claim);
    }

    /// <summary>
    /// Hands off from outside the runner, but only from exactly <paramref name="claim"/> and only
    /// when that cannot split a multi-reply source. The runner may have claimed its latest reply
    /// and been descheduled before completing it; if that reply's source still has replies among
    /// the unclaimed ones, two runners would advance one transaction concurrently, so the rescue
    /// waits for the next check instead. A runner blocked in caller code is always inside a
    /// source's final reply, which has no later replies, so a real stall is never refused.
    /// Caller holds the gate.
    /// </summary>
    private bool TryRescueHandOffLocked(long claim)
    {
        var next = (int)claim;
        if (_activeItems is { } items && next > 0 && items[next - 1].Source is { } inFlight
            && HasUnclaimedReplyFor(inFlight, next))
        {
            return false;
        }

        var generation = _generation + 1;
        if (Interlocked.CompareExchange(ref _claim, (long)generation << 32, claim) != claim)
        {
            // The runner moved on; the next check re-evaluates from its new claim.
            return false;
        }

        _generation = generation;
        TransferUnclaimedLocked(next);
        return true;
    }

    private bool HasUnclaimedReplyFor(PendingResponse source, int next)
    {
        for (var i = next; i < _activeCount; i++)
        {
            if (ReferenceEquals(_activeItems![i].Source, source)) return true;
        }

        for (var b = 0; b < _pendingCount; b++)
        {
            var batch = _pending[(_pendingHead + b) % _pending.Length];
            for (var i = 0; i < batch.Count; i++)
            {
                if (ReferenceEquals(batch.Items[i].Source, source)) return true;
            }
        }

        return false;
    }

    private void TransferUnclaimedLocked(int next)
    {
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

    /// <summary>
    /// Awaits <paramref name="operation"/> and, once the awaiting method has suspended, runs
    /// the runner that <see cref="FlushDeferred"/> handed to the caller on the current thread.
    /// The awaiting loop resumes independently on whichever thread completes the operation,
    /// so a continuation that blocks here never stalls it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If the operation completes between the caller's completion check and registration, the
    /// socket and task sources still queue the continuation to the pool rather than run it on
    /// this stack, so the loop resumes elsewhere and the runner still executes here.
    /// </para>
    /// <para>
    /// The runner does not isolate exceptions from caller continuations, exactly as when it runs
    /// as a pool work item: an exception escaping one surfaces through the async method
    /// builder's suspension path, which rethrows it on the thread pool, as unhandled pool work
    /// item exceptions are.
    /// </para>
    /// </remarks>
    internal readonly struct RunWhileAwaiting<T>(ValueTask<T> operation, CompletionScheduler scheduler)
        : ICriticalNotifyCompletion
    {
        private readonly ConfiguredValueTaskAwaitable<T>.ConfiguredValueTaskAwaiter _awaiter =
            operation.ConfigureAwait(false).GetAwaiter();

        public RunWhileAwaiting<T> GetAwaiter() => this;

        // Always suspend: completing synchronously would skip OnCompleted and strand the runner.
        public bool IsCompleted => false;

        public T GetResult() => _awaiter.GetResult();

        public void OnCompleted(Action continuation)
        {
            _awaiter.OnCompleted(continuation);
            scheduler.Execute();
        }

        public void UnsafeOnCompleted(Action continuation)
        {
            _awaiter.UnsafeOnCompleted(continuation);
            scheduler.Execute();
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
