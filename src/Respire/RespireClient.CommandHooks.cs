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
            ? CommandHookScope.RunAsync(hook, command.Name, cancellationToken,
                (Client: this, Command: command, Args: args, Flags: flags, Token: cancellationToken),
                static s => s.Client.ExecuteOwnedCommandUnhookedAsync(s.Command, s.Args, s.Flags, s.Token),
                static s => s.Client.ValidateHookCommand(s.Command, s.Args, s.Flags, fireAndForget: false))
            : ExecuteOwnedCommandUnhookedAsync(command, args, flags, cancellationToken);

    private ValueTask<RespireResult> ExecuteCommandAsync(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags, CancellationToken cancellationToken)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, command.Name, cancellationToken,
                (Client: this, Command: command, Args: args, Flags: flags, Token: cancellationToken),
                static s => s.Client.ExecuteCommandUnhookedAsync(s.Command, s.Args, s.Flags, s.Token),
                static s => s.Client.ValidateHookCommand(s.Command, s.Args, s.Flags, fireAndForget: false))
            : ExecuteCommandUnhookedAsync(command, args, flags, cancellationToken);

    private ValueTask ExecuteCommandFireAndForgetAsync(RespireCommand command, RespireValue[] args,
        CancellationToken cancellationToken)
        => _core.Options.CommandHook is { } hook
            ? CommandHookScope.RunAsync(hook, command.Name, cancellationToken,
                (Client: this, Command: command, Args: args, Token: cancellationToken),
                static s => s.Client.ExecuteCommandFireAndForgetUnhookedAsync(s.Command, s.Args, s.Token),
                static s => s.Client.ValidateHookCommand(s.Command, s.Args, RespireCommandFlags.None, fireAndForget: true))
            : ExecuteCommandFireAndForgetUnhookedAsync(command, args, cancellationToken);

    // Only the opt-in hook path needs this early validation. Keep using the dispatch validators
    // so rejection cannot mask their errors, without adding work to unconfigured calls.
    private string ValidateHookCommand(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags, bool fireAndForget)
    {
        string operation;
        if (command.IsCallerSupplied)
        {
            operation = command.Name;
            if (_keyPrefix is not null && IsModuleCommand(operation))
            {
                var error = PrefixModuleKeysOrError(operation.ToUpperInvariant(), args, out args);
                if (error is not null) throw error;
            }
        }
        else if (!TryGetPreencodedRawOperation(command, args, out operation, out args))
        {
            if (_keyPrefix is null || !RawCommandKeyLayouts.HasPrefixableLayout(command.Name))
            {
                if (!fireAndForget) ValidateResultFlags(flags);
                ValidateCatalogCommand(command);
                args = PrefixCatalogKeys(command.Name, args);
                if (fireAndForget && command.IsBlocking(args))
                    throw new NotSupportedException($"{command.Name} can block and cannot run through ExecuteFireAndForgetAsync.");
                _ = StoredProcedureName(command.Name, args);
                _ = ValidateClusterRawKeys(command.Name, args);
                if (!fireAndForget && _core.Cluster is not null
                    && DynamicCommandRouting.IsClusterWideMutation(command.Name, args))
                    ValidateClusterWideFlags(command.Name, flags);
                return command.Name;
            }
            operation = command.Name;
        }

        if (!command.IsCallerSupplied && _keyPrefix is not null)
        {
            var error = PrefixModuleKeysOrError(operation, args, out args);
            if (error is not null) throw error;
        }
        if (!fireAndForget) ValidateResultFlags(flags);
        var parsed = ParseRawCommand(operation, ref args);
        if (fireAndForget)
            ValidateRawFireAndForgetCommand(parsed.Operation, parsed.Words.AsSpan(parsed.FirstArgumentIndex), args);
        _ = CreateRawCommand(parsed.Operation, parsed.Words, parsed.FirstArgumentIndex, args);
        if (!fireAndForget && _core.Cluster is not null
            && DynamicCommandRouting.IsClusterWideMutation(parsed.Operation, args))
            ValidateClusterWideFlags(parsed.Operation, flags);
        // Raw descriptors may contain inline arguments. Never expose those as command names.
        return command.IsCallerSupplied ? parsed.Operation : command.Name;
    }
}
