using System.Runtime.CompilerServices;
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
        string? storedProcedureName = null, bool instrument = true,
        Action<RespireConnection>? onConnectionSelected = null) where TCommand : struct, IRespCommand
    {
        // Copy borrowed data before the first send or asynchronous routing wait.
        var snapshot = new BacklogCommand<TCommand>(command, operation);
        deadline = CreateCircuitDeadline(deadline);
        return FollowReplicaRedirectsAsync(operation, connection, snapshot, cancellationToken,
            deadline, observation, storedProcedureName, instrument, onConnectionSelected,
            sendAsking, tracking, track, allowStreamingConnectionReroute);
    }

    private async ValueTask<RespValue> FollowReplicaRedirectsAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command,
        CancellationToken cancellationToken, CommandDeadline deadline, RespireTelemetry.ErrorObservation observation,
        string? storedProcedureName, bool instrument, Action<RespireConnection>? onConnectionSelected,
        bool sendAsking, bool tracking, bool track, bool allowStreamingConnectionReroute)
        where TCommand : struct, IRespCommand
    {
        var acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
        var telemetry = instrument && RespireTelemetry.IsOperationEnabled(operation)
            ? RespireTelemetry.StartOperation(operation, connection, _core.Options.Database,
                storedProcedureName: storedProcedureName) : default;
        DedicatedConnectionPool? pool = null;
        RespireConnection? rentedConnection = null;
        Exception? failure = null;
        try
        {
            onConnectionSelected?.Invoke(connection);
            for (var attempt = 0; ; attempt++)
            {
                RespireServerException error;
                try
                {
                    var reply = await SendReplicaRedirectAttemptAsync(operation, connection, command,
                        cancellationToken, deadline, observation, sendAsking, tracking, track).ConfigureAwait(false);
                    if (!reply.IsError) return reply;
                    try { error = ResponseReader.ServerError(in reply, operation, new(connection.Host, connection.Port)); }
                    finally { reply.Dispose(); }
                }
                catch (RespireConnectionRetiredException retired) when (allowStreamingConnectionReroute
                    && TryRerouteCircuit(connection, deadline, out var target, out var rerouted, GetTransportReadZone(in command)))
                {
                    observation.Handled(retired, new(connection.Host, connection.Port));
                    connection = target;
                    deadline = rerouted;
                    // Maintenance may extend the deadline; subsequent redirect rents share it.
                    acquisition.Dispose();
                    acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    onConnectionSelected?.Invoke(connection);
                    // Retirement rejected admission; it does not consume a server redirect attempt.
                    attempt--;
                    continue;
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
                    completedPool.Return(rentedConnection!);
                    rentedConnection = null;
                    await _core.ReleaseServerPoolAsync(completedPool).ConfigureAwait(false);
                }
                pool = _core.CreateServerPool(endpoint, replicaRedirect: true);
                // The logical command owns the deadline, including any maintenance allowance.
                rentedConnection = await pool.RentAsync(acquisition.Token, armHandshakeDeadline: false).ConfigureAwait(false);
                connection = rentedConnection;
                telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                onConnectionSelected?.Invoke(connection);
                acquisition.CheckDeadline(operation, _core, connection);
                // Release this admission timer; another redirect can re-arm the remaining deadline.
                acquisition.Dispose();
                sendAsking = false;
                tracking = false;
                allowStreamingConnectionReroute = false;
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
                    if (rentedConnection is not null) pool.Return(rentedConnection);
                    await _core.ReleaseServerPoolAsync(pool).ConfigureAwait(false);
                }
            }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendReplicaRedirectAttemptAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        CommandDeadline deadline, RespireTelemetry.ErrorObservation observation, bool sendAsking, bool tracking, bool track)
        where TCommand : struct, IRespCommand
    {
        // Keep every attempt pinned so this logical owner sees all endpoint changes,
        // including maintenance retirement before the first REDIRECT response.
        connection.ThrowIfRetired();
        var admission = (tracking ? _core.Circuits is not null && !_snapshotPrefixedBinaryKeys
            : UsesCircuitResponsePath(operation, pinToConnection: false))
            ? AcquireCircuit(connection, cancellationToken) : default;
        try
        {
            var response = tracking
                ? await SendTrackedOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                    sendAsking, track, deadline, pinToConnection: true, observation: observation).ConfigureAwait(false)
                : await SendOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                    sendAsking, deadline, allowStreamingConnectionReroute: false, pinToConnection: true,
                    observation: observation).ConfigureAwait(false);
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

    internal static bool TryParseReplicaRedirect(string message, string sourceHost, out RespireEndpoint endpoint)
    {
        endpoint = default;
        return message.StartsWith("REDIRECT ", StringComparison.Ordinal)
            && RedirectEndpoint.TryParse(message.AsSpan(9), sourceHost, out endpoint);
    }
}
