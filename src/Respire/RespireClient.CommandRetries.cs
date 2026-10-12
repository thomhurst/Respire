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
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool track = false)
        where TCommand : struct, IRespCommand
    {
        var deadline = CreateCircuitDeadline();
        cancellationToken.ThrowIfCancellationRequested();
        var state = ImmediateRetryState.Rent(in command);
        return SendImmediateRetryCoreAsync(operation, new ImmediateRetryCommand<TCommand>(command, state, track),
            state, cancellationToken, deadline, observation, track);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendImmediateRetryCoreAsync<TCommand>(string operation, TCommand command,
        ImmediateRetryState state, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool track) where TCommand : struct, IRespCommand
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
                        : GetCircuitAwareConnection(multiplexer, token);
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
                        var error = ResponseReader.ServerError(in response, operation);
                        response.Dispose();
                        throw error;
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
                    // Release the half-open probe before backoff or replacement selection.
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
        var frame = SnapshotCommand.Create(in command, captureCacheMetadata: false);
        var state = new ImmediateRetryState(command.RetryCategory);
        return LogicalCommandRetry.RunAsync(_core.CommandRetries!, operation, state.Category, _core.Options.CommandTimeout,
            (Client: this, Operation: operation, Cluster: cluster,
                Command: new ImmediateRetryCommand<TCommand>(command, frame, state, track: false),
                OnRedirect: onRedirect, Track: track, Observation: observation),
            static (retry, attempt) => retry.Client.SendTrackedClusterRetryAttemptAsync(retry.Operation, retry.Cluster,
                retry.Command, retry.OnRedirect, retry.Track, attempt, retry.Observation), observation, cancellationToken);
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

    // Scripts and functions retry only with an explicit category.
    internal ValueTask<TResult> RunWithDeclaredRetriesAsync<TState, TResult>(string operation,
        RespireCommandRetryCategory category, TState state,
        Func<TState, LogicalCommandRetry.Attempt, ValueTask<TResult>> send,
        RespireTelemetry.ErrorObservation observation, CancellationToken cancellationToken)
        => LogicalCommandRetry.RunAsync(_core.CommandRetries!, operation, category, _core.Options.CommandTimeout,
            (Client: this, State: state, Send: send, Observation: observation),
            static (retry, attempt) => retry.Client.SendDeclaredAttemptAsync(retry.State, retry.Send, attempt, retry.Observation),
            observation, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<TResult> SendDeclaredAttemptAsync<TState, TResult>(TState state,
        Func<TState, LogicalCommandRetry.Attempt, ValueTask<TResult>> send, LogicalCommandRetry.Attempt attempt,
        RespireTelemetry.ErrorObservation observation)
    {
        // Initialization alone does not wait for an established socket's replacement. As in the
        // standalone owner, an open circuit rejects before the wait and again at admission.
        if (attempt.Number > 1 && _core.Cluster is null)
        {
            await _core.EnsureConnectedAsync(attempt.Token, observation).ConfigureAwait(false);
            if (_core.Circuits is not null) RejectOpenCircuit(attempt.Token);
            _ = await _core.Multiplexer.GetHealthyConnectionAsync(attempt.Token).ConfigureAwait(false);
        }
        return await send(state, attempt).ConfigureAwait(false);
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
        bool track) : IRespCommandWrapper where TCommand : struct, IRespCommand
    {
        public int GetWriteSizeHint() => checked(state.Frame.Length + (track ? new ClientCachingCommand().GetWriteSizeHint() : 0));
        public void Write(ref RespWriter writer)
        {
            if (track) new ClientCachingCommand().Write(ref writer);
            writer.WriteRaw(state.Frame.Span);
        }
        public ReadCommandKind ReadKind => command.ReadKind;
        public RespireCommandRetryCategory RetryCategory => command.RetryCategory;
        public bool TryGetClusterSlot(out int slot) => state.TryGetClusterSlot(out slot);
        public bool IsConnectionProtocol => false;
        public void ValidateAdmission() => command.ValidateAdmission();
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
        public void OnAccepted() { state.Accepted(); command.OnAccepted(); }
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    }
}
