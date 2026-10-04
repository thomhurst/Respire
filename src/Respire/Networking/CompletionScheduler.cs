using System.Runtime.CompilerServices;
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
    private TaskCompletionSource? _idleWaiter;

    [ThreadStatic]
    private static RunnerState _currentRunner;

    private struct RunnerState
    {
        public CompletionScheduler? Scheduler;
        public Entry[]? Items;
        public int Next;
        public int Count;
        public bool Detached;
    }

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
            }

            schedule = !_running;
            _running = true;
        }

        _filling = replacement ?? new Entry[InitialBatchSize];
        _fillingCount = 0;
        return schedule;
    }

    /// <summary>Queues the runner that <see cref="FlushDeferred"/> handed to the caller.</summary>
    public void ScheduleRunner() => ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);

    public void Execute()
    {
        var previous = _currentRunner;
        _currentRunner = new RunnerState { Scheduler = this };
        try { ExecuteCore(ref _currentRunner); }
        finally { _currentRunner = previous; }
    }

    private void ExecuteCore(ref RunnerState runner)
    {
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                if (_pendingCount == 0)
                {
                    _running = false;
                    _idleWaiter?.TrySetResult();
                    _idleWaiter = null;
                    return;
                }

                batch = _pending[_pendingHead];
                _pending[_pendingHead] = default;
                _pendingHead = (_pendingHead + 1) % _pending.Length;
                _pendingCount--;
            }

            var items = batch.Items;
            runner.Items = items;
            runner.Count = batch.Count;
            for (var i = 0; i < batch.Count; i++)
            {
                ref var entry = ref items[i];
                var source = entry.Source;
                var value = entry.Value;
                entry = default;
                runner.Next = i + 1;
                if (!source.CompleteReservedResult(in value))
                {
                    // Lost to cancellation or connection failure; the reply still had to be
                    // consumed from the wire.
                    value.Dispose();
                }

                source.ReleaseRef();
                if (runner.Detached) break;
            }

            lock (_gate)
            {
                if (_spareCount < _spares.Length)
                {
                    _spares[_spareCount++] = items;
                }
            }
            if (runner.Detached) return;
        }
    }

    /// <summary>
    /// A delivered reply's continuation may synchronously wait for retirement. Transfer the
    /// remaining replies to another runner so retirement never waits for that continuation.
    /// </summary>
    internal void ReleaseCurrentRunner()
    {
        ref var runner = ref _currentRunner;
        if (!ReferenceEquals(runner.Scheduler, this) || runner.Detached) return;
        var remaining = runner.Count - runner.Next;
        Entry[]? tail = null;
        if (remaining > 0)
        {
            tail = new Entry[remaining];
            Array.Copy(runner.Items!, runner.Next, tail, 0, remaining);
            Array.Clear(runner.Items!, runner.Next, remaining);
        }
        runner.Detached = true;
        bool schedule;
        lock (_gate)
        {
            if (tail is not null)
            {
                if (_pendingCount == _pending.Length) GrowPending();
                _pendingHead = (_pendingHead + _pending.Length - 1) % _pending.Length;
                _pending[_pendingHead] = new Batch(tail, remaining);
                _pendingCount++;
            }
            schedule = _pendingCount != 0;
            _running = schedule;
            if (!schedule)
            {
                _idleWaiter?.TrySetResult();
                _idleWaiter = null;
            }
        }
        if (schedule) ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
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
