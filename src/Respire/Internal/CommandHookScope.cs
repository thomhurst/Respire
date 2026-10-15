namespace Respire.Internal;

// Only configured callers enter these async wrappers. Explicit dispatch boundaries, rather than
// ambient suppression, ensure callbacks can start unrelated commands with independent scopes.
internal static class CommandHookScope
{
    // Composite owners retain the same error publisher and direct dispatch path when hooks
    // are absent. Borrowed legs never call this helper: only the public result owner admits.
    internal static ValueTask<TResult> RunObservedAsync<TState, TResult>(RespireCommandHook? hook,
        string command, CancellationToken token, TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask<TResult>> execute,
        Action<TState>? preflight = null)
        => hook is null
            ? DispatchResponseSource<TResult>.Run(state, execute)
            : DispatchResponseSource<TResult>.Run(
                (Hook: hook, Command: command, Token: token, State: state, Execute: execute, Preflight: preflight),
                static (s, owner) =>
                {
                    s.Preflight?.Invoke(s.State);
                    return RunAsync(s.Hook, s.Command, s.Token,
                        (s.State, s.Execute, Owner: owner), static s => s.Execute(s.State, s.Owner));
                });

    internal static ValueTask RunObservedAsync<TState>(RespireCommandHook? hook,
        string command, CancellationToken token, TState state,
        Func<TState, RespireTelemetry.ErrorObservation, ValueTask> execute,
        Action<TState>? preflight = null)
        => hook is null
            ? DispatchResponseSource.Run(state, execute)
            : DispatchResponseSource.Run(
                (Hook: hook, Command: command, Token: token, State: state, Execute: execute, Preflight: preflight),
                static (s, owner) =>
                {
                    s.Preflight?.Invoke(s.State);
                    return RunAsync(s.Hook, s.Command, s.Token,
                        (s.State, s.Execute, Owner: owner), static s => s.Execute(s.State, s.Owner));
                });

    internal static async ValueTask<TResult> RunAsync<TState, TResult>(RespireCommandHook hook,
        string command, CancellationToken token, TState state, Func<TState, ValueTask<TResult>> execute,
        Func<TState, string>? preflight = null)
    {
        if (preflight is not null) command = preflight(state);
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
        string command, CancellationToken token, TState state, Func<TState, ValueTask> execute,
        Func<TState, string>? preflight = null)
    {
        if (preflight is not null) command = preflight(state);
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
