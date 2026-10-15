using Respire.Internal;

namespace Respire;

public sealed partial class RespireClient
{
    // Typed module owners retain admission through conversion and their recovery legs.
    // Opaque decorators never enter this path: modules still dispatch through their interface.
    internal ValueTask<TResult> ExecuteModuleAsync<TResult>(RespireCommand command, RespireValue[] arguments,
        CancellationToken token,
        Func<ValueTask<RespireResult>, RespireTelemetry.ErrorObservation, ValueTask<TResult>> convert)
        => CommandHookScope.RunObservedAsync(_core.Options.CommandHook, command.Name, token,
            (Client: this, Command: command, Arguments: arguments, Token: token, Convert: convert),
            static (s, owner) => s.Client.ExecuteModuleUnhookedAsync(s.Command, s.Arguments, s.Token, s.Convert, owner),
            static s => s.Client.ValidateHookCommand(s.Command, s.Arguments, RespireCommandFlags.None, fireAndForget: false));

    private async ValueTask<TResult> ExecuteModuleUnhookedAsync<TResult>(RespireCommand command, RespireValue[] arguments,
        CancellationToken token,
        Func<ValueTask<RespireResult>, RespireTelemetry.ErrorObservation, ValueTask<TResult>> convert,
        RespireTelemetry.ErrorObservation owner)
    {
        token.ThrowIfCancellationRequested();
        if (_core.Options.CommandHook is null)
            ValidateHookCommand(command, arguments, RespireCommandFlags.None, fireAndForget: false);
        // The normal dispatch fence still owns physical FIFO retirement. This outer fence keeps
        // cache publication excluded until the module has accepted the typed reply and cleaned up.
        var cache = _core.ClientCache;
        var fence = cache is null || command.CacheMutation == RespireCacheMutation.ReadOnly
            ? default : cache.BeginUnknownMutation();
        var succeeded = false;
        try
        {
            var result = await convert(ExecuteModuleBorrowedAsync(command, arguments, token, owner), owner).ConfigureAwait(false);
            succeeded = true;
            return result;
        }
        finally
        {
            if (fence.IsRequired)
            {
                if (succeeded) cache!.CompleteMutation(in fence, true);
                else
                {
                    try { cache!.CompleteMutation(in fence, false); }
                    catch (Exception) { /* Preserve the command or converter failure. */ }
                }
            }
        }
    }

    internal ValueTask<RespireResult> ExecuteModuleBorrowedAsync(RespireCommand command, RespireValue[] arguments,
        CancellationToken token, RespireTelemetry.ErrorObservation owner)
    {
        try { return ExecuteCommandUnhookedAsync(command, arguments, RespireCommandFlags.None, token, owner); }
        catch (Exception error) { return ValueTask.FromException<RespireResult>(error); }
    }
}
