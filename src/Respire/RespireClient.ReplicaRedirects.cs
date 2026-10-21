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
        Action<RespireEndpoint, RespireConnection?>? onEndpointSelected = null,
        TrackedScriptExecution? execution = null, Action? onTrackingReady = null) where TCommand : struct, IRespCommand
    {
        // Copy borrowed data before the first send or asynchronous routing wait.
        var snapshot = new BacklogCommand<TCommand>(command, operation);
        deadline = CreateCircuitDeadline(deadline);
        return FollowReplicaRedirectsAsync(operation, connection, snapshot, cancellationToken,
            deadline, observation, storedProcedureName, instrument, onEndpointSelected,
            sendAsking, tracking, track, allowStreamingConnectionReroute, execution, onTrackingReady);
    }

    private async ValueTask<RespValue> FollowReplicaRedirectsAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command,
        CancellationToken cancellationToken, CommandDeadline deadline, RespireTelemetry.ErrorObservation observation,
        string? storedProcedureName, bool instrument, Action<RespireEndpoint, RespireConnection?>? onEndpointSelected,
        bool sendAsking, bool tracking, bool track, bool allowStreamingConnectionReroute,
        TrackedScriptExecution? execution, Action? onTrackingReady)
        where TCommand : struct, IRespCommand
    {
        var acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
        var telemetry = instrument && RespireTelemetry.IsOperationEnabled(operation)
            ? RespireTelemetry.StartOperation(operation, connection, _core.Options.Database,
                storedProcedureName: storedProcedureName) : default;
        DedicatedConnectionPool? pool = null;
        RespireConnection? rentedConnection = null;
        ReplicaRedirectTargets.Lease? trackedTarget = null;
        var retainTracking = tracking;
        var continuityPending = false;
        var telemetryEndpoint = new RespireEndpoint(connection.Host, connection.Port);
        RespireConnection? telemetryConnection = connection;
        Exception? failure = null;
        try
        {
            onEndpointSelected?.Invoke(telemetryEndpoint, connection);
            for (var attempt = 0; ; attempt++)
            {
                RespireServerException error;
                try
                {
                    // Register before dispatch, under the same gate as the socket loss
                    // fence. The union covers completed values and overlapping fills.
                    if (track) trackedTarget?.RegisterRead(operation, in command);
                    var reply = execution is null
                        ? await SendReplicaRedirectAttemptAsync(operation, connection, command,
                            cancellationToken, deadline, observation, sendAsking, tracking, track).ConfigureAwait(false)
                        : await SendReplicaRedirectAttemptAsync(operation, connection,
                            new SendTimestampCommand<TCommand>(command, execution),
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
                    if (trackedTarget is not null)
                    {
                        trackedTarget.UseConnection(connection);
                        onTrackingReady?.Invoke();
                    }
                    telemetryEndpoint = new(connection.Host, connection.Port);
                    telemetryConnection = connection;
                    deadline = rerouted;
                    // Maintenance may extend the deadline; subsequent redirect rents share it.
                    acquisition.Dispose();
                    acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    onEndpointSelected?.Invoke(telemetryEndpoint, connection);
                    // Retirement rejected admission; it does not consume a server redirect attempt.
                    attempt--;
                    continue;
                }
                catch (RespireServerException rejection) { error = rejection; }

                if (error.Code != "REDIRECT" || attempt >= ClusterRouter.RedirectLimit
                    || !TryParseReplicaRedirect(error.Message, connection.Host, out var endpoint)) throw error;
                continuityPending = retainTracking;
                execution?.RecordCommandNotApplied();
                // A connected source can grant maintenance time without retiring. Carry
                // that allowance across the redirect, once, before checking or renting.
                deadline = connection.GetReroutedCommandDeadline(deadline);
                acquisition.Dispose();
                acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _core.Options.CommandTimeout);
                if (!retainTracking) _core.ClientCache?.FlushForContinuityLoss();
                command.ValidateAdmission();
                acquisition.CheckDeadline(operation, _core);
                if (observation.IsEmpty)
                    RespireTelemetry.RecordError(error, internallyHandled: true, attempt, new(connection.Host, connection.Port));
                else observation.Handled(error, new(connection.Host, connection.Port));

                if (execution is not null)
                    await execution.ReleaseRedirectTargetAsync().ConfigureAwait(false);

                if (pool is not null)
                {
                    var completedPool = pool;
                    pool = null;
                    completedPool.Return(rentedConnection!);
                    rentedConnection = null;
                    await _core.ReleaseServerPoolAsync(completedPool).ConfigureAwait(false);
                }
                trackedTarget?.Dispose();
                trackedTarget = null;
                // Acquisition can fail before a connection exists. Attribute it to the
                // selected destination without attaching the previous hop's socket.
                telemetryEndpoint = endpoint;
                telemetryConnection = null;
                telemetry.UpdateServerEndpoint(endpoint.Host, endpoint.Port);
                observation.UseEndpoint(endpoint, cancellationToken);
                onEndpointSelected?.Invoke(endpoint, null);
                if (retainTracking && _core.RedirectTargets is { } targets)
                {
                    // The acquisition owner now guarantees the failure fence.
                    continuityPending = false;
                    trackedTarget = await AcquireTrackedReplicaTargetAsync(targets, endpoint, acquisition.Token).ConfigureAwait(false);
                }
                if (trackedTarget is not null)
                {
                    connection = trackedTarget.Connection;
                    // Tracking is part of the completed handshake. Register the fill before
                    // its send, so a subsequent invalidation/disconnect fences publication.
                    onTrackingReady?.Invoke();
                }
                else
                {
                    if (retainTracking)
                    {
                        continuityPending = false;
                        _core.ClientCache?.FlushForContinuityLoss();
                    }
                    pool = _core.CreateServerPool(endpoint, replicaRedirect: true);
                    // The logical command owns the deadline, including any maintenance allowance.
                    rentedConnection = await RentReplicaRedirectTargetAsync(pool, acquisition.Token).ConfigureAwait(false);
                    connection = rentedConnection;
                }
                telemetryConnection = connection;
                onEndpointSelected?.Invoke(endpoint, connection);
                if (execution is not null)
                {
                    // A redirect is proof that the previous attempt did not run. Capture and
                    // validate the target's own identity before any mutation can be submitted.
                    var reliable = await InitializeTrackedRedirectTargetAsync(connection, execution, acquisition.Token).ConfigureAwait(false);
                    execution.Connection = connection;
                    execution.ConnectionIdentity = GetTrackedConnectionIdentity(connection, reliable);
                }
                acquisition.CheckDeadline(operation, _core, connection);
                // Release this admission timer; another redirect can re-arm the remaining deadline.
                acquisition.Dispose();
                sendAsking = false;
                tracking = trackedTarget is not null;
                allowStreamingConnectionReroute = trackedTarget is not null;
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
                // An accepted REDIRECT is a continuity signal even when cancellation,
                // deadline or cleanup prevents handing it to the acquisition owner.
                if (continuityPending) _core.ClientCache?.FlushForContinuityLoss();
                telemetry.Complete(operation, telemetryEndpoint.Host, telemetryEndpoint.Port, _core.Options.Database,
                    storedProcedureName, error: failure, connection: telemetryConnection);
            }
            finally
            {
                trackedTarget?.Dispose();
                if (pool is not null)
                {
                    if (execution is not null)
                        execution.HoldRedirectTarget(_core, pool, rentedConnection);
                    else
                    {
                        if (rentedConnection is not null) pool.Return(rentedConnection);
                        await _core.ReleaseServerPoolAsync(pool).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    private static async ValueTask<bool> InitializeTrackedRedirectTargetAsync(
        RespireConnection connection, TrackedScriptExecution execution, CancellationToken cancellationToken)
    {
        if (!execution.RequiresRedirectIdentity && execution.ConnectionIdentity.ServerClientId == 0) return false;
        try
        {
            await connection.EnsureServerClientIdAsync(cancellationToken).ConfigureAwait(false);
            await Infrastructure.RespireConnectionMultiplexer.ValidateClientKillPermissionAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (RespireServerException error) when (!execution.RequiresRedirectIdentity)
        {
            execution.ErrorObservation.Handled(error);
            return false;
        }
    }

    private async ValueTask<ReplicaRedirectTargets.Lease?> AcquireTrackedReplicaTargetAsync(
        ReplicaRedirectTargets targets, RespireEndpoint endpoint, CancellationToken cancellationToken)
    {
        CircuitAdmission admission;
        try
        {
            _core.ValidateReplicaRedirectDestination(endpoint);
            admission = _core.Circuits?.Acquire(endpoint, cancellationToken) ?? default;
        }
        catch
        {
            // Validation or admission failed before the target owner could fence continuity.
            _core.ClientCache?.FlushForContinuityLoss();
            throw;
        }
        try { return await targets.AcquireAsync(endpoint, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { admission.ConnectionFailed(error, cancellationToken); throw; }
        finally { admission.Dispose(); }
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

    private async ValueTask<RespireConnection> RentReplicaRedirectTargetAsync(
        DedicatedConnectionPool pool, CancellationToken cancellationToken)
    {
        var admission = _core.Circuits?.Acquire(pool.Endpoint, cancellationToken) ?? default;
        try
        {
            // The caller owns the logical deadline. Recovery may retry connection setup,
            // but a successful handshake is not a successful application circuit probe.
            return await pool.RentAsync(cancellationToken, armHandshakeDeadline: false).ConfigureAwait(false);
        }
        catch (Exception error) { admission.ConnectionFailed(error, cancellationToken); throw; }
        finally { admission.Dispose(); }
    }

    internal static bool TryParseReplicaRedirect(string message, string sourceHost, out RespireEndpoint endpoint)
    {
        endpoint = default;
        return message.StartsWith("REDIRECT ", StringComparison.Ordinal)
            && RedirectEndpoint.TryParse(message.AsSpan(9), sourceHost, out endpoint);
    }
}
