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

#if NET9_0_OR_GREATER
    private readonly Lock _gate = new();
#else
    private readonly object _gate = new();
#endif

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
        while (true)
        {
            Batch batch;
            lock (_gate)
            {
                if (_pendingCount == 0)
                {
                    _running = false;
                    return;
                }

                batch = _pending[_pendingHead];
                _pending[_pendingHead] = default;
                _pendingHead = (_pendingHead + 1) % _pending.Length;
                _pendingCount--;
            }

            var items = batch.Items;
            for (var i = 0; i < batch.Count; i++)
            {
                ref var entry = ref items[i];
                var source = entry.Source;
                var value = entry.Value;
                entry = default;
                if (!source.TrySetResult(in value))
                {
                    // Lost to cancellation or connection failure; the reply still had to be
                    // consumed from the wire.
                    value.Dispose();
                }

                source.ReleaseRef();
            }

            lock (_gate)
            {
                if (_spareCount < _spares.Length)
                {
                    _spares[_spareCount++] = items;
                }
            }
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
