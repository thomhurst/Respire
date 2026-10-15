using Respire.Internal;
using Respire.Networking;

namespace Respire;

public sealed partial class RespireClient
{
    // Initial connection/ROLE acquisition keeps its existing connection budget. Only a
    // previously usable route starts the command deadline before recovery selection.
    private CommandDeadline CreateReplicaDispatchDeadline(CommandDeadline deadline = default)
        => _core.ReadRouter.CanQueue(_readFrom) ? CreateCircuitDeadline(deadline) : deadline;

    private async ValueTask<T> RunReplicaBacklogAsync<T>(string operation, CancellationToken callerToken,
        CommandDeadline deadline, Func<RespireConnection, CommandDeadline, ValueTask<T>> dispatch,
        RespireTelemetry.ErrorObservation observation, bool cursorRead = false, ReadAffinity? affinity = null)
    {
        // Eligibility can become true during an initially cold selection, for example
        // when another caller establishes the route and starts a drain. Bound admission
        // from here when that caller did not already carry a recovery deadline.
        deadline = CreateCircuitDeadline(deadline);
        var acquisition = new CommandAcquisitionScope(callerToken, deadline, _core.Options.CommandTimeout);
        try
        {
            acquisition.CheckDeadline(operation, _core);
            return await _core.ReadRouter.EnqueueAsync(_readFrom, (connection, _) =>
            {
                acquisition.CheckDeadline(operation, _core);
                acquisition.Dispose();
                return dispatch(connection, deadline);
            }, acquisition.Token, observation, cursorRead, affinity).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        { throw new OperationCanceledException(error.Message, error, callerToken); }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        { throw acquisition.CreateTimeout(operation, _core, cause: error); }
        finally { acquisition.Dispose(); }
    }

    private async ValueTask<RespireConnection?> SelectReplicaForDispatchAsync(string operation,
        CancellationToken callerToken, CommandDeadline deadline, RespireTelemetry.ErrorObservation observation,
        bool cursorRead = false, ReadAffinity? affinity = null)
    {
        if (_core.Options.BacklogPolicy == RespireBacklogPolicy.FailFast)
            return await _core.ReadRouter.SelectForDispatchAsync(_readFrom, callerToken, observation, cursorRead, affinity).ConfigureAwait(false);
        var acquisition = new CommandAcquisitionScope(callerToken, deadline, _core.Options.CommandTimeout);
        try
        {
            acquisition.CheckDeadline(operation, _core);
            // A fresh single-replica publication needs no deadline timer or linked token.
            // The original deadline still follows it into transport admission.
            if (!cursorRead && !_core.ReadRouter.IsBacklogDraining
                && _core.ReadRouter.TryAcquireReadyConnection(_readFrom, callerToken) is { } ready
                && !_core.ReadRouter.IsBacklogDraining) return ready;
            return await _core.ReadRouter.SelectForDispatchAsync(_readFrom, acquisition.Token, observation, cursorRead, affinity).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        { throw new OperationCanceledException(error.Message, error, callerToken); }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        { throw acquisition.CreateTimeout(operation, _core, cause: error); }
        finally { acquisition.Dispose(); }
    }
}
