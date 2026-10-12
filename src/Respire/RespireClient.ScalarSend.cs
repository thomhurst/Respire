using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // ConvertResponseAsync for the stateless scalar shapes. The converter struct is both the
    // state and a generic argument, so every route is specialized rather than shared-generic,
    // and the immediately admitted sources convert through a direct call instead of a delegate.
    private ValueTask<TResult> ScalarAsync<TCommand, TConverter, TResult>(
        string operation, in TCommand command, CancellationToken ct, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        if (!observation.IsEmpty) return ScalarCoreAsync<TCommand, TConverter, TResult>(operation, command, ct, observation);
        if (TryGetNativeDispatchConnection(operation, in command, out var connection))
            return connection.SendNativeScalarAsync<TCommand, TConverter, TResult>(in command, ct, operation);
        return DispatchResponseSource<TResult>.Run(
            (Client: this, Operation: operation, Command: command, Token: ct),
            static (state, observation) => state.Client.ScalarCoreAsync<TCommand, TConverter, TResult>(
                state.Operation, state.Command, state.Token, observation));
    }

    // Mirrors ConvertResponseCoreAsync route for route; only the standalone and replica ready
    // sends swap in the scalar source.
    private ValueTask<TResult> ScalarCoreAsync<TCommand, TConverter, TResult>(
        string operation, in TCommand command, CancellationToken ct, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        var core = _core;
        if (core.Disposed) ThrowIfDisposedForCommand(observeErrors: false);
        if (TryDispatchReplica<TCommand, TResult, ScalarReadySend<TConverter, TResult>>(
            operation, in command, ct, default, out var replicaResponse, observation))
            return replicaResponse;
        var converter = ScalarResponseConverter<TConverter, TResult>.Delegate;
        if (CanUseDirectReplySource(operation, in command)
            && command is not IStreamingRespCommand)
        {
            if (core.Cluster is null && core.TryGetReadyPrimaryMultiplexer(out var readyMultiplexer))
            {
                // CommandTimeout is enforced by the connection's deadline sweep.
                var cache = core.ClientCache;
                var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
                return SendOnReadyPrimaryAsync<TCommand, TResult, ScalarReadySend<TConverter, TResult>>(
                    operation, readyMultiplexer, command, ct, default, cache, mutationFence,
                    observeSelectionErrors: false, observation: observation);
            }
            else if (TryGetDirectReplyCluster(in command, out var cluster))
            {
                return SendOnReadyClusterAsync<TCommand, TResult, ClusterConvertedReadySend<TConverter, TResult>>(
                    operation, cluster, command, ct, new(default, converter, false), observation);
            }
        }

        return ConvertObservedResponseAsync(operation, command, ct, default(TConverter), converter, false, observation: observation);
    }
}
