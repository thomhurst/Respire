using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Networking;
using Respire.Protocol;

namespace Respire.Internal;

// Deferred queues acquire only when executing. Each reply owns its permit, independently
// of other commands in the pipeline; accepted frames keep their existing FIFO placeholders.
internal static class QueuedCircuitDispatch
{
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal static async ValueTask<RespValue> SendAsync<TCommand>(StandaloneCircuitRegistry circuits,
        RespireConnection connection, TCommand command, string operation, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool withoutResponseTimeout = false)
        where TCommand : struct, IRespCommand
    {
        var admission = circuits.Acquire(new(connection.Host, connection.Port), cancellationToken);
        try
        {
            var reply = withoutResponseTimeout
                ? await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken, pinToConnection: true).ConfigureAwait(false)
                : await connection.SendAsync(in command, cancellationToken, commandName: operation,
                    pinToConnection: true, observation: observation).ConfigureAwait(false);
            admission.Success();
            return reply;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal static async ValueTask<ValueTask<RespValue>> EnqueueAsync<TCommand>(StandaloneCircuitRegistry circuits,
        RespireConnection connection, TCommand command, string operation, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var admission = circuits.Acquire(new(connection.Host, connection.Port), cancellationToken);
        var transferred = false;
        try
        {
            var reply = await connection.EnqueuePinnedAsync(command, cancellationToken, operation).ConfigureAwait(false);
            // ObserveAsync starts eagerly and owns reply completion even before the caller
            // awaits its returned result. Connection failure/cancellation also completes it.
            var guarded = ObserveAsync(reply, admission, cancellationToken);
            transferred = true;
            return guarded;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { if (!transferred) admission.Dispose(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<RespValue> ObserveAsync(ValueTask<RespValue> reply,
        CircuitAdmission admission, CancellationToken cancellationToken)
    {
        try
        {
            var response = await reply.ConfigureAwait(false);
            admission.Success();
            return response;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }
}
