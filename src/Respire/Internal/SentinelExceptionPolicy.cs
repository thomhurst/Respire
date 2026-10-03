namespace Respire.Internal;

internal static class SentinelExceptionPolicy
{
    // Recovery, retry and guarded diagnostics share these exclusions. Cleanup aggregation
    // still captures all failures so it can finish joining resources before propagating them.
    internal static bool IsRecoverable(Exception error)
        => error is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}
