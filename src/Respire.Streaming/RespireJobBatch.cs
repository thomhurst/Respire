namespace Respire.Streaming;

/// <summary>A callback job using the batch queue's payload codec and handler.</summary>
public sealed class RespireJobBatchCallback<T>(string id, T payload)
{
    /// <summary>The callback's application job ID.</summary>
    public string Id { get; } = id;
    /// <summary>The callback payload. Include the batch ID if the handler needs it.</summary>
    public T Payload { get; } = payload;
}

/// <summary>Optional callback jobs, published when a sealed batch has no pending members.</summary>
public sealed class RespireJobBatchOptions<T>
{
    /// <summary>Published when every member succeeded and cancellation was not requested.</summary>
    public RespireJobBatchCallback<T>? OnSuccess { get; init; }
    /// <summary>Published when any member failed or cancellation was requested.</summary>
    public RespireJobBatchCallback<T>? OnFailure { get; init; }
    /// <summary>Published after either outcome, independently of the other callback's execution.</summary>
    public RespireJobBatchCallback<T>? OnComplete { get; init; }
}

/// <summary>An atomic snapshot of retained batch state. Callback execution is excluded from the counts.</summary>
public sealed class RespireJobBatchStatus
{
    internal RespireJobBatchStatus(int pending, int succeeded, int failed, int cancelled,
        bool isSealed, bool cancellationRequested, bool isCompleted)
    {
        Pending = pending;
        Succeeded = succeeded;
        Failed = failed;
        Cancelled = cancelled;
        IsSealed = isSealed;
        CancellationRequested = cancellationRequested;
        IsCompleted = isCompleted;
    }

    /// <summary>Members not yet acknowledged with a terminal outcome, including retries.</summary>
    public int Pending { get; }
    /// <summary>Members that completed successfully.</summary>
    public int Succeeded { get; }
    /// <summary>Members that exhausted retries or were explicitly dead-lettered.</summary>
    public int Failed { get; }
    /// <summary>Members acknowledged after cancellation was requested.</summary>
    public int Cancelled { get; }
    /// <summary>True when no more members can be added.</summary>
    public bool IsSealed { get; }
    /// <summary>True when cancellation was requested. Already running handlers may still execute.</summary>
    public bool CancellationRequested { get; }
    /// <summary>True when sealed with no pending members and eligible callbacks have been published.</summary>
    public bool IsCompleted { get; }
}
