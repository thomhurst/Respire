namespace Respire.Tests;

internal static class AllocationMeasurement
{
    // Call only from an unkeyed NotInParallel test. Warm measured paths first and
    // keep counters inside synchronous no-inline methods; assertions belong outside.
    internal static TResult WithoutConcurrentGc<TResult>(Func<TResult> measure)
    {
        // This is a bounded GC reservation, not a permitted allocation threshold.
        if (!GC.TryStartNoGCRegion(16 * 1024 * 1024))
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
