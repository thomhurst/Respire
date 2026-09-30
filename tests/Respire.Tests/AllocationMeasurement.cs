namespace Respire.Tests;

internal static class AllocationMeasurement
{
    private const long NoGcReservationBytes = 16 * 1024 * 1024;

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
}
