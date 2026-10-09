using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    internal ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
        string operation, in TCommand command, CancellationToken ct, TState state,
        ResponseConverter<TState, TResult> converter, bool transferOwnership = false)
        where TCommand : struct, IRespCommand
    {
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeConvertedAsync(in command, ct, operation, state, converter, transferOwnership);
        return DispatchResponseSource<TResult>.Run(
            (Client: this, Operation: operation, Command: command, Token: ct, State: state,
                Converter: converter, Transfer: transferOwnership),
            static (state, observation) => state.Client.ConvertResponseCoreAsync(state.Operation,
                state.Command, state.Token, state.State, state.Converter, state.Transfer, observation));
    }

    internal ValueTask<string?> StringOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeStringAsync(in command, ct, operation);
        return DispatchResponseSource<string?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.StringOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));
    }

    internal ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeBytesAsync(in command, ct, operation);
        return DispatchResponseSource<byte[]?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.BytesOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));
    }

    private bool TryGetNativeDispatchConnection<TCommand>(string operation, in TCommand command,
        out RespireConnection connection)
        where TCommand : struct, IRespCommand
    {
        connection = null!;
        // These routes can retry or clean up after native inspection. They retain the
        // generation-bound owner, including the logical cache mutation fence.
        var core = _core;
        try
        {
            if (core.Cluster is not null || core.Sentinel is not null || core.Circuits is not null
                || core.ClientCache is not null || command is IStreamingRespCommand
                || ScriptingEngineInfo.IsScriptingCommand(operation)
                || !CanUseDirectReplySource(operation, in command)
                || !core.TryGetReadyPrimaryMultiplexer(out var multiplexer)) return false;
            ObjectDisposedException.ThrowIf(core.Disposed, this);
            connection = multiplexer.GetConnection();
            return true;
        }
        catch (Exception error)
        {
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }
}
