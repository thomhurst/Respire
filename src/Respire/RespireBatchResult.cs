using System.Runtime.ExceptionServices;

namespace Respire;

/// <summary>Outcome of flushing a <see cref="RespireBatch"/>.</summary>
public readonly record struct RespireBatchResult
{
    internal RespireBatchResult(int count, int failureCount, Exception? firstError)
    {
        Count = count;
        FailureCount = failureCount;
        FirstError = firstError;
    }

    /// <summary>Total number of commands in the batch.</summary>
    public int Count { get; }

    /// <summary>Number of commands whose pending result faulted.</summary>
    public int FailureCount { get; }

    /// <summary>The first command failure, or null when every command succeeded.</summary>
    public Exception? FirstError { get; }

    /// <summary>Rethrows <see cref="FirstError"/> when any command failed.</summary>
    public void ThrowIfAnyFailed()
    {
        if (FirstError is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
