namespace Respire.Internal;

// Shared observation boundary, not a response owner. Batch and transaction operations
// keep their own dispatch, conversion and cleanup; their pending retains publication rights.
internal abstract class DeferredOperation(string operation)
{
    public string Operation { get; } = operation;
    public abstract bool IsCompleted { get; }
    public abstract void Fail(Exception error);
    public abstract bool ReportError();
    public abstract void AllowErrorInspection();
    public abstract RespireTelemetry.ErrorObservation Observation { get; }

    internal static void SetAttempts<TOperation>(List<TOperation> operations, int attempts)
        where TOperation : DeferredOperation
    {
        foreach (var operation in operations) operation.Observation.SetAttempts(attempts);
    }

    // Call only after the route's final cleanup. Capture shared selection/transaction
    // history before closing the first pending, since it also lends the dispatch owner.
    internal static int ReportErrors<TOperation>(List<TOperation> operations, int? sharedAttempts = null)
        where TOperation : DeferredOperation
    {
        var failureCount = 0;
        foreach (var operation in operations)
        {
            if (sharedAttempts is { } attempts) operation.Observation.SetAttempts(attempts);
            if (operation.ReportError()) failureCount++;
        }
        return failureCount;
    }
}
