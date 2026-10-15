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
        var connection = UsesPrimaryBacklog ? SelectPrimaryBacklogConnection(cancellationToken)
            : GetCircuitAwareConnection(_core.Multiplexer, cancellationToken);
        if (connection is not null)
            return SendBulkStreamOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation);
        var deadline = CreateCircuitDeadline();
        var snapshot = new BacklogCommand<TCommand>(command);
        return RunPrimaryBacklogAsync(operation, cancellationToken, deadline, (ready, originalDeadline) =>
            SendBulkStreamOnConnectionAsync(operation, ready, snapshot, cancellationToken,
                observation: observation, commandDeadline: originalDeadline), observation);
    }

    private ValueTask SendPrimaryFireAndForgetAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, string? storedProcedureName, RespireTelemetry.ErrorObservation observation,
        bool awaitReply = false) where TCommand : struct, IRespCommand
    {
        var connection = UsesPrimaryBacklog && !CursorCommandMetadata.IsServerCursorContinuation(in command)
            ? SelectPrimaryBacklogConnection(cancellationToken)
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
        await RunPrimaryBacklogAsync(operation, cancellationToken, deadline, async (ready, originalDeadline) =>
        {
            if (!awaitReply)
                await SendFireAndForgetOnConnectionAsync(operation, ready, snapshot, cancellationToken,
                    storedProcedureName, observation, originalDeadline).ConfigureAwait(false);
            else
                await SendDiscardedReplyAsync(operation, ready, snapshot, cancellationToken, storedProcedureName,
                    observation, originalDeadline).ConfigureAwait(false);
            return true;
        }, observation).ConfigureAwait(false);
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

    internal bool UsesPrimaryBacklog => _core.Options.BacklogPolicy == RespireBacklogPolicy.Queue
        && _core.Cluster is null && (_core.Sentinel is { } sentinel
            ? sentinel.Current is not null : _core.Multiplexer.IsInitialized);

    // Initial discovery retains Connect/Create semantics. Once a Sentinel generation has
    // been published, the queue owns rediscovery, so callers cannot accumulate unbounded
    // waiters at the discovery gate before backlog admission.
    private ValueTask EnsurePrimaryDispatchConnectedAsync(CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation, bool allowBacklog = true)
        => allowBacklog && UsesPrimaryBacklog && _core.Sentinel is not null
            ? default : _core.EnsureConnectedAsync(cancellationToken, observation);

    private void RejectOpenBacklogCircuit(CancellationToken cancellationToken)
    {
        // A retired Sentinel endpoint cannot veto discovery of the new elected primary.
        // The actual dispatch still acquires that selected endpoint's circuit permit.
        if (_core.Circuits is not null && (_core.Sentinel is null
            || _core.TryGetReadyPrimaryMultiplexer(out _))) RejectOpenCircuit(cancellationToken);
    }

    // Only connection selection failures enter the backlog. Errors from dispatch (including
    // accepted commands) belong to the existing command/retry owner and never come back here.
    internal RespireConnection? SelectPrimaryBacklogConnection(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        var multiplexer = _core.Multiplexer;
        if (_core.Sentinel is { } sentinel
            && (sentinel.IsBacklogDraining || !_core.TryGetReadyPrimaryMultiplexer(out multiplexer)))
        {
            RejectOpenBacklogCircuit(cancellationToken);
            return null;
        }
        if (multiplexer.IsBacklogDraining)
        {
            RejectOpenBacklogCircuit(cancellationToken);
            return null;
        }
        try
        {
            // Unit callers use a deferred client view, but selection still owns availability
            // evidence. Their individual sends acquire their circuit permits after admission.
            var connection = _core.Circuits is null ? multiplexer.GetConnection()
                : GetCircuitConnectionSlow(multiplexer, cancellationToken);
            return multiplexer.IsBacklogDraining || _core.Sentinel?.IsBacklogDraining == true ? null : connection;
        }
        catch (ObjectDisposedException) when (_core.Sentinel is not null && multiplexer.IsRetired && !_core.Disposed)
        {
            return null;
        }
        catch (RespireConnectionRetiredException) when (_core.Sentinel is not null && UsesPrimaryBacklog)
        {
            return null;
        }
        catch (RespireConnectionException error) when (UsesPrimaryBacklog
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

    internal async ValueTask<T> RunPrimaryBacklogAsync<T>(string operation, CancellationToken callerToken,
        CommandDeadline deadline, Func<RespireConnection, CommandDeadline, ValueTask<T>> dispatch,
        RespireTelemetry.ErrorObservation observation = default)
    {
        deadline = CreateCircuitDeadline(deadline);
        var acquisition = new CommandAcquisitionScope(callerToken, deadline, _core.Options.CommandTimeout);
        var dispatched = false;
        BacklogDiscoveryObservation? discoveryObservation = null;
        try
        {
            RejectOpenBacklogCircuit(callerToken);
            acquisition.CheckDeadline(operation, _core);
            var backlog = _core.Sentinel is { } sentinel
                ? sentinel.GetReconnectBacklog(_core.Options.BacklogCapacity)
                : _core.Multiplexer.GetReconnectBacklog(_core.Options.BacklogCapacity);
            if (_core.Sentinel is { } router)
            {
                discoveryObservation = observation.IsEmpty ? null : new(observation);
                return await backlog.EnqueueAsync((connection, _) => Dispatch(connection), acquisition.Token,
                    token => router.WaitForBacklogConnectionAsync(token, discoveryObservation?.Observation ?? default)).ConfigureAwait(false);
            }
            return await backlog.EnqueueAsync((connection, _) => Dispatch(connection), acquisition.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (acquisition.IsCallerCancellation(error))
        { throw new OperationCanceledException(error.Message, error, callerToken); }
        catch (OperationCanceledException error) when (acquisition.IsDeadlineCancellation(error))
        { throw acquisition.CreateTimeout(operation, _core, cause: error); }
        catch (RespireTimeoutException error) when (!dispatched && _core.Sentinel is not null)
        {
            throw RespireTimeoutException.BeforeConnectionSelection(operation,
                _core.Options.CommandTimeout ?? error.Timeout, error);
        }
        finally
        {
            discoveryObservation?.Detach();
            acquisition.Dispose();
        }

        ValueTask<T> Dispatch(RespireConnection connection)
        {
            acquisition.CheckDeadline(operation, _core);
            dispatched = true;
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
        private readonly RespireValue[] _arguments = SnapshotArguments(in command);
        public void Write(ref RespWriter writer) => _snapshot.Write(ref writer);
        public int GetWriteSizeHint() => _snapshot.GetWriteSizeHint();
        public bool TryGetClusterSlot(out int slot) => _snapshot.TryGetClusterSlot(out slot);
        public bool TryGetArgument(int index, out RespireValue value)
        {
            if ((uint)index < (uint)_arguments.Length)
            {
                value = _arguments[index];
                return true;
            }
            value = default;
            return false;
        }

        private static RespireValue[] SnapshotArguments(in TCommand command)
        {
            // Reply classification (including scripting engine detection) must observe the
            // same arguments as the wire frame, even if the caller reuses borrowed storage.
            List<RespireValue>? arguments = null;
            for (var index = 0; command.TryGetArgument(index, out var value); index++)
                (arguments ??= []).Add(value.Snapshot());
            return arguments?.ToArray() ?? [];
        }

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
