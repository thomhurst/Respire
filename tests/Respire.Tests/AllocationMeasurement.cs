using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace Respire.Tests;

internal static class AllocationMeasurement
{
    private const long NoGcReservationBytes = 16 * 1024 * 1024;

    // A plain object fails the array cast below. That negative result is stored in the
    // runtime cast cache; a successful string[] to object[] cast was not.
    private static readonly object CastCachePrimer = new();

    // Call only from an unkeyed NotInParallel test. Warm measured paths first and
    // keep counters inside synchronous no-inline methods; assertions belong outside.
    // Keep total region allocations well below the reservation. See docs/ALLOCATION_MEASUREMENT.md.
    internal static TResult WithoutConcurrentGc<TResult>(Func<TResult> measure)
    {
        // This is a bounded GC reservation, not a permitted allocation threshold.
        if (!GC.TryStartNoGCRegion(NoGcReservationBytes))
            throw new InvalidOperationException("Could not establish the allocation measurement's no-GC region.");
        TResult result;
        InvalidOperationException? regionError = null;
        var started = RegionSnapshot.Capture();
        var prepared = started;
        var finished = started;
        try
        {
            // Starting the region runs a blocking full GC. The finalizer thread can then
            // flush the runtime's cast cache, racing with the measured thread. Wait for
            // that pass, then refill the cache so the measured thread does not pay for it.
            GC.WaitForPendingFinalizers();
            PrimeRuntimeCastCache(CastCachePrimer);
            prepared = RegionSnapshot.Capture();
            result = measure();
        }
        finally
        {
            finished = RegionSnapshot.Capture();
            try { GC.EndNoGCRegion(); }
            catch (InvalidOperationException error) { regionError = error; }
        }
        // Preserve a measurement exception; otherwise surface failed region cleanup.
        if (regionError is not null)
            throw new InvalidOperationException($"The allocation measurement's no-GC region was not retained. " +
                $"Reservation={NoGcReservationBytes}; preparation: {prepared.DescribeSince(started)}; " +
                $"measurement: {finished.DescribeSince(prepared)}.", regionError);
        return result;
    }

    private readonly record struct RegionSnapshot(long Allocated, long ThreadAllocated, long Timestamp,
        int Gen0, int Gen1, int Gen2, GCLatencyMode Mode)
    {
        internal static RegionSnapshot Capture() => new(GC.GetTotalAllocatedBytes(precise: true),
            GC.GetAllocatedBytesForCurrentThread(), Stopwatch.GetTimestamp(),
            GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GCSettings.LatencyMode);

        internal string DescribeSince(RegionSnapshot before) =>
            $"process bytes={Allocated - before.Allocated}, thread bytes={ThreadAllocated - before.ThreadAllocated}, " +
            $"GC counts={Gen0 - before.Gen0}/{Gen1 - before.Gen1}/{Gen2 - before.Gen2}, " +
            $"mode={before.Mode}->{Mode}, elapsed ms={Stopwatch.GetElapsedTime(before.Timestamp, Timestamp).TotalMilliseconds:F3}";
    }

    // After a flush this cast misses the cache and stores its result, which reallocates
    // the cache table at its previous size before measurement begins. No-inline keeps
    // the JIT from folding the check for a known primer type.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool PrimeRuntimeCastCache(object value) => value is object[];
}
