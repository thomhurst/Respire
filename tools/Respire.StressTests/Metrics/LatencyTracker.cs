using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Respire.StressTests.Metrics;

/// <summary>
/// Thread-safe latency tracker using a microsecond-resolution histogram for percentile
/// calculations. Pre-allocates all buckets up front so recording is allocation-free.
/// Default configuration: 10μs bucket width covering 0–5,000ms (500,000 buckets ≈ 4MB).
/// </summary>
internal sealed class LatencyTracker
{
    private readonly long[] _buckets;
    private readonly double _bucketWidthUs;
    private readonly double _maxValueUs;
    private long _count;
    private long _overflowCount;
    private long _minTicks = long.MaxValue;
    private long _maxTicks;

    /// <param name="maxValueMs">Upper bound of the histogram in milliseconds. Values above this are counted as overflow.</param>
    /// <param name="bucketWidthUs">Width of each bucket in microseconds. Smaller = higher resolution but more memory.</param>
    public LatencyTracker(double maxValueMs = 5000, double bucketWidthUs = 10)
    {
        _maxValueUs = maxValueMs * 1000.0;
        _bucketWidthUs = bucketWidthUs;
        _buckets = new long[(int)(_maxValueUs / bucketWidthUs)];
    }

    public long Count => Interlocked.Read(ref _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void RecordTicks(long ticks)
    {
        Interlocked.Increment(ref _count);
        UpdateMinMax(ticks);

        var microseconds = ticks * 1_000_000.0 / Stopwatch.Frequency;
        if (microseconds >= _maxValueUs)
        {
            Interlocked.Increment(ref _overflowCount);
            return;
        }

        var bucketIndex = (int)(microseconds / _bucketWidthUs);
        if ((uint)bucketIndex >= (uint)_buckets.Length)
        {
            bucketIndex = _buckets.Length - 1;
        }

        Interlocked.Increment(ref _buckets[bucketIndex]);
    }

    private void UpdateMinMax(long ticks)
    {
        long currentMin;
        do
        {
            currentMin = Interlocked.Read(ref _minTicks);
            if (ticks >= currentMin)
                break;
        } while (Interlocked.CompareExchange(ref _minTicks, ticks, currentMin) != currentMin);

        long currentMax;
        do
        {
            currentMax = Interlocked.Read(ref _maxTicks);
            if (ticks <= currentMax)
                break;
        } while (Interlocked.CompareExchange(ref _maxTicks, ticks, currentMax) != currentMax);
    }

    public LatencySnapshot ToSnapshot()
    {
        var count = Interlocked.Read(ref _count);
        if (count == 0)
        {
            return new LatencySnapshot
            {
                Count = 0,
                MinMs = 0,
                MaxMs = 0,
                P50Ms = 0,
                P95Ms = 0,
                P99Ms = 0,
                P999Ms = 0,
                OverflowCount = 0
            };
        }

        return new LatencySnapshot
        {
            Count = count,
            MinMs = TicksToMs(Interlocked.Read(ref _minTicks)),
            MaxMs = TicksToMs(Interlocked.Read(ref _maxTicks)),
            P50Ms = GetPercentileMs(0.50),
            P95Ms = GetPercentileMs(0.95),
            P99Ms = GetPercentileMs(0.99),
            P999Ms = GetPercentileMs(0.999),
            OverflowCount = Interlocked.Read(ref _overflowCount)
        };
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// Percentile over all recorded operations. When the target rank lands in the
    /// overflow region (beyond the histogram's upper bound) the observed maximum is
    /// returned, which is the tightest bound available.
    /// </summary>
    private double GetPercentileMs(double percentile)
    {
        var count = Interlocked.Read(ref _count);
        var target = (long)Math.Ceiling(count * percentile);
        if (target <= 0)
            target = 1;

        long cumulative = 0;
        for (var i = 0; i < _buckets.Length; i++)
        {
            cumulative += Interlocked.Read(ref _buckets[i]);
            if (cumulative >= target)
            {
                return (i + 1) * _bucketWidthUs / 1000.0;
            }
        }

        return TicksToMs(Interlocked.Read(ref _maxTicks));
    }
}

internal sealed class LatencySnapshot
{
    public required long Count { get; init; }
    public required double MinMs { get; init; }
    public required double MaxMs { get; init; }
    public required double P50Ms { get; init; }
    public required double P95Ms { get; init; }
    public required double P99Ms { get; init; }
    public required double P999Ms { get; init; }
    public required long OverflowCount { get; init; }
}
