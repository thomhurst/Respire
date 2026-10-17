using System.Runtime.CompilerServices;
using Respire.Infrastructure;
using Respire.Networking;

namespace Respire.Internal;

/// <summary>One sampling wait budget shared by every candidate and topology retry.</summary>
internal static class NearestReadSelection
{
    internal static long CreateDeadline() => Environment.TickCount64 + ReadLatencySampler.SamplingWaitMilliseconds;

    /// <summary>
    /// Once the shared budget has expired, a newly started probe could not answer before the read
    /// is queued behind it. Selection then reuses existing evidence and starts no new probe.
    /// </summary>
    internal static bool CanStartProbe(long deadline) => deadline - Environment.TickCount64 > 0;

    /// <summary>
    /// Returns <paramref name="selected"/>, or with Nearest sampling the first socket without an
    /// unanswered probe or ROLE validation reservation. Either excludes only its own socket, so siblings
    /// are checked in a fixed order (independent of concurrent cursor movement) before the endpoint is skipped.
    /// </summary>
    internal static RespireConnection? AvoidPendingProbe(RespireConnectionMultiplexer multiplexer, RespireConnection selected,
        ReadLatencySampler<RespireConnection>? sampler, string? preferredZone = null)
    {
        if (sampler is null || !sampler.IsOccupied(selected)) return selected;
        for (var index = 0; index < multiplexer.ConnectionCount; index++)
        {
            var sibling = preferredZone is null ? multiplexer.GetConnection(index)
                : multiplexer.GetConnectionForZone(preferredZone, index);
            if (!ReferenceEquals(sibling, selected) && !sampler.IsOccupied(sibling)) return sibling;
        }
        return null;
    }

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
        if (wait is not null) return WaitAsync(latency, wait.Token, cancellationToken);
        // The shared budget has expired. An unanswered probe still occupies its connection's FIFO,
        // so a read sent there would queue behind it. Candidates without a probe remain eligible
        // because selection starts no new probe after the budget expires.
        return ValueTask.FromResult(ReadLatencyResult.Pending);
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
/// candidates and completed probes without latency evidence remain eligible. After the shared
/// budget expires, selection starts no new probe, so a late-discovered candidate stays eligible.
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
    private const int InlineBandCapacity = 8;
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
    private int _priority;
    private readonly long? _tolerance;
    private readonly uint _start;
    private int _currentIndex;
    private bool _enumerating;
    private BandBuffer _inlineBand;
    private List<BandSample>? _overflowBand;
    private int _bandCount;

    [InlineArray(InlineBandCapacity)]
    private struct BandBuffer
    {
        private BandSample _element0;
    }

    internal readonly record struct PendingSample(T Candidate, ValueTask<ReadLatencyResult> Latency, bool Linked, int Order, int Priority);
    private readonly record struct BandSample(T Candidate, long Latency, bool Linked, int Order, int Priority);
    internal readonly bool HasPendingSamples => _pending is { Count: > 0 };
    internal readonly bool HasPreferredCandidate => _hasValue && _priority == 0;

    internal void QueueSample(T candidate, ValueTask<ReadLatencyResult> latency, bool linked = true, int priority = 0)
    {
        var order = _tolerance is not null && _enumerating ? _currentIndex : _sampleOrder;
        _sampleOrder++;
        if (latency.IsCompletedSuccessfully) Consider(candidate, latency.Result, linked, order, priority);
        // Without a band, only cold/expired samples allocate; warm minimum selection stays allocation-free.
        else (_pending ??= new(4)).Add(new(candidate, latency, linked, order, priority));
    }

    internal bool TryNextSample(out PendingSample sample)
    {
        sample = default;
        if (_pending is null || _nextSample == _pending.Count) return false;
        sample = _pending[_nextSample++];
        return true;
    }

    internal NearestReadSelection(uint start, int candidateCount, TimeSpan? tolerance = null)
    {
        this = default;
        _next = start;
        _start = start;
        _tolerance = tolerance?.Ticks;
        _candidateCount = _remaining = candidateCount;
    }

    internal bool TryNext(out int index)
    {
        index = 0;
        if (_remaining == 0) return false;
        _remaining--;
        index = (int)(_next++ % (uint)_candidateCount);
        _currentIndex = index;
        _enumerating = true;
        return true;
    }

