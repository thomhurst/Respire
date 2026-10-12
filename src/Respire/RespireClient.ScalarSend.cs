using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // ConvertResponseAsync for the stateless scalar shapes. The converter struct is both the
    // state and a generic argument, so every route is specialized rather than shared-generic,
    // and native admission converts through a direct call instead of a delegate.
    // Observed routes keep ConvertResponseCoreAsync unchanged: the ready-send benchmark
    // control (scripts/benchmarks/ReadySendStrategy.patch) must still build against them.
    private ValueTask<TResult> ScalarAsync<TCommand, TConverter, TResult>(
        string operation, in TCommand command, CancellationToken ct, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        if (!observation.IsEmpty)
            return ConvertResponseCoreAsync(operation, command, ct, default(TConverter),
                ScalarResponseConverter<TConverter, TResult>.Delegate, false, observation);
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeScalarAsync<TCommand, TConverter, TResult>(in command, ct, operation);
        return DispatchResponseSource<TResult>.Run(
            (Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.ConvertResponseCoreAsync(state.Operation,
                state.Command, state.Token, default(TConverter), ScalarResponseConverter<TConverter, TResult>.Delegate,
                false, observation));
    }
}
