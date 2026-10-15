using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireBatch
{
    private PipelineHookScope CreateHookScope(RespirePipelineHook hook, CancellationToken token, string? acknowledgement = null)
    {
        var commands = new string[_ops.Count + (acknowledgement is null ? 0 : 1)];
        for (var i = 0; i < _ops.Count; i++) commands[i] = _ops[i].Operation;
        if (acknowledgement is not null) commands[^1] = acknowledgement;
        return new(hook, isTransaction: false, commands, token);
    }

    private async ValueTask<RespireBatchResult> TryExecuteHookedAsync(RespirePipelineHook? hook, CancellationToken token)
    {
        using var scope = hook is null ? null : CreateHookScope(hook, token);
        using var members = _client.Core.Options.CommandHook is { } commandHook
            ? new AggregateCommandHookScope(commandHook, _ops, token) : null;
        using var importUsage = EnterObservedOperation();
        _sent = true;
        try
        {
            try { scope?.Enter(); members?.Enter(); }
            catch (Exception error)
            {
                scope?.Fail(error);
                foreach (var op in _ops) op.Fail(error);
                // An empty result cannot carry a failure. Empty admission rejection therefore throws.
                if (_ops.Count == 0) { ErrorObservation.FinishFinal(default, error); throw; }
                return new RespireBatchResult(_ops.Count, CollectFailures(_ops));
            }
            var result = await TryExecuteCoreAsync(token, admitted: true).ConfigureAwait(false);
            scope?.Fail(result.FirstError);
            return result;
        }
        catch (Exception error) { scope?.Fail(error); throw; }
    }

    private async ValueTask<T> ExecuteAcknowledgementHookedAsync<TCommand, T>(RespirePipelineHook? hook,
        string operation, TCommand acknowledgement, Func<RespValue, T> convert, CancellationToken token)
        where TCommand : struct, IRespCommand
    {
        // With command hooks, cancellation is observed after all member admissions, like
        // ordinary command execution. Structural validation still precedes callbacks.
        var slot = ValidateDurabilityAdmission(_client.Core.Options.CommandHook is null ? token : default);
        _sent = true;
        using var scope = hook is null ? null : CreateHookScope(hook, token, operation);
        using var members = _client.Core.Options.CommandHook is { } commandHook
            ? new AggregateCommandHookScope(commandHook, _ops, token, operation) : null;
        try
        {
            try { scope?.Enter(); members?.Enter(); }
            catch (Exception error)
            {
                foreach (var op in _ops) op.Fail(error);
                DeferredOperation.ReportErrors(_ops);
                throw;
            }
            return await ExecuteAcknowledgementCoreAsync(operation, acknowledgement, convert, token,
                admitted: true, admittedSlot: slot).ConfigureAwait(false);
        }
        catch (Exception error) { scope?.Fail(error); members?.FailAcknowledgement(error); throw; }
    }
}
