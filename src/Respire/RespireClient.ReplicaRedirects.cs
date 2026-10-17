using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<RespValue> SendWithReplicaRedirectsAsync<TCommand>(
        string operation, RespireConnection connection, in TCommand command, CancellationToken cancellationToken,
        CommandDeadline deadline, RespireTelemetry.ErrorObservation observation, bool sendAsking = false,
        bool tracking = false, bool track = true, bool allowStreamingConnectionReroute = true,
        string? storedProcedureName = null, bool instrument = true) where TCommand : struct, IRespCommand
    {
        // Copy borrowed data before the first send or asynchronous routing wait.
        var snapshot = new BacklogCommand<TCommand>(command, operation);
        deadline = CreateCircuitDeadline(deadline);
        var pending = tracking
            ? SendTrackedOnConnectionCoreAsync(operation, connection, snapshot, cancellationToken, sendAsking, track,
                observation, deadline)
            : SendOnConnectionCoreAsync(operation, connection, snapshot, cancellationToken, sendAsking,
                deadline, allowStreamingConnectionReroute, observation: observation);
        return FollowReplicaRedirectsAsync(operation, connection, snapshot, pending, cancellationToken,
            deadline, observation, storedProcedureName, instrument);
    }

    private async ValueTask<RespValue> FollowReplicaRedirectsAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, ValueTask<RespValue> pending,
        CancellationToken cancellationToken, CommandDeadline deadline, RespireTelemetry.ErrorObservation observation,
        string? storedProcedureName, bool instrument)
        where TCommand : struct, IRespCommand
    {
        var acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
        var telemetry = instrument && RespireTelemetry.IsOperationEnabled(operation)
            ? RespireTelemetry.StartOperation(operation, connection, _core.Options.Database,
                storedProcedureName: storedProcedureName) : default;
        DedicatedConnectionPool? pool = null;
        Exception? failure = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                RespireServerException error;
                try
                {
                    var reply = await pending.ConfigureAwait(false);
                    if (!reply.IsError) return reply;
                    try { error = ResponseReader.ServerError(in reply, operation, new(connection.Host, connection.Port)); }
                    finally { reply.Dispose(); }
                }
                catch (RespireServerException rejection) { error = rejection; }

                if (error.Code != "REDIRECT" || attempt >= ClusterRouter.RedirectLimit
                    || !TryParseReplicaRedirect(error.Message, connection.Host, out var endpoint)) throw error;
                _core.ClientCache?.FlushForContinuityLoss();
                command.ValidateAdmission();
                acquisition.CheckDeadline(operation, _core);
                if (observation.IsEmpty)
                    RespireTelemetry.RecordError(error, internallyHandled: true, attempt, new(connection.Host, connection.Port));
                else observation.Handled(error, new(connection.Host, connection.Port));

                if (pool is not null)
                {
                    var completedPool = pool;
                    pool = null;
                    completedPool.Return(connection);
                    await _core.ReleaseServerPoolAsync(completedPool).ConfigureAwait(false);
                }
                pool = _core.CreateServerPool(endpoint, replicaRedirect: true);
                connection = await pool.RentAsync(acquisition.Token).ConfigureAwait(false);
                acquisition.CheckDeadline(operation, _core, connection);
                // Release this admission timer; another redirect can re-arm the remaining deadline.
                acquisition.Dispose();
                pending = SendOnConnectionCoreAsync(operation, connection, command, cancellationToken,
                    commandDeadline: deadline, allowStreamingConnectionReroute: false, observation: observation);
            }
        }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        {
            failure = acquisition.CreateTimeout(operation, _core, cause: error);
            throw failure;
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        {
            failure = new OperationCanceledException(error.Message, error, cancellationToken);
            throw failure;
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            acquisition.Dispose();
            try
            {
                telemetry.Complete(operation, connection.Host, connection.Port, _core.Options.Database,
                    storedProcedureName, error: failure, connection: connection);
            }
            finally
            {
                if (pool is not null)
                {
                    pool.Return(connection);
                    await _core.ReleaseServerPoolAsync(pool).ConfigureAwait(false);
                }
            }
        }
    }

    internal static bool TryParseReplicaRedirect(string message, string sourceHost, out RespireEndpoint endpoint)
    {
        endpoint = default;
        return message.StartsWith("REDIRECT ", StringComparison.Ordinal)
            && RedirectEndpoint.TryParse(message.AsSpan(9), sourceHost, out endpoint);
    }
}
