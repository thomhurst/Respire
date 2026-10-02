namespace Respire.Internal;

/// <summary>One sampling wait budget shared by every candidate and topology retry.</summary>
internal static class NearestReadSelection
{
    internal static long CreateDeadline() => Environment.TickCount64 + 1_000;

    internal static ValueTask<long> GetLatencyAsync(ValueTask<long> latency, long deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (latency.IsCompletedSuccessfully) return latency;
        var remaining = deadline - Environment.TickCount64;
        return remaining > 0 ? WaitAsync(latency, remaining, cancellationToken)
            : ValueTask.FromResult(long.MaxValue);
    }

    private static async ValueTask<long> WaitAsync(ValueTask<long> latency, long remaining,
        CancellationToken cancellationToken)
    {
        try { return await latency.AsTask().WaitAsync(TimeSpan.FromMilliseconds(remaining), cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException) { return long.MaxValue; }
    }
}

/// <summary>Rotated minimum selection with allocation-free warm samples and bounded pending probes.</summary>
internal struct NearestReadSelection<T>
{
    private T _selected;
    private long _latency;
    private bool _linked;
    private bool _hasValue;
    private uint _next;
    private int _remaining;
    private int _candidateCount;
    private List<PendingSample>? _pending;
    private int _nextSample;
    private int _sampleOrder;
    private int _selectedOrder;

    internal readonly record struct PendingSample(T Candidate, ValueTask<long> Latency, bool Linked, int Order);

    internal void QueueSample(T candidate, ValueTask<long> latency, bool linked = true)
    {
        var order = _sampleOrder++;
        if (latency.IsCompletedSuccessfully) Consider(candidate, latency.Result, linked, order);
        // Only cold/expired samples allocate. Warm selections retain the allocation-free path.
        else (_pending ??= new(4)).Add(new(candidate, latency, linked, order));
    }

    internal bool TryNextSample(out PendingSample sample)
    {
        sample = default;
        if (_pending is null || _nextSample == _pending.Count) return false;
        sample = _pending[_nextSample++];
        return true;
    }

    internal NearestReadSelection(uint start, int candidateCount)
    {
        this = default;
        _next = start;
        _candidateCount = _remaining = candidateCount;
    }

    internal bool TryNext(out int index)
    {
        index = 0;
        if (_remaining == 0) return false;
        _remaining--;
        index = (int)(_next++ % (uint)_candidateCount);
        return true;
    }

    internal void Consider(T candidate, long latency, bool linked = true, int order = int.MaxValue)
    {
        if (_hasValue && (_linked && !linked || _linked == linked
            && (latency > _latency || latency == _latency && order >= _selectedOrder))) return;
        _selected = candidate;
        _latency = latency;
        _linked = linked;
        _hasValue = true;
        _selectedOrder = order;
    }

    internal bool TryGet(out T selected)
    {
        selected = _selected;
        return _hasValue;
    }
}
