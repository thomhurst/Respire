using System.Runtime.ExceptionServices;

namespace Respire;

/// <summary>A command that failed while a <see cref="RespireBatch"/> was flushed.</summary>
/// <param name="Index">The command's zero-based position in the original batch.</param>
/// <param name="Operation">The Redis operation name.</param>
/// <param name="Error">The error that faulted the command's pending result.</param>
public readonly record struct RespireBatchFailure(int Index, string Operation, Exception Error);

/// <summary>Outcome of flushing a <see cref="RespireBatch"/>.</summary>
public readonly record struct RespireBatchResult
{
    private static readonly IReadOnlyList<RespireBatchFailure> EmptyFailures = Array.Empty<RespireBatchFailure>();
    private readonly IReadOnlyList<RespireBatchFailure>? _failures;

    internal RespireBatchResult(int count, RespireBatchFailure[]? failures)
    {
        Count = count;
        FailureCount = failures?.Length ?? 0;
        FirstError = failures is { Length: > 0 } ? failures[0].Error : null;
        _failures = failures is null ? null : Array.AsReadOnly(failures);
    }

    /// <summary>Total number of commands in the batch.</summary>
    public int Count { get; }

    /// <summary>Number of commands whose pending result faulted; equal to <see cref="Failures"/> count.</summary>
    public int FailureCount { get; }

    /// <summary>The earliest failure in original queue order, or null when every command succeeded.</summary>
    public Exception? FirstError { get; }

    /// <summary>
    /// Failed commands in their original queue order. Empty when every command succeeded.
    /// </summary>
    public IReadOnlyList<RespireBatchFailure> Failures => _failures ?? EmptyFailures;

    /// <summary>Rethrows <see cref="FirstError"/> when any command failed.</summary>
    public void ThrowIfAnyFailed()
    {
        if (FirstError is { } error)
        {
            ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
