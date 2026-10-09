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
        RespireTelemetry.ErrorObservation observation, bool withoutResponseTimeout = false, RespireClient? client = null)
        where TCommand : struct, IRespCommand
    {
        var deadline = client?.Core.Options.CommandTimeout is { } timeout
            ? CommandDeadline.After(Math.Max(1L, (long)timeout.TotalMilliseconds)) : default;
        while (true)
        {
            CircuitAdmission admission = default;
            try
            {
                connection.ThrowIfRetired();
                admission = circuits.Acquire(new(connection.Host, connection.Port), cancellationToken);
                var reply = withoutResponseTimeout
                    ? await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken, pinToConnection: true).ConfigureAwait(false)
                    : await connection.SendAsync(in command, cancellationToken, commandName: operation,
                        commandDeadline: deadline, pinToConnection: true, observation: observation).ConfigureAwait(false);
                admission.Success();
                return reply;
            }
            catch (RespireConnectionRetiredException error) when (!withoutResponseTimeout
                && command.ReadKind != ReadCommandKind.CursorRead && client?.Core.Sentinel is not null
                && client.TryRerouteCircuit(connection, deadline, out var target, out var rerouted, preferredZone: null))
            {
                observation.Handled(error);
                connection = target;
                deadline = rerouted;
            }
            catch (Exception error)
            {
                admission.Failed(error, cancellationToken);
                throw;
            }
            finally { admission.Dispose(); }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal static async ValueTask<ValueTask<RespValue>> EnqueueAsync<TCommand>(StandaloneCircuitRegistry circuits,
        RespireConnection connection, TCommand command, string operation, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation = default, CommandDeadline deadline = default, RespireClient? client = null)
        where TCommand : struct, IRespCommand
        => (await EnqueueWithConnectionAsync(circuits, connection, command, operation, cancellationToken,
            observation, deadline, client).ConfigureAwait(false)).Reply;

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal static async ValueTask<(ValueTask<RespValue> Reply, RespireConnection Connection)> EnqueueWithConnectionAsync<TCommand>(
        StandaloneCircuitRegistry circuits, RespireConnection connection, TCommand command, string operation,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default,
        CommandDeadline deadline = default, RespireClient? client = null)
        where TCommand : struct, IRespCommand
    {
        if (!deadline.IsSet) deadline = connection.CreateCommandDeadline();
        while (true)
        {
            CircuitAdmission admission = default;
            var transferred = false;
            try
            {
                connection.ThrowIfRetired();
                admission = circuits.Acquire(new(connection.Host, connection.Port), cancellationToken);
                var reply = await connection.EnqueuePinnedAsync(command, cancellationToken, operation,
                    observation, deadline: deadline).ConfigureAwait(false);
                // ObserveAsync starts eagerly and owns reply completion even before the caller
                // awaits its returned result. Connection failure/cancellation also completes it.
                var guarded = ObserveAsync(reply, admission, cancellationToken);
                transferred = true;
                return (guarded, connection);
            }
            catch (RespireConnectionRetiredException error) when (command.ReadKind != ReadCommandKind.CursorRead
                && client?.Core.Sentinel is not null
                && client.TryRerouteCircuit(connection, deadline, out var target, out var rerouted, preferredZone: null))
            {
                observation.Handled(error);
                connection = target;
                deadline = rerouted;
            }
            catch (Exception error)
            {
                admission.Failed(error, cancellationToken);
                throw;
            }
            finally { if (!transferred) admission.Dispose(); }
        }
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
