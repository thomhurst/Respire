using Respire.Infrastructure;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private ValueTask<Stream?> SendPrimaryBulkStreamAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        var connection = UsesStandaloneBacklog ? SelectStandaloneConnection(cancellationToken)
            : GetCircuitAwareConnection(_core.Multiplexer, cancellationToken);
        if (connection is not null)
            return SendBulkStreamOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation);
        var deadline = CreateCircuitDeadline();
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunStandaloneBacklogAsync(operation, cancellationToken, deadline, (ready, originalDeadline) =>
            SendBulkStreamOnConnectionAsync(operation, ready, snapshot, cancellationToken,
                observation: observation, commandDeadline: originalDeadline));
    }

    private ValueTask SendPrimaryFireAndForgetAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, string? storedProcedureName, RespireTelemetry.ErrorObservation observation,
        bool awaitReply = false) where TCommand : struct, IRespCommand
    {
        var connection = UsesStandaloneBacklog && !CursorCommandMetadata.IsServerCursorContinuation(in command)
            ? SelectStandaloneConnection(cancellationToken)
            : GetCircuitAwareConnection(_core.Multiplexer, cancellationToken);
        if (connection is null)
            return SendBackloggedFireAndForgetAsync(operation, command, cancellationToken, storedProcedureName, observation, awaitReply);
        return awaitReply
            ? SendDiscardedReplyAsync(operation, connection, command, cancellationToken, storedProcedureName, observation)
            : SendFireAndForgetOnConnectionAsync(operation, connection, command, cancellationToken, storedProcedureName, observation);
    }

    private async ValueTask SendBackloggedFireAndForgetAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, string? storedProcedureName, RespireTelemetry.ErrorObservation observation,
        bool awaitReply) where TCommand : struct, IRespCommand
    {
        var deadline = CreateCircuitDeadline();
        var snapshot = new BacklogCommand<TCommand>(command);
        await RunStandaloneBacklogAsync(operation, cancellationToken, deadline, async (ready, originalDeadline) =>
        {
            if (!awaitReply)
                await SendFireAndForgetOnConnectionAsync(operation, ready, snapshot, cancellationToken,
                    storedProcedureName, observation, originalDeadline).ConfigureAwait(false);
            else
                await SendDiscardedReplyAsync(operation, ready, snapshot, cancellationToken, storedProcedureName,
                    observation, originalDeadline).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }

    private async ValueTask SendDiscardedReplyAsync<TCommand>(string operation, RespireConnection connection, TCommand command,
        CancellationToken cancellationToken, string? storedProcedureName, RespireTelemetry.ErrorObservation observation,
        CommandDeadline deadline = default) where TCommand : struct, IRespCommand
    {
        try
        {
            using var response = await SendOnConnectionAsync(operation, connection, command, cancellationToken,
                storedProcedureName, commandDeadline: deadline, observation: observation).ConfigureAwait(false);
        }
        catch (Exception error) when (RespireException.GetDefinitiveServerError(error) is not null)
        {
            RespireTelemetry.RecordError(error, internallyHandled: true, observation.Attempts, observation.Endpoint);
        }
    }

    internal bool UsesStandaloneBacklog => _core.Options.BacklogPolicy == RespireBacklogPolicy.Queue
        && _core.Cluster is null && _core.Sentinel is null && _core.Multiplexer.IsInitialized;

    // Only connection selection failures enter the backlog. Errors from dispatch (including
    // accepted commands) belong to the existing command/retry owner and never come back here.
    internal RespireConnection? SelectStandaloneConnection(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_core.Multiplexer.IsBacklogDraining)
        {
            if (_core.Circuits is not null) RejectOpenCircuit(cancellationToken);
            return null;
        }
        try
        {
            // Unit callers use a deferred client view, but selection still owns availability
            // evidence. Their individual sends acquire their circuit permits after admission.
            var connection = _core.Circuits is null ? _core.Multiplexer.GetConnection()
                : GetCircuitConnectionSlow(_core.Multiplexer, cancellationToken);
            return _core.Multiplexer.IsBacklogDraining ? null : connection;
        }
        catch (RespireConnectionException error) when (UsesStandaloneBacklog
            && error is not RespireReconnectLimitException)
        {
            if (_core.Circuits is not null)
            {
                var opened = false;
                try { RejectOpenCircuit(cancellationToken); }
                catch (RespireCircuitOpenException) { opened = true; }
                // Selection recorded the availability failure. Preserve that first outcome
                // if it opened the circuit; later callers see the circuit rejection.
                if (opened) throw;
            }
            return null;
        }
    }

    internal async ValueTask<T> RunStandaloneBacklogAsync<T>(string operation, CancellationToken callerToken,
        CommandDeadline deadline, Func<RespireConnection, CommandDeadline, ValueTask<T>> dispatch)
    {
        deadline = CreateCircuitDeadline(deadline);
        var acquisition = new CommandAcquisitionScope(callerToken, deadline, _core.Options.CommandTimeout);
        try
        {
            if (_core.Circuits is not null) RejectOpenCircuit(callerToken);
            acquisition.CheckDeadline(operation, _core);
            return await _core.Multiplexer.GetReconnectBacklog(_core.Options.BacklogCapacity).EnqueueAsync(
                (connection, _) => Dispatch(connection), acquisition.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        { throw new OperationCanceledException(error.Message, error, callerToken); }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        { throw acquisition.CreateTimeout(operation, _core, cause: error); }
        finally { acquisition.Dispose(); }

        ValueTask<T> Dispatch(RespireConnection connection)
        {
            acquisition.CheckDeadline(operation, _core);
            // Transport admission and response sweeping now own this same absolute deadline.
            // Keep the caller's original token for transport cancellation and diagnostics.
            acquisition.Dispose();
            return dispatch(connection, deadline);
        }
    }

    // Snapshot the wire bytes before suspending, but retain admission, acceptance and fence
    // ownership on the original command (plain SnapshotCommand deliberately drops callbacks).
    private readonly struct BacklogCommand<TCommand>(TCommand command) : IRespCommandWrapper
        where TCommand : struct, IRespCommand
    {
        private readonly SnapshotCommand _snapshot = SnapshotCommand.Create(in command, captureCacheMetadata: false);
        public void Write(ref RespWriter writer) => _snapshot.Write(ref writer);
        public int GetWriteSizeHint() => _snapshot.GetWriteSizeHint();
        public bool TryGetClusterSlot(out int slot) => _snapshot.TryGetClusterSlot(out slot);
        public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);
        public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
        public bool TryGetClientCacheKey(string operation, out ClientCacheCommandKey key)
            => command.TryGetClientCacheKey(operation, out key);
        public ReadCommandKind ReadKind => command.ReadKind;
        public RespireCommandRetryCategory RetryCategory => command.RetryCategory;
        public int CursorArgumentIndex => command.CursorArgumentIndex;
        public bool IsConnectionProtocol => CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command);
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
        public void ValidateAdmission() => command.ValidateAdmission();
        public CancellationToken GetResponseCancellationToken(CancellationToken token) => command.GetResponseCancellationToken(token);
        public void OnAccepted() => command.OnAccepted();
    }
}
