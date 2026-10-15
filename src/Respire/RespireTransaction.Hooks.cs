using Respire.Internal;

namespace Respire;

public abstract partial class RespireTransactionBase
{
    private async ValueTask<bool> CommitHookedAsync(RespirePipelineHook? hook, CancellationToken token, bool validateEmptyWatch)
    {
        using var scope = hook is null ? null : CreateHookScope(hook, token);
        using var members = _client.Core.Options.CommandHook is { } commandHook
            ? new AggregateCommandHookScope(commandHook, _ops, token) : null;
        using var importUsage = PrepareCommit();
        try
        {
            try { scope?.Enter(); members?.Enter(); }
            catch (Exception error)
            {
                foreach (var op in _ops) op.Fail(error);
                // A rejected commit must retire any WATCH lease without executing MULTI/EXEC.
                try { await ReleaseAsync(returnWatchConnection: false).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the admission failure. */ }
                if (DeferredOperation.ReportErrors(_ops) == 0) ErrorObservation.FinishFinal(default, error);
                throw;
            }
            var committed = await CommitWithoutHookAsync(token, validateEmptyWatch, scope, admitted: true).ConfigureAwait(false);
            if (!committed) scope?.Abort();
            return committed;
        }
        catch (Exception error) { scope?.Fail(error); throw; }
    }

    private PipelineHookScope CreateHookScope(RespirePipelineHook hook, CancellationToken token)
    {
        var commands = new string[_ops.Count];
        for (var i = 0; i < _ops.Count; i++) commands[i] = _ops[i].Operation;
        return new(hook, isTransaction: true, commands, token);
    }
}
