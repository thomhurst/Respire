using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private Func<RespireClient>? _retryClientSelector;
    private CommandRetryPolicy? _groupCommandRetries;
    private CommandRetryPolicy? ImmediateRetries => _groupCommandRetries ?? _core.CommandRetries;

    internal RespireClient WithRetrySelection(Func<RespireClient> selector, RespireRetryPolicy policy)
        => new(_core, _keyPrefix, ownsCore: false, readFrom: _readFrom)
        {
            _retryClientSelector = selector,
            _groupCommandRetries = new CommandRetryPolicy(policy),
        };

    internal ValueTask<RespireResult> ExecuteGroupCommandAsync(RespireCommand command, RespireValue[] args,
        RespireCommandFlags flags, CancellationToken token, RespireTelemetry.ErrorObservation observation)
        => ExecuteCommandAsync(command, args, flags, token, observation);

    private bool CanRetryImmediate<TCommand>(in TCommand command) where TCommand : struct, IRespCommand
        => ImmediateRetries is { IsEnabled: true }
            && !_snapshotPrefixedBinaryKeys && command is not IStreamingRespCommand
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && !CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
            && command.ReadKind != ReadCommandKind.CursorRead
            && command.RetryCategory != RespireCommandRetryCategory.Never;

    // This method starts synchronously: caller memory is copied before any asynchronous selection.
    // The original command retains admission callbacks and its logical fence; only serialization
    // uses the snapshot. Each transport source still retains its own fence until FIFO retirement.
    private ValueTask<RespValue> SendImmediateWithRetriesAsync<TCommand>(string operation, TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool track = false,
        bool noRedirect = false, CommandDeadline deadline = default, SnapshotCommand? noScriptFallback = null)
        where TCommand : struct, IRespCommand
    {
        deadline = CreateCircuitDeadline(deadline);
        cancellationToken.ThrowIfCancellationRequested();
        var frame = SnapshotCommand.Create(in command, captureCacheMetadata: false);
        var state = new ImmediateRetryState(command.RetryCategory, deadline, frame, operation, _core.Options.CommandTimeout)
        { NoScriptFallback = noScriptFallback };
        return SendImmediateRetryCoreAsync(operation, new ImmediateRetryCommand<TCommand>(command, state, track),
            state, cancellationToken, deadline, observation, noRedirect);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendImmediateRetryCoreAsync<TCommand>(string operation, TCommand command,
        ImmediateRetryState state, CancellationToken callerToken, CommandDeadline deadline,
        RespireTelemetry.ErrorObservation observation, bool noRedirect) where TCommand : struct, IRespCommand
    {
        using var timeout = deadline.IsSet
            ? CommandTimeoutCancellation.Create(callerToken, TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
            : null;
        var token = timeout?.Token ?? callerToken;
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
            for (var attempt = 1; ; attempt++)
            {
                ThrowIfRetryStopped(operation, token, deadline);
                ObjectDisposedException.ThrowIf(_core.Disposed, this);
                state.Outcome = CommandWriteOutcome.DefinitelyUnwritten;
                state.IsRetry = attempt > 1;
                state.AcceptedAttempt = false;
                CircuitAdmission admission = default;
                try
                {
                    var target = _retryClientSelector?.Invoke() ?? this;
                    if (target._core.Cluster is { } cluster)
                    {
                        var reply = await target.SendClusterAsync(operation, cluster, command, token,
                            noRedirect: noRedirect, suppressTelemetry: true, observation: observation).ConfigureAwait(false);
                        if (state.Retries != 0) RespireTelemetry.RecordCommandRetry(state.Category, "succeeded");
                        return reply;
                    }
                    await target._core.EnsureConnectedAsync(token, observation).ConfigureAwait(false);
                    // Initialization alone does not wait for an established socket's replacement.
                    // Do not spend the command attempt budget probing the same dead connection.
                    // An open circuit still rejects before the wait, and again at admission below.
                    if (attempt > 1 && target._core.Circuits is not null) target.RejectOpenCircuit(token);
                    connection = attempt > 1
                        ? await target._core.Multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false)
                        : target.GetCircuitAwareConnection(target._core.Multiplexer, token);
                    observation.UseEndpoint(new(connection.Host, connection.Port), token);
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    connection.ThrowIfRetired();
                    if (target._core.Circuits is not null) admission = target.AcquireCircuit(connection, token);
                    ThrowIfRetryStopped(operation, token, deadline);
                    var response = await SendRetryAttemptAsync(operation, connection, command, token,
                        sendAsking: false, observation).ConfigureAwait(false);
                    admission.Success();
                    if (state.Retries != 0) RespireTelemetry.RecordCommandRetry(state.Category, "succeeded");
                    return response;
                }
                catch (RespireServerException error) when (error.Code == RespireErrorCodes.NoScript
                    && state.NoScriptFallback is not null)
                {
                    admission.Failed(error, token);
                    observation.Handled(error);
                    state.Frame = state.NoScriptFallback.Value;
                    state.NoScriptFallback = null;
                    // NOSCRIPT proves no execution. Correction consumes no transient retry budget.
                    attempt--;
                }
                catch (Exception error)
                {
                    admission.Failed(error, token);
                    var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
                    if (!ImmediateRetries!.TryGetDelay(error, state.Outcome, state.Category, attempt, remaining, token, out var delay))
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
            connection = state.Connection ?? connection;
            if (connection is not null) telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
            if (finalError is not null && state.Retries != 0)
                RespireTelemetry.RecordCommandRetry(state.Category, "exhausted");
            try { telemetry.Complete(_core, operation, error: finalError, connection: connection); }
            catch (Exception) { /* A listener cannot replace the logical command outcome. */ }
        }
    }

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

    private interface IImmediateRetryCommand : IRespCommand
    {
        ImmediateRetryState State { get; }
        int PreludeReplies { get; }
    }

    private static async ValueTask<RespValue> SendRetryAttemptAsync<TCommand>(string operation,
        RespireConnection connection, TCommand command, CancellationToken token, bool sendAsking,
        RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        var retry = (IImmediateRetryCommand)command;
        var state = retry.State;
        state.Connection = connection;
        state.Outcome = CommandWriteOutcome.DefinitelyUnwritten;
        var result = sendAsking
            ? await connection.SendAttemptAsync(new AskingRetryCommand<TCommand>(command), token, operation,
                state.Deadline, repliesBeforeFinal: retry.PreludeReplies + 1, errorObservation: observation).ConfigureAwait(false)
            : await connection.SendAttemptAsync(command, token, operation, state.Deadline,
                repliesBeforeFinal: retry.PreludeReplies, errorObservation: observation).ConfigureAwait(false);
        state.Outcome = result.WriteOutcome;
        if (state.IsRetry && state.AcceptedAttempt)
            RespireTelemetry.RecordCommandRetry(state.Category, "attempted");
        var response = result.GetResult();
        if (!response.IsError) return response;
        var error = ResponseReader.ServerError(in response, operation);
        response.Dispose();
        throw error;
    }

    private readonly struct AskingRetryCommand<TCommand>(TCommand command) : IRespCommandWrapper
        where TCommand : struct, IRespCommand
    {
        private static readonly Cmd Asking = new(RespireCommands.Cluster.ASKING.Verb);
        public int GetWriteSizeHint() => CommandWriteSizeHint.Combine(Asking.GetWriteSizeHint(), command.GetWriteSizeHint());
        public void Write(ref RespWriter writer) { Asking.Write(ref writer); command.Write(ref writer); }
        public ReadCommandKind ReadKind => command.ReadKind;
        public bool IsConnectionProtocol => false;
        public void OnAccepted() => command.OnAccepted();
        public void ValidateAdmission() => command.ValidateAdmission();
        public CancellationToken GetResponseCancellationToken(CancellationToken token) => command.GetResponseCancellationToken(token);
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    }

    private sealed class ImmediateRetryState(RespireCommandRetryCategory category, CommandDeadline deadline,
        SnapshotCommand frame, string operation, TimeSpan? timeout)
    {
        internal RespireCommandRetryCategory Category { get; } = category;
        internal CommandDeadline Deadline { get; } = deadline;
        internal string Operation { get; } = operation;
        internal TimeSpan? Timeout { get; } = timeout;
        internal RespireConnection? Connection;
        internal SnapshotCommand Frame = frame;
        internal SnapshotCommand? NoScriptFallback;
        internal CommandWriteOutcome Outcome;
        internal bool IsRetry;
        internal bool AcceptedAttempt;
        internal int Retries;
        internal void Accepted()
        {
            AcceptedAttempt = true;
            if (!IsRetry) return;
            Retries++;
        }
    }

    private readonly struct ImmediateRetryCommand<TCommand>(TCommand command,
        ImmediateRetryState state, bool track) : IRespCommandWrapper, IImmediateRetryCommand where TCommand : struct, IRespCommand
    {
        public ImmediateRetryState State => state;
        public int PreludeReplies => track ? 1 : 0;
        public bool TryGetClusterSlot(out int slot) => state.Frame.TryGetClusterSlot(out slot);
        public int GetWriteSizeHint() => checked(state.Frame.GetWriteSizeHint() + (track ? new ClientCachingCommand().GetWriteSizeHint() : 0));
        public void Write(ref RespWriter writer)
        {
            if (track) new ClientCachingCommand().Write(ref writer);
            state.Frame.Write(ref writer);
        }
        public ReadCommandKind ReadKind => command.ReadKind;
        public bool IsConnectionProtocol => false;
        public void ValidateAdmission()
        {
            if (state.Deadline.IsSet && state.Deadline.RemainingMilliseconds == 0)
                throw new RespireTimeoutException(state.Operation, state.Timeout!.Value);
            command.ValidateAdmission();
        }
        public CancellationToken GetResponseCancellationToken(CancellationToken admissionToken) => admissionToken;
        public void OnAccepted() { state.Accepted(); command.OnAccepted(); }
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => CommandDispatchAdmission<TCommand>.GetMutationFence(in command);
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
    }
}
