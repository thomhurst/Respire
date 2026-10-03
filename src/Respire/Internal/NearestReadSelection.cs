namespace Respire.Internal;

/// <summary>One sampling wait budget shared by every candidate and topology retry.</summary>
internal static class NearestReadSelection
{
    internal static long CreateDeadline() => Environment.TickCount64 + ReadLatencySampler.SamplingWaitMilliseconds;

    internal static CancellationTokenSource? CreateWaitCancellation(long deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = deadline - Environment.TickCount64;
        if (remaining <= 0) return null;
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromMilliseconds(remaining));
        return source;
    }

    internal static ValueTask<ReadLatencyResult> GetLatencyAsync(ValueTask<ReadLatencyResult> latency, CancellationTokenSource? wait,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (latency.IsCompletedSuccessfully) return latency;
        return wait is not null ? WaitAsync(latency, wait.Token, cancellationToken)
            : ValueTask.FromResult(ReadLatencyResult.Pending);
    }

    private static async ValueTask<ReadLatencyResult> WaitAsync(ValueTask<ReadLatencyResult> latency, CancellationToken waitToken,
        CancellationToken cancellationToken)
    {
        try { return await latency.AsTask().WaitAsync(waitToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (waitToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ReadLatencyResult.Pending;
        }
    }
}

/// <summary>Rotated minimum selection with allocation-free warm samples and bounded pending probes.</summary>
/// <remarks>
/// This type owns the common enumeration, sampling order, tie-breaking and minimum selection.
/// Routers retain connection acquisition and publication checks: configured/Sentinel replicas
/// require ROLE/link validation, while Cluster candidates require current slot membership.
/// <para>The router-owned selection transitions are:</para>
/// <list type="table">
/// <listheader><term>State / observation</term><description>Next action</description></listheader>
/// <item><term>Collect candidates</term><description>
/// Rotate eligible candidates and start all available probes before waiting. Reuse fresh samples.
/// </description></item>
/// <item><term>Pending samples</term><description>
/// Wait under the original shared sampling deadline, then recheck connection and role eligibility.
/// An unanswered probe excludes its connection until the FIFO reply completes. Unsampled
/// candidates and completed probes without latency evidence remain eligible.
/// </description></item>
/// <item><term>Current winner</term><description>
/// Revalidate its owner/membership and return. A usable candidate need not await background discovery.
/// </description></item>
/// <item><term>No current winner, first pass: configured group</term><description>
/// Reselect once from current entries, preserving the sampling deadline and previous failure.
/// </description></item>
/// <item><term>No current winner, first pass: Sentinel</term><description>
/// Join pending/due replica discovery, then reselect once even when endpoint addresses are unchanged.
/// </description></item>
/// <item><term>No current winner, first pass: Cluster</term><description>
/// If owner or replica membership was replaced, reselect directly. Otherwise join the captured
/// range's pending/due refresh when available; a throttled range does not start another refresh.
/// Reselect once from current publication in either case, retaining the original sampling deadline.
/// </description></item>
/// <item><term>No current winner, second pass</term><description>
/// Fail with the retained candidate/discovery error. Do not repeat the failed-selection refresh/retry.
/// </description></item>
/// <item><term>Caller cancellation</term><description>
/// Stop selection and detach its sampling wait; shared physical probes keep their bounded slots.
/// </description></item>
/// </list>
/// <para>The sampling deadline spans both passes. Connection acquisition and topology discovery
/// retain their own existing cancellation, timeout and refresh-throttle rules.</para>
/// </remarks>
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

    internal readonly record struct PendingSample(T Candidate, ValueTask<ReadLatencyResult> Latency, bool Linked, int Order);
    internal readonly bool HasPendingSamples => _pending is { Count: > 0 };

    internal void QueueSample(T candidate, ValueTask<ReadLatencyResult> latency, bool linked = true)
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

    internal void Consider(T candidate, ReadLatencyResult sample, bool linked = true, int order = int.MaxValue)
    {
        if (sample.Kind == ReadLatencyKind.Pending) return;
        var latency = sample.Kind == ReadLatencyKind.Measured ? sample.Ticks : long.MaxValue;
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
