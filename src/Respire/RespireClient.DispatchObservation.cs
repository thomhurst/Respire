using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    internal ValueTask<TResult> ConvertResponseAsync<TCommand, TState, TResult>(
        string operation, in TCommand command, CancellationToken ct, TState state,
        ResponseConverter<TState, TResult> converter, bool transferOwnership = false)
        where TCommand : struct, IRespCommand
        => DispatchResponseSource<TResult>.Run(
            (Client: this, Operation: operation, Command: command, Token: ct, State: state,
                Converter: converter, Transfer: transferOwnership),
            static (state, observation) => state.Client.ConvertResponseCoreAsync(state.Operation,
                state.Command, state.Token, state.State, state.Converter, state.Transfer, observation));

    internal ValueTask<string?> StringOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => DispatchResponseSource<string?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.StringOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));

    internal ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, in TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
        => DispatchResponseSource<byte[]?>.Run((Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.BytesOrNullCoreAsync(state.Operation, state.Command, state.Token, observation));
}
