using Respire.Internal;

namespace Respire;

public abstract partial class RespireTransactionBase
{
    private async ValueTask<bool> CommitHookedAsync(RespirePipelineHook hook, CancellationToken token, bool validateEmptyWatch)
    {
        var commands = new string[_ops.Count];
        for (var i = 0; i < _ops.Count; i++) commands[i] = _ops[i].Operation;
        using var scope = new PipelineHookScope(hook, isTransaction: true, commands, token);
        using var importUsage = PrepareCommit();
        try
        {
            try { scope.Enter(); }
            catch (Exception error)
            {
                foreach (var op in _ops) op.Fail(error);
                // A rejected commit must retire any WATCH lease without executing MULTI/EXEC.
                try { await ReleaseAsync(returnWatchConnection: false).ConfigureAwait(false); }
                catch (Exception) { /* Preserve the admission failure. */ }
                if (DeferredOperation.ReportErrors(_ops) == 0) ErrorObservation.FinishFinal(default, error);
                throw;
            }
            var committed = await CommitWithoutHookAsync(token, validateEmptyWatch, scope).ConfigureAwait(false);
            if (!committed) scope.Abort();
            return committed;
        }
        catch (Exception error) { scope.Fail(error); throw; }
    }
}
