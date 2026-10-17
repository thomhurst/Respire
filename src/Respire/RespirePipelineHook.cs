namespace Respire;

/// <summary>Observes and optionally rejects explicit pipeline and transaction executions.</summary>
/// <remarks>
/// One instance can be called concurrently. Admission is synchronous and runs before any queued
/// command is submitted. Throw to reject the complete execution. Completion exceptions are ignored.
/// This hook does not intercept individual commands or the WATCH setup preceding a commit.
/// </remarks>
public abstract class RespirePipelineHook
{
    /// <summary>Creates a pipeline hook.</summary>
    protected RespirePipelineHook() { }

    /// <summary>Admits an execution and returns optional state passed to completion.</summary>
    public abstract object? OnExecuting(RespirePipelineContext context);

    /// <summary>Observes the final outcome, including rejection; state is null when admission threw.</summary>
    public abstract void OnCompleted(RespirePipelineContext context, object? state,
        RespirePipelineOutcome outcome, Exception? error);
}

/// <summary>An immutable snapshot of one explicit execution, safe to retain after completion.</summary>
public sealed class RespirePipelineContext
{
    internal RespirePipelineContext(bool isTransaction, string[] commands, CancellationToken cancellationToken)
    {
        IsTransaction = isTransaction;
        Commands = Array.AsReadOnly(commands);
        CancellationToken = cancellationToken;
    }

    /// <summary>True for a MULTI/EXEC commit; false for a pipeline.</summary>
    public bool IsTransaction { get; }

    /// <summary>Queued command names in order, followed by WAIT/WAITAOF for durability executions.</summary>
    /// <remarks>Excludes MULTI/EXEC framing, WATCH setup, keys, values, and protocol housekeeping.</remarks>
    public IReadOnlyList<string> Commands { get; }

    /// <summary>The caller's cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>The final outcome of an explicit execution.</summary>
public enum RespirePipelineOutcome
{
    /// <summary>Every queued command and any durability acknowledgement succeeded.</summary>
    Succeeded,
    /// <summary>An execution or queued command failed.</summary>
    Failed,
    /// <summary>The first execution failure was cancellation.</summary>
    Canceled,
    /// <summary>A watched or conditional transaction aborted without executing its queued commands.</summary>
    Aborted,
    /// <summary>The admission callback threw; no queued command was submitted.</summary>
    Rejected,
}
