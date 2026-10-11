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
        try
        {
            // Starting the region runs a blocking full GC. The finalizer thread can then
            // flush the runtime's cast cache, racing with the measured thread. Wait for
            // that pass, then refill the cache so the measured thread does not pay for it.
            GC.WaitForPendingFinalizers();
            PrimeRuntimeCastCache(CastCachePrimer);
            result = measure();
        }
        finally
        {
            try { GC.EndNoGCRegion(); }
            catch (InvalidOperationException error) { regionError = error; }
        }
        // Preserve a measurement exception; otherwise surface failed region cleanup.
        if (regionError is not null)
            throw new InvalidOperationException("The allocation measurement's no-GC region was not retained.", regionError);
        return result;
    }

    // After a flush this cast misses the cache and stores its result, which reallocates
    // the cache table at its previous size before measurement begins. No-inline keeps
    // the JIT from folding the check for a known primer type.
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool PrimeRuntimeCastCache(object value) => value is object[];
}
