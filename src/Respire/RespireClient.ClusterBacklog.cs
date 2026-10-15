using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<Stream?> SendBackloggedClusterBulkStreamAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, int? slot, RespireReadFrom readFrom,
        RespireTelemetry.ErrorObservation observation, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
    {
        deadline = CreateCircuitDeadline(deadline);
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunClusterBacklogAsync(operation, slot, readFrom, cancellationToken, deadline, observation,
            (ready, originalDeadline) => SendClusterBulkStreamAsync(operation, cluster, snapshot, cancellationToken,
                observation, ready, originalDeadline));
    }

    private async ValueTask SendBackloggedClosingClusterAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, int? slot, string? storedProcedureName,
        bool allowReadFrom, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
    {
        deadline = CreateCircuitDeadline(deadline);
        var snapshot = new BacklogCommand<TCommand>(command);
        await RunClusterBacklogAsync(operation, slot, RespireReadFrom.Primary, cancellationToken, deadline,
            observation, async (ready, originalDeadline) =>
            {
                await SendFireAndForgetClusterAsync(operation, cluster, snapshot, cancellationToken,
                    storedProcedureName, allowReadFrom, observation, ready, originalDeadline).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
    }

    private ValueTask<RespValue> SendBackloggedHedgedClusterAsync<TCommand>(string operation, TCommand command,
        HedgedReadBudget budget, RespireCommandFlags flags, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, int? slot, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
    {
        deadline = CreateCircuitDeadline(deadline);
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunClusterBacklogAsync(operation, slot, _readFrom, cancellationToken, deadline, observation,
            (ready, originalDeadline) => SendHedgedReadAsync(operation, snapshot, budget, flags, cancellationToken,
                observation, observeErrors: false, initialConnection: ready, deadline: originalDeadline));
    }

    // Keep snapshots and delegate closures on the cold path, outside normal routed sends.
    private ValueTask<RespValue> SendBackloggedClusterAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, int? slot, RespireReadFrom readFrom,
        string? storedProcedureName, bool noRedirect, int firstAttempt,
        RespireConnectionRetiredException? initialRetirement, RespireServerException? initialRejection,
        bool allowReadFrom, RespireReadFrom? readFromOverride, ReadAffinity? cursorAffinity,
        HedgeOriginalRoute? hedgeOriginalRoute, bool isHedge, RespireReadFrom? cursorReadFromOverride,
        bool suppressTelemetry, ClusterScriptTelemetry? scriptTelemetry, bool discardServerErrors,
        Action<Exception>? onRetry, RespireTelemetry.ErrorObservation observation, CommandDeadline retryDeadline)
        where TCommand : struct, IRespCommand
    {
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunClusterBacklogAsync(operation, slot, readFrom, cancellationToken, retryDeadline, observation,
            (ready, deadline) => SendClusterAsync(operation, cluster, snapshot, cancellationToken,
                storedProcedureName, noRedirect, ready, firstAttempt, initialRetirement, initialRejection,
                allowReadFrom, readFromOverride, cursorAffinity, hedgeOriginalRoute, isHedge,
                cursorReadFromOverride, suppressTelemetry, scriptTelemetry, observeErrors: false,
                discardServerErrors: discardServerErrors, onRetry: onRetry, observation: observation,
                retryDeadline: deadline));
    }

    private ValueTask<RespValue> SendBackloggedTrackedClusterAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, int? slot, RespireReadFrom readFrom,
        Action? onRedirect, bool track, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
    {
        deadline = CreateCircuitDeadline(deadline);
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunClusterBacklogAsync(operation, slot, readFrom, cancellationToken, deadline, observation,
            (ready, originalDeadline) => SendTrackedClusterAsync(operation, cluster, snapshot, cancellationToken,
                onRedirect, track, observation, originalDeadline, ready));
    }

    internal async ValueTask<T> RunClusterBacklogAsync<T>(string operation, int? slot,
        RespireReadFrom readFrom, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation,
        Func<RespireConnection, CommandDeadline, ValueTask<T>> dispatch)
    {
        deadline = CreateCircuitDeadline(deadline);
        var acquisition = new CommandAcquisitionScope(callerToken, deadline, _core.Options.CommandTimeout);
        var cluster = _core.Cluster!;
        var discoveryObservation = observation.IsEmpty ? null : new BacklogDiscoveryObservation(observation);
        try
        {
            cluster.ValidateBacklogAdmission(slot, readFrom, callerToken);
            acquisition.CheckDeadline(operation, _core);
            return await cluster.GetReconnectBacklog(slot).EnqueueAsync(
                (connection, _) => Dispatch(connection), acquisition.Token,
                token => cluster.WaitForBacklogConnectionAsync(slot, readFrom, token,
                    discoveryObservation?.Observation ?? default)).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        { throw new OperationCanceledException(error.Message, error, callerToken); }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        { throw acquisition.CreateTimeout(operation, _core, cause: error); }
        finally
        {
            discoveryObservation?.Detach();
            acquisition.Dispose();
        }

        ValueTask<T> Dispatch(RespireConnection connection)
        {
            acquisition.CheckDeadline(operation, _core);
            acquisition.Dispose();
            return dispatch(connection, deadline);
        }
    }
}
