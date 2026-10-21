using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Standalone and Sentinel primaries share the write-evidence owner. Every attempt resolves the
    // current Sentinel generation; a READONLY reply retires its generation before the retry selects one.
    private bool CanRetryImmediate<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
        => _core.CommandRetries is not null && _core.Cluster is null && IsImmediateRetryEligible(in command);

    private bool CanRetryCluster<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
        => _core.CommandRetries is not null && _core.Cluster is not null && IsImmediateRetryEligible(in command);

    private bool IsImmediateRetryEligible<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
        => !_snapshotPrefixedBinaryKeys && command is not IStreamingRespCommand
            && !CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
            && !CursorCommandMetadata.IsServerCursorContinuation(in command)
            && command.RetryCategory != RespireCommandRetryCategory.Never;

    // This method starts synchronously: caller memory is copied before any asynchronous selection.
    // The original command retains admission callbacks and its logical fence; only serialization
    // uses the snapshot. Each transport source still retains its own fence until FIFO retirement.
    private ValueTask<RespValue> SendImmediateWithRetriesAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool track = false, bool optOut = false,
        Action? onTrackingReady = null, bool retainTracking = false)
        where TCommand : struct, IRespCommand
    {
        if (ShouldSkipTracking(operation, in command))
        {
            track = true;
            optOut = true;
        }
        var deadline = CreateCircuitDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        var backlogConnection = UsesPrimaryBacklog ? SelectPrimaryBacklogConnection(cancellationToken) : null;
        var state = ImmediateRetryState.Rent(in command);
        var retryCommand = new ImmediateRetryCommand<TCommand>(command, state, track, optOut);
        ClientSideCacheCoordinator.QueryRequest? trackingRead;
        try
        {
            // Capture alongside the wire frame, before backlog waits or user observers
            // can reuse borrowed keys. The option-off async state machine stays unchanged.
            trackingRead = retainTracking && _core.Options.EnableReplicaRedirects
                ? retryCommand.SnapshotTrackingRead(operation) : null;
        }
        catch { state.Return(); throw; }
        if (UsesPrimaryBacklog && backlogConnection is null)
            return SendBackloggedImmediateRetryAsync(operation, retryCommand,
                state, cancellationToken, deadline, observation, track, onTrackingReady, retainTracking, trackingRead);
        return SendImmediateRetryCoreAsync(operation, retryCommand,
            state, cancellationToken, deadline, observation, track, backlogConnection, onTrackingReady, retainTracking, trackingRead);
    }

    private async ValueTask<RespValue> SendBackloggedImmediateRetryAsync<TCommand>(string operation, ImmediateRetryCommand<TCommand> command,
        ImmediateRetryState state, CancellationToken token, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool track, Action? onTrackingReady, bool retainTracking,
        ClientSideCacheCoordinator.QueryRequest? trackingRead) where TCommand : struct, IRespCommand
    {
        var dispatched = false;
        try
        {
            return await RunPrimaryBacklogAsync(operation, token, deadline, (connection, originalDeadline) =>
            {
                dispatched = true;
                return SendImmediateRetryCoreAsync(operation, command, state, token, originalDeadline,
                    observation, track, connection, onTrackingReady, retainTracking, trackingRead);
            }, observation).ConfigureAwait(false);
        }
        finally { if (!dispatched) state.Return(); }
    }

    // Keep redirect ownership and its asynchronous cleanup out of the option-off state machine.
    // Pool misses allocate the entire state machine, including fields for branches never taken.
    private ValueTask<RespValue> SendImmediateRetryCoreAsync<TCommand>(string operation, ImmediateRetryCommand<TCommand> command,
        ImmediateRetryState state, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool track, RespireConnection? backlogConnection = null,
        Action? onTrackingReady = null, bool retainTracking = false,
        ClientSideCacheCoordinator.QueryRequest? trackingRead = null) where TCommand : struct, IRespCommand
        => _core.Options.EnableReplicaRedirects
            ? SendImmediateRedirectRetryCoreAsync(operation, command, state, callerToken, deadline, observation, track,
                backlogConnection, onTrackingReady, retainTracking, trackingRead)
            : SendImmediateRetryWithoutRedirectsAsync(operation, command, state, callerToken, deadline, observation, track, backlogConnection);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendImmediateRetryWithoutRedirectsAsync<TCommand>(string operation, TCommand command,
        ImmediateRetryState state, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool track, RespireConnection? backlogConnection) where TCommand : struct, IRespCommand
    {
        CancellationTokenSource? timeout = null;
        var token = callerToken;
        RespireConnection? connection = null;
        RespireTelemetry.OperationScope telemetry = default;
        try
        {
            if (RespireTelemetry.IsOperationEnabled(operation))
                telemetry = RespireTelemetry.StartOperation(operation, _core.Endpoint, _core.Options.Database);
        }
        catch (Exception) { /* A tracing listener cannot prevent application dispatch. */ }
        Exception? finalError = null;
        try
        {
            if (deadline.IsSet)
            {
                timeout = CommandTimeoutCancellation.Create(callerToken,
                    TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)));
                token = timeout.Token;
            }
            for (var attempt = 1; ; attempt++)
            {
                ThrowIfRetryStopped(operation, token, deadline);
                ObjectDisposedException.ThrowIf(_core.Disposed, this);
                var outcome = CommandWriteOutcome.DefinitelyUnwritten;
                CircuitAdmission admission = default;
                try
                {
                    // Match redirect-aware admission: resolve the current primary on each attempt,
                    // then acquire its circuit and preserve the attempt-local transport evidence.
                    if (!_core.TryGetReadyPrimaryMultiplexer(out var multiplexer))
                    {
                        await _core.EnsureConnectedAsync(token, observation).ConfigureAwait(false);
                        multiplexer = _core.Multiplexer;
                    }
                    if (attempt > 1 && _core.Circuits is not null) RejectOpenCircuit(token);
                    connection = attempt > 1
                        ? await multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false)
                        : backlogConnection ?? GetCircuitAwareConnection(multiplexer, token);
                    observation.UseEndpoint(new(connection.Host, connection.Port), token);
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    connection.ThrowIfRetired();
                    if (_core.Circuits is not null) admission = AcquireCircuit(connection, token);
                    ThrowIfRetryStopped(operation, token, deadline);
                    state.IsRetry = attempt > 1;
                    var result = await connection.SendAttemptAsync(command, token, operation, deadline,
                        repliesBeforeFinal: track ? 1 : 0, errorObservation: observation).ConfigureAwait(false);
                    if (state.IsRetry && state.AcceptedAttempt)
                        RespireTelemetry.RecordCommandRetry(state.Category, "attempted");
                    outcome = result.WriteOutcome;
                    var response = result.GetResult();
                    if (response.IsError)
                    {
                        try { throw ResponseReader.ServerError(in response, operation, new(connection.Host, connection.Port)); }
                        finally { response.Dispose(); }
                    }
                    admission.Success();
                    if (state.Retries != 0) RespireTelemetry.RecordCommandRetry(state.Category, "succeeded");
                    return response;
                }
                catch (Exception error)
                {
                    admission.Failed(error, token);
                    var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
                    if (!_core.CommandRetries!.TryGetDelay(error, outcome, state.Category, attempt, remaining, token, out var delay))
                        throw;
                    observation.Handled(error);
                    admission.Dispose();
                    admission = default;
                    if (delay != TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                    ThrowIfRetryStopped(operation, token, deadline);
                    state.AcceptedAttempt = false;
                }
                finally { admission.Dispose(); }
            }
        }
        catch (OperationCanceledException error) when (callerToken.IsCancellationRequested
            && error.CancellationToken == token)
        {
            finalError = new OperationCanceledException(error.Message, error, callerToken);
            throw finalError;
        }
        catch (OperationCanceledException error) when (timeout is not null && timeout.IsCancellationRequested
            && error.CancellationToken == token)
        {
            finalError = new RespireTimeoutException(operation, _core.Options.CommandTimeout!.Value, error);
            throw finalError;
        }
        catch (Exception error) { finalError = error; throw; }
        finally
        {
            if (finalError is not null && state.Retries != 0)
                RespireTelemetry.RecordCommandRetry(state.Category, "exhausted");
            try { telemetry.Complete(_core, operation, error: finalError, connection: connection); }
            catch (Exception) { /* A listener cannot replace the logical command outcome. */ }
            try { timeout?.Dispose(); }
            finally { state.Return(); }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendImmediateRedirectRetryCoreAsync<TCommand>(string operation, ImmediateRetryCommand<TCommand> command,
        ImmediateRetryState state, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool track, RespireConnection? backlogConnection,
        Action? onTrackingReady, bool retainTracking,
        ClientSideCacheCoordinator.QueryRequest? trackingRead) where TCommand : struct, IRespCommand
    {
        CancellationTokenSource? timeout = null;
        var token = callerToken;
        RespireConnection? connection = null;
        var telemetryEndpoint = _core.Endpoint;
        RespireTelemetry.OperationScope telemetry = default;
        try
        {
            if (RespireTelemetry.IsOperationEnabled(operation))
                telemetry = RespireTelemetry.StartOperation(operation, _core.Endpoint, _core.Options.Database);
        }
        catch (Exception) { /* A tracing listener cannot prevent application dispatch. */ }
        Exception? finalError = null;
        try
        {
            if (deadline.IsSet)
            {
                timeout = CommandTimeoutCancellation.Create(callerToken,
                    TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)));
                token = timeout.Token;
            }
            for (var attempt = 1; ; attempt++)
            {
                ThrowIfRetryStopped(operation, token, deadline);
                ObjectDisposedException.ThrowIf(_core.Disposed, this);
                var outcome = CommandWriteOutcome.DefinitelyUnwritten;
                CircuitAdmission admission = default;
                DedicatedConnectionPool? redirectPool = null;
                RespireConnection? redirectConnection = null;
                ReplicaRedirectTargets.Lease? trackedTarget = null;
                var continuityPending = false;
                try
                {
                    // Use the same validated ready route as default dispatch. Healthy Sentinel
                    // sends must not create discovery state; retirement is rechecked at admission.
                    if (!_core.TryGetReadyPrimaryMultiplexer(out var multiplexer))
                    {
                        await _core.EnsureConnectedAsync(token, observation).ConfigureAwait(false);
                        multiplexer = _core.Multiplexer;
                    }
                    // Initialization alone does not wait for an established socket's replacement.
                    // Do not spend the command attempt budget probing the same dead connection.
                    // An open circuit still rejects before the wait, and again at admission below.
                    if (attempt > 1 && _core.Circuits is not null) RejectOpenCircuit(token);
                    connection = attempt > 1
                        ? await multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false)
                        : backlogConnection ?? GetCircuitAwareConnection(multiplexer, token);
                    observation.UseEndpoint(new(connection.Host, connection.Port), token);
                    telemetryEndpoint = new(connection.Host, connection.Port);
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    connection.ThrowIfRetired();
                    if (_core.Circuits is not null) admission = AcquireCircuit(connection, token);
                    ThrowIfRetryStopped(operation, token, deadline);
                    state.IsRetry = attempt > 1;
                    for (var redirects = 0; ; redirects++)
                    {
                        // The connection enforces the send deadline and can relax it for
                        // maintenance. The acquisition timer must not cancel that allowance.
                        var useTracking = redirects == 0 || trackedTarget is not null;
                        if (retainTracking) trackedTarget?.RegisterRead(trackingRead);
                        var result = await connection.SendAttemptAsync(useTracking ? command : command.WithoutTracking(),
                            connection.ReplicaRedirectsEnabled ? callerToken : token, operation, deadline,
                            repliesBeforeFinal: useTracking && track ? 1 : 0,
                            errorObservation: observation).ConfigureAwait(false);
                        if (redirects == 0 && state.IsRetry && state.AcceptedAttempt)
                            RespireTelemetry.RecordCommandRetry(state.Category, "attempted");
                        outcome = result.WriteOutcome;
                        RespireServerException rejection;
                        try
                        {
                            var response = result.GetResult();
                            if (!response.IsError)
                            {
                                admission.Success();
                                if (state.Retries != 0) RespireTelemetry.RecordCommandRetry(state.Category, "succeeded");
                                return response;
                            }
                            try { rejection = ResponseReader.ServerError(in response, operation, new(connection.Host, connection.Port)); }
                            finally { response.Dispose(); }
                        }
                        catch (RespireConnectionRetiredException retired) when (trackedTarget is not null
                            && TryRerouteCircuit(connection, deadline, out var replacement, out var rerouted,
                                GetTransportReadZone(in command)))
                        {
                            // Planned target recycling rejected admission, so it spends neither
                            // a server redirect nor a command retry and retains tracking ownership.
                            observation.Handled(retired, new(connection.Host, connection.Port));
                            admission.Dispose();
                            admission = default;
                            connection = replacement;
                            deadline = rerouted;
                            timeout?.Dispose();
                            timeout = deadline.IsSet
                                ? CommandTimeoutCancellation.Create(callerToken,
                                    TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
                                : null;
                            token = timeout?.Token ?? callerToken;
                            telemetryEndpoint = new(connection.Host, connection.Port);
                            observation.UseEndpoint(telemetryEndpoint, token);
                            telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                            trackedTarget.UseConnection(connection);
                            onTrackingReady?.Invoke();
                            if (_core.Circuits is not null) admission = AcquireCircuit(connection, token);
                            redirects--;
                            continue;
                        }
                        catch (RespireServerException error) { rejection = error; }

                        if ((redirects == 0 && !connection.ReplicaRedirectsEnabled) || CursorCommandMetadata.HasServerCursor(in command)
                            || rejection.Code != "REDIRECT" || redirects >= ClusterRouter.RedirectLimit
                            || !TryParseReplicaRedirect(rejection.Message, connection.Host, out var endpoint)) throw rejection;

                        continuityPending = retainTracking;
                        // Carry the source's maintenance allowance across acquisition and
                        // later sends. IsRelaxed prevents a second hop extending it again.
                        deadline = connection.GetReroutedCommandDeadline(deadline);
                        timeout?.Dispose();
                        timeout = deadline.IsSet
                            ? CommandTimeoutCancellation.Create(callerToken,
                                TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
                            : null;
                        token = timeout?.Token ?? callerToken;
                        // REDIRECT proves this hop did not execute the command. The next hop owns
                        // fresh write evidence, including an unwritten connection/handshake failure.
                        outcome = CommandWriteOutcome.DefinitelyUnwritten;
                        admission.Success();
                        admission.Dispose();
                        admission = default;
                        if (!retainTracking) _core.ClientCache?.FlushForContinuityLoss();
                        command.ValidateAdmission();
                        ThrowIfRetryStopped(operation, token, deadline);
                        if (observation.IsEmpty)
                            RespireTelemetry.RecordError(rejection, internallyHandled: true, redirects, new(connection.Host, connection.Port));
                        else observation.Handled(rejection, new(connection.Host, connection.Port));
                        if (redirectPool is not null)
                        {
                            var completedPool = redirectPool;
                            redirectPool = null;
                            completedPool.Return(redirectConnection!);
                            redirectConnection = null;
                            await _core.ReleaseServerPoolAsync(completedPool).ConfigureAwait(false);
                        }
                        trackedTarget?.Dispose();
                        trackedTarget = null;
                        telemetryEndpoint = endpoint;
                        connection = null;
                        observation.UseEndpoint(endpoint, token);
                        telemetry.UpdateServerEndpoint(endpoint.Host, endpoint.Port);
                        if (retainTracking && _core.RedirectTargets is { } targets)
                        {
                            // The acquisition owner now guarantees the failure fence.
                            continuityPending = false;
                            trackedTarget = await AcquireTrackedReplicaTargetAsync(targets, endpoint, token).ConfigureAwait(false);
                        }
                        if (trackedTarget is not null)
                        {
                            connection = trackedTarget.Connection;
                            onTrackingReady?.Invoke();
                        }
                        else
                        {
                            if (retainTracking)
                            {
                                continuityPending = false;
                                _core.ClientCache?.FlushForContinuityLoss();
                            }
                            redirectPool = _core.CreateServerPool(endpoint, replicaRedirect: true);
                            redirectConnection = await RentReplicaRedirectTargetAsync(redirectPool, token).ConfigureAwait(false);
                            connection = redirectConnection;
                        }
                        observation.UseEndpoint(new(connection.Host, connection.Port), token);
                        telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                        if (trackedTarget is null) connection.ThrowIfRetired();
                        if (_core.Circuits is not null) admission = AcquireCircuit(connection, token);
                        ThrowIfRetryStopped(operation, token, deadline);
                    }
                }
                catch (Exception error)
                {
                    // Fence early handoff failures before retry classification or backoff.
                    if (continuityPending)
                    {
                        continuityPending = false;
                        _core.ClientCache?.FlushForContinuityLoss();
                    }
                    admission.Failed(error, token);
                    var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
                    if (!_core.CommandRetries!.TryGetDelay(error, outcome, state.Category, attempt, remaining, token, out var delay))
                        throw;
                    observation.Handled(error);
                    // Release the half-open probe before backoff or replacement selection.
                    admission.Dispose();
                    admission = default;
                    if (delay != TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
                    ThrowIfRetryStopped(operation, token, deadline);
                    state.AcceptedAttempt = false;
                }
                finally
                {
                    admission.Dispose();
                    trackedTarget?.Dispose();
                    if (redirectPool is not null)
                    {
                        if (redirectConnection is not null) redirectPool.Return(redirectConnection);
                        await _core.ReleaseServerPoolAsync(redirectPool).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException error) when (callerToken.IsCancellationRequested
            && error.CancellationToken == token)
        {
            finalError = new OperationCanceledException(error.Message, error, callerToken);
            throw finalError;
        }
        catch (OperationCanceledException error) when (timeout is not null && timeout.IsCancellationRequested
            && error.CancellationToken == token)
        {
            finalError = new RespireTimeoutException(operation, _core.Options.CommandTimeout!.Value, error);
            throw finalError;
        }
        catch (Exception error) { finalError = error; throw; }
        finally
        {
            if (finalError is not null && state.Retries != 0)
                RespireTelemetry.RecordCommandRetry(state.Category, "exhausted");
            try { telemetry.Complete(operation, telemetryEndpoint.Host, telemetryEndpoint.Port,
                _core.Options.Database, error: finalError, connection: connection); }
            catch (Exception) { /* A listener cannot replace the logical command outcome. */ }
            try { timeout?.Dispose(); }
            finally { state.Return(); }
        }
    }

    // Cluster dispatch keeps MOVED, ASK, retirement and READONLY recovery inside each attempt. The
    // retry owner only sees the attempt's final failure, without write evidence, and passes its one
    // deadline into every attempt. Arguments are copied before any asynchronous selection.
    private ValueTask<RespValue> SendClusterWithRetriesAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, bool noRedirect, bool observeErrors,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ImmediateRetryState.Rent(in command);
        return SendClusterRetryCoreAsync(operation, cluster, new ImmediateRetryCommand<TCommand>(command, state, track: false),
            state, cancellationToken, noRedirect, observeErrors, observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendClusterRetryCoreAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, ImmediateRetryState state, CancellationToken cancellationToken, bool noRedirect,
        bool observeErrors, RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        // Attempts borrow one logical owner, so handled attempt failures never publish as final errors.
        var ownsObservation = observeErrors && observation.IsEmpty;
        try
        {
            if (ownsObservation) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
            return await LogicalCommandRetry.RunAsync(_core.CommandRetries!, operation, state.Category, _core.Options.CommandTimeout,
                (Client: this, Operation: operation, Cluster: cluster, Command: command, NoRedirect: noRedirect, Observation: observation),
                static (state, attempt) => state.Client.SendClusterAttemptAsync(state.Operation, state.Cluster, state.Command,
                    state.NoRedirect, attempt, state.Observation),
                observation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (observeErrors) observation.Final(error);
            throw;
        }
        finally
        {
            try { if (ownsObservation) observation.Dispose(); }
            finally { state.Return(); }
        }
    }

    // Every attempt takes this path. Initialization alone does not wait for an established socket's
    // replacement, so later attempts first give the routed owner a bounded chance to reconnect.
    private ValueTask<RespValue> SendClusterAttemptAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, bool noRedirect, LogicalCommandRetry.Attempt attempt, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var wait = attempt.Number > 1
            ? cluster.WaitForRouteReplacementAsync(command.TryGetClusterSlot(out var slot) ? slot : null, attempt.Token)
            : default;
        return wait.IsCompletedSuccessfully
            ? SendClusterAsync(operation, cluster, command, attempt.Token,
                noRedirect: noRedirect, retryDeadline: attempt.Deadline, observation: observation)
            : SendClusterAfterRouteReplacementAsync(wait, operation, cluster, command, noRedirect, attempt, observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendClusterAfterRouteReplacementAsync<TCommand>(ValueTask wait, string operation,
        ClusterRouter cluster, TCommand command, bool noRedirect, LogicalCommandRetry.Attempt attempt,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        await wait.ConfigureAwait(false);
        return await SendClusterAsync(operation, cluster, command, attempt.Token,
            noRedirect: noRedirect, retryDeadline: attempt.Deadline, observation: observation).ConfigureAwait(false);
    }

    // The cache producer owns retries; each attempt retains the tracking prelude and the
    // routing owner's redirect/rebase callbacks. The snapshot contains only the read command.
    private ValueTask<RespValue> SendTrackedClusterWithRetriesAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, CancellationToken cancellationToken, Action? onRedirect, bool track,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ImmediateRetryState.Rent(in command);
        return SendTrackedClusterRetryCoreAsync(operation, cluster,
            new ImmediateRetryCommand<TCommand>(command, state, track: false), state,
            cancellationToken, onRedirect, track, observation);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendTrackedClusterRetryCoreAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, ImmediateRetryState state, CancellationToken cancellationToken, Action? onRedirect, bool track,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        try
        {
            return await LogicalCommandRetry.RunAsync(_core.CommandRetries!, operation, state.Category, _core.Options.CommandTimeout,
                (Client: this, Operation: operation, Cluster: cluster, Command: command,
                    OnRedirect: onRedirect, Track: track, Observation: observation),
                static (retry, attempt) => retry.Client.SendTrackedClusterRetryAttemptAsync(retry.Operation, retry.Cluster,
                    retry.Command, retry.OnRedirect, retry.Track, attempt, retry.Observation), observation, cancellationToken)
                .ConfigureAwait(false);
        }
        finally { state.Return(); }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendTrackedClusterRetryAttemptAsync<TCommand>(string operation, ClusterRouter cluster,
        TCommand command, Action? onRedirect, bool track, LogicalCommandRetry.Attempt attempt,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        if (attempt.Number > 1)
        {
            var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
            await cluster.WaitForRouteReplacementAsync(slot, attempt.Token).ConfigureAwait(false);
            onRedirect?.Invoke();
        }
        return await SendTrackedClusterAsync(operation, cluster, command, attempt.Token, onRedirect, track,
            observation, attempt.Deadline).ConfigureAwait(false);
    }

    // Scripts and functions retry only with an explicit category. Their operation can issue several
    // independently admitted commands (EVALSHA then EVAL, or FCALL around a library reload), so a
    // circuit rejection inside it does not prove the resend unexecuted; only one from the resend's
    // preparation does. Cluster has no preparation: its first command's routing admits inside the
    // operation, so a Cluster resend rejected before any send still counts as attempted.
    internal ValueTask<TResult> RunWithDeclaredRetriesAsync<TState, TResult>(string operation,
        RespireCommandRetryCategory category, TState state,
        Func<TState, LogicalCommandRetry.Attempt, ValueTask<TResult>> send,
        RespireTelemetry.ErrorObservation observation, CancellationToken cancellationToken)
        => LogicalCommandRetry.RunAsync(_core.CommandRetries!, operation, category, _core.Options.CommandTimeout,
            (Client: this, State: state, Send: send, Observation: observation),
            static (retry, attempt) => retry.Send(retry.State, attempt),
            observation, cancellationToken, rejectionProvesUnexecuted: false,
            prepareResend: static (retry, attempt) => retry.Client.PrepareDeclaredResendAsync(attempt, retry.Observation));

    // Initialization alone does not wait for an established socket's replacement. As in the
    // standalone owner, an open circuit rejects before the wait and again at admission.
    private ValueTask PrepareDeclaredResendAsync(LogicalCommandRetry.Attempt attempt,
        RespireTelemetry.ErrorObservation observation)
        => _core.Cluster is null ? PrepareDeclaredStandaloneResendAsync(attempt.Token, observation) : default;

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask PrepareDeclaredStandaloneResendAsync(CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
    {
        await _core.EnsureConnectedAsync(token, observation).ConfigureAwait(false);
        if (_core.Circuits is not null) RejectOpenCircuit(token);
        _ = await _core.Multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false);
    }

    // A complete unit resends on the current primary, selected as for the immediate owner. An
    // open circuit rejects before the replacement wait; the unit's admission rechecks it.
    internal async ValueTask<RespireConnection> GetUnitRetryConnectionAsync(CancellationToken token,
        RespireTelemetry.ErrorObservation observation)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        if (!_core.TryGetReadyPrimaryMultiplexer(out var multiplexer))
        {
            await _core.EnsureConnectedAsync(token, observation).ConfigureAwait(false);
            multiplexer = _core.Multiplexer;
        }
        if (_core.Circuits is not null) RejectOpenCircuit(token);
        return await multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false);
    }

    internal bool CanRetryDeclared(RespireCommandRetryCategory category)
        => _core.CommandRetries is not null && category != RespireCommandRetryCategory.Never;

    // Waiting for a replacement records no availability samples. The ignored permit
    // releases half-open probe capacity without completing the probe.
    private void RejectOpenCircuit(CancellationToken token)
        => _core.Circuits!.Acquire(_core.Multiplexer.ActiveConnectionEndpoint, token).Dispose();

    private void ThrowIfRetryStopped(string operation, CancellationToken token, CommandDeadline deadline)
    {
        token.ThrowIfCancellationRequested();
        if (deadline.IsSet && deadline.RemainingMilliseconds == 0)
            throw new RespireTimeoutException(operation, _core.Options.CommandTimeout!.Value);
    }

    private readonly struct ImmediateRetryCommand<TCommand>(TCommand command, ImmediateRetryState state,
        bool track, bool optOut = false) : IRespCommandWrapper where TCommand : struct, IRespCommand
    {
        public int GetWriteSizeHint() => checked(state.Frame.Length + (track ? new ClientCachingCommand(optOut).GetWriteSizeHint() : 0));
        public void Write(ref RespWriter writer)
        {
            if (track) new ClientCachingCommand(optOut).Write(ref writer);
            writer.WriteRaw(state.Frame.Span);
        }
        public ReadCommandKind ReadKind => command.ReadKind;
        public int CursorArgumentIndex => command.CursorArgumentIndex;
        public bool HasOpaqueCursor => command.HasOpaqueCursor;
        public RespireCommandRetryCategory RetryCategory => command.RetryCategory;
        public bool TryGetClusterSlot(out int slot) => state.TryGetClusterSlot(out slot);
        public bool IsConnectionProtocol => false;
        public void ValidateAdmission() => command.ValidateAdmission();
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
        public void OnAccepted() { if (!state.AcceptedAttempt) state.Accepted(); command.OnAccepted(); }
        internal ImmediateRetryCommand<TCommand> WithoutTracking() => new(command, state, track: false);
        internal ClientSideCacheCoordinator.QueryRequest? SnapshotTrackingRead(string operation)
            => ClientSideCacheCoordinator.TryCreateQuery(operation, in command, out var request) && request.Query.Operation == operation
                ? new(request.Query.Snapshot(), request.PrimaryKey.Snapshot()) : null;
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    }
}
