using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    internal ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
        string operation, in TCommand command, CancellationToken ct, TState state,
        ResponseConverter<TState, TResult> converter, bool transferOwnership = false,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, operation, ct,
                (Client: this, Operation: operation, Command: command, Token: ct, State: state,
                    Converter: converter, Transfer: transferOwnership, Observation: observation),
                static s => s.Client.ConvertResponseUnhookedAsync(s.Operation, s.Command, s.Token,
                    s.State, s.Converter, s.Transfer, s.Observation))
            : ConvertResponseUnhookedAsync(operation, command, ct, state, converter, transferOwnership, observation);

    internal ValueTask<string?> StringOrNullAsync<TCommand>(string operation, in TCommand command,
        CancellationToken ct, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, operation, ct,
                (Client: this, Operation: operation, Command: command, Token: ct, Observation: observation),
                static s => s.Client.StringOrNullUnhookedAsync(s.Operation, s.Command, s.Token, s.Observation))
            : StringOrNullUnhookedAsync(operation, command, ct, observation);

    internal ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, in TCommand command,
        CancellationToken ct, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, operation, ct,
                (Client: this, Operation: operation, Command: command, Token: ct, Observation: observation),
                static s => s.Client.BytesOrNullUnhookedAsync(s.Operation, s.Command, s.Token, s.Observation))
            : BytesOrNullUnhookedAsync(operation, command, ct, observation);

    internal ValueTask<TResult> CachedGetAsync<TResult>(RespireKey resolvedKey, CancellationToken cancellationToken,
        ResponseConverter<RespireClient, TResult> converter, bool observeErrors = true,
        RespireTelemetry.ErrorObservation observation = default)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "GET", cancellationToken,
                (Client: this, Key: resolvedKey, Token: cancellationToken, Converter: converter,
                    Observe: observeErrors, Observation: observation),
                static s => s.Client.CachedGetUnhookedAsync(s.Key, s.Token, s.Converter, s.Observe, s.Observation))
            : CachedGetUnhookedAsync(resolvedKey, cancellationToken, converter, observeErrors, observation);

    internal ValueTask<string?> CachedGetStringAsync(RespireKey resolvedKey, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, "GET", cancellationToken,
                (Client: this, Key: resolvedKey, Token: cancellationToken, Observation: observation),
                static s => s.Client.CachedGetStringUnhookedAsync(s.Key, s.Token, s.Observation))
            : CachedGetStringUnhookedAsync(resolvedKey, cancellationToken, observation);

    private ValueTask<RespireResult> ExecuteOwnedCommandAsync(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags, CancellationToken cancellationToken)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, HookCommandName(command, args), cancellationToken,
                (Client: this, Command: command, Args: args, Flags: flags, Token: cancellationToken),
                static s => s.Client.ExecuteOwnedCommandUnhookedAsync(s.Command, s.Args, s.Flags, s.Token))
            : ExecuteOwnedCommandUnhookedAsync(command, args, flags, cancellationToken);

    private ValueTask<RespireResult> ExecuteCommandAsync(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags, CancellationToken cancellationToken)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, HookCommandName(command, args), cancellationToken,
                (Client: this, Command: command, Args: args, Flags: flags, Token: cancellationToken),
                static s => s.Client.ExecuteCommandUnhookedAsync(s.Command, s.Args, s.Flags, s.Token))
            : ExecuteCommandUnhookedAsync(command, args, flags, cancellationToken);

    private ValueTask ExecuteCommandFireAndForgetAsync(RespireCommand command, RespireValue[] args,
        CancellationToken cancellationToken)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, HookCommandName(command, args), cancellationToken,
                (Client: this, Command: command, Args: args, Token: cancellationToken),
                static s => s.Client.ExecuteCommandFireAndForgetUnhookedAsync(s.Command, s.Args, s.Token))
            : ExecuteCommandFireAndForgetUnhookedAsync(command, args, cancellationToken);

    // Raw descriptors may contain inline arguments. Never expose those as command names.
    private static string HookCommandName(RespireCommand command, RespireValue[] args)
        => command.IsCallerSupplied ? ParseRawCommand(command.Name, ref args).Operation : command.Name;
}
