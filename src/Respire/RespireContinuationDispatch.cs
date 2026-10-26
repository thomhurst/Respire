namespace Respire;

/// <summary>Where command await continuations run when their replies arrive.</summary>
public enum RespireContinuationDispatch
{
    /// <summary>
    /// Run continuations inline during reply delivery while they are fast, and queue them to the
    /// thread pool while continuations keep running long enough to delay the replies behind them.
    /// </summary>
    Adaptive,
    /// <summary>
    /// Always run continuations inline during reply delivery. CPU work after an await then delays
    /// other callers sharing the connection until the stalled-delivery rescue moves delivery.
    /// </summary>
    Inline,
    /// <summary>Queue every pending command's continuation to the thread pool.</summary>
    ThreadPool,
}