    internal void Consider(T candidate, ReadLatencyResult sample, bool linked = true, int order = int.MaxValue, int priority = 0)
    {
        if (sample.Kind == ReadLatencyKind.Pending) return;
        var latency = sample.Kind == ReadLatencyKind.Measured ? sample.Ticks : long.MaxValue;
        if (_tolerance is not null)
        {
            var value = new BandSample(candidate, latency, linked, order, priority);
            if (_bandCount < InlineBandCapacity) _inlineBand[_bandCount] = value;
            else (_overflowBand ??= []).Add(value);
            _bandCount++;
        }
        if (_hasValue && (priority > _priority || priority == _priority
            && (_linked && !linked || _linked == linked
                && (latency > _latency || latency == _latency && order >= _selectedOrder)))) return;
        _selected = candidate;
        _latency = latency;
        _linked = linked;
        _hasValue = true;
        _selectedOrder = order;
        _priority = priority;
    }

    internal bool TryGet(out T selected)
    {
        selected = _selected;
        if (_bandCount > 0)
        {
            // Candidate indices are stable even though probes start in rotated order.
            // Rotate within the eligible set, so gaps left by slow nodes do not bias traffic.
            // Small replica groups stay inline. Larger groups combine their overflow only here.
            Span<BandSample> band = _inlineBand[..Math.Min(_bandCount, InlineBandCapacity)];
            if (_overflowBand is not null)
            {
                var combined = new BandSample[_bandCount];
                band.CopyTo(combined);
                _overflowBand.CopyTo(combined, InlineBandCapacity);
                band = combined;
            }
            band.Sort(static (left, right) => left.Order.CompareTo(right.Order));
            for (var index = 0; index < band.Length; index++)
            {
                if (index < InlineBandCapacity) _inlineBand[index] = band[index];
                else _overflowBand![index - InlineBandCapacity] = band[index];
            }
            var count = 0;
            foreach (var sample in band)
                if (IsInBand(sample)) count++;
            var offset = count == 0 ? 0 : (int)(_start % (uint)count);
            foreach (var sample in band)
            {
                if (!IsInBand(sample) || offset-- != 0) continue;
                selected = sample.Candidate;
                break;
            }
        }
        return _hasValue;
    }

    // Subtraction cannot overflow: usable measured ticks are nonnegative, and the minimum
    // belongs to this same tier. Unknown samples join only an entirely unmeasured tier.
    private readonly bool IsInBand(BandSample sample)
        => sample.Priority == _priority && sample.Linked == _linked
            && (sample.Latency == long.MaxValue ? _latency == long.MaxValue
                : sample.Latency - _latency <= _tolerance!.Value);

    /// <summary>Revalidates every eligible member before the router publishes a band snapshot.</summary>
    internal bool AllBandCandidatesMatch<TState>(TState state, Func<T, TState, bool> isCurrent)
    {
        for (var index = 0; index < _bandCount; index++)
        {
            var sample = BandAt(index);
            if (IsInBand(sample) && !isCurrent(sample.Candidate, state)) return false;
        }
        return true;
    }

    /// <summary>Captures the band in topology order after <see cref="TryGet"/> has sorted it.</summary>
    internal RespireReadRoutingSnapshot CaptureSnapshot(RespireReadFrom policy, int? slot,
        Func<T, RespireEndpoint> endpoint, RespireReadRoutingSnapshot? previous = null)
    {
        var count = 0;
        var unchanged = previous is not null && previous.ReadFrom == policy && previous.ClusterSlot == slot;
        for (var index = 0; index < _bandCount; index++)
        {
            var sample = BandAt(index);
            if (!IsInBand(sample)) continue;
            unchanged &= previous is not null && count < previous.EligibleEndpoints.Count
                && previous.EligibleEndpoints[count] == endpoint(sample.Candidate);
            count++;
        }
        if (unchanged && count == previous!.EligibleEndpoints.Count) return previous;
        var eligible = new RespireEndpoint[count];
        count = 0;
        for (var index = 0; index < _bandCount; index++)
        {
            var sample = BandAt(index);
            if (IsInBand(sample)) eligible[count++] = endpoint(sample.Candidate);
        }
        return new(policy, slot, Array.AsReadOnly(eligible));
    }

    private BandSample BandAt(int index) => index < InlineBandCapacity
        ? _inlineBand[index] : _overflowBand![index - InlineBandCapacity];
}
