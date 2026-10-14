namespace Respire.Internal;

// Only configured callers enter these async wrappers. Explicit dispatch boundaries, rather than
// ambient suppression, ensure callbacks can start unrelated commands with independent scopes.
internal static class CommandHookScope
{
    internal static async ValueTask<TResult> RunAsync<TState, TResult>(RespireCommandHook hook,
        string command, CancellationToken token, TState state, Func<TState, ValueTask<TResult>> execute)
    {
        var context = new RespireCommandContext(command, token);
        object? hookState = null;
        var outcome = RespireCommandOutcome.Rejected;
        Exception? failure = null;
        try
        {
            hookState = hook.OnExecuting(context);
            outcome = RespireCommandOutcome.Succeeded;
            token.ThrowIfCancellationRequested();
            return await execute(state).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (outcome != RespireCommandOutcome.Rejected)
                outcome = error is OperationCanceledException ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed;
            throw;
        }
        finally
        {
            try { hook.OnCompleted(context, hookState, outcome, failure); }
            catch (Exception) { /* Observers must not replace results or disturb ownership. */ }
        }
    }

    internal static async ValueTask RunAsync<TState>(RespireCommandHook hook,
        string command, CancellationToken token, TState state, Func<TState, ValueTask> execute)
    {
        var context = new RespireCommandContext(command, token);
        object? hookState = null;
        var outcome = RespireCommandOutcome.Rejected;
        Exception? failure = null;
        try
        {
            hookState = hook.OnExecuting(context);
            outcome = RespireCommandOutcome.Succeeded;
            token.ThrowIfCancellationRequested();
            await execute(state).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
            if (outcome != RespireCommandOutcome.Rejected)
                outcome = error is OperationCanceledException ? RespireCommandOutcome.Canceled : RespireCommandOutcome.Failed;
            throw;
        }
        finally
        {
            try { hook.OnCompleted(context, hookState, outcome, failure); }
            catch (Exception) { /* Observers must not replace results or disturb ownership. */ }
        }
    }
}
