namespace Respire;

/// <summary>Admits and observes supported logical command executions.</summary>
/// <remarks>
/// Covers ordinary converted commands, single-key cached GET, non-interpolated Execute APIs,
/// script execution, blocking converted replies, ordinary cursor pages, streaming results and
/// explicit subscription setup/teardown. Downloads complete on stream
/// transfer, not later payload consumption. Uploads retain borrowed buffers through completion.
/// Also covers specialized server dispatch, cluster scan pages and stream appends including
/// automatic IDMP retries and capability fallback. Other specialized owners
/// and explicit aggregate members remain outside this contract; this is not a
/// client-wide blocking policy. Callbacks may run concurrently and reentrantly. A command started
/// by a callback has its own callbacks; applications must prevent unbounded recursion.
/// </remarks>
public abstract class RespireCommandHook
{
    /// <summary>Creates a command hook.</summary>
    protected RespireCommandHook() { }

    /// <summary>Runs synchronously before dispatch or cache lookup. Throw to reject the operation.</summary>
    /// <returns>Optional state passed to completion.</returns>
    public abstract object? OnExecuting(RespireCommandContext context);

    /// <summary>Observes the final result, including rejection. Exceptions from this callback are ignored.</summary>
    /// <remarks>State is null when admission throws. Raw results complete before later caller-selected conversions.</remarks>
    public abstract void OnCompleted(RespireCommandContext context, object? state,
        RespireCommandOutcome outcome, Exception? error);
}

/// <summary>An immutable description of one logical command; safe to retain after completion.</summary>
public sealed class RespireCommandContext
{
    internal RespireCommandContext(string command, CancellationToken cancellationToken)
    {
        Command = command;
        CancellationToken = cancellationToken;
    }

    /// <summary>The normalized command name, excluding keys and values.</summary>
    public string Command { get; }

    /// <summary>The caller's cancellation token.</summary>
    public CancellationToken CancellationToken { get; }
}

/// <summary>The final outcome at the command API's result boundary.</summary>
public enum RespireCommandOutcome
{
    /// <summary>The command API completed successfully.</summary>
    Succeeded,
    /// <summary>Dispatch or reply conversion failed.</summary>
    Failed,
    /// <summary>The command API completed with an OperationCanceledException.</summary>
    Canceled,
    /// <summary>Admission threw; this operation did not dispatch or read the cache.</summary>
    Rejected,
}
