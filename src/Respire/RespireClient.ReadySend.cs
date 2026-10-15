using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Raw SendAsync has already handled telemetry, cache hits, and replica/cluster routing.
    // Typed entry points share these gates before selecting a generation or opening a fence.
    // Callers retain their response-specific streaming exclusions.
    private bool CanUseDirectReplySource<TCommand>(string operation, in TCommand command)
        where TCommand : struct, IRespCommand
        // With no listeners, IsOperationEnabled reduces to the command metric check, which only
        // scripting commands need here; the ready send captures that decision once afterwards.
        => _core.CommandRetries is null && !RespireTelemetry.Source.HasListeners()
            && (!ScriptingEngineInfo.IsScriptingCommand(operation) || !RespireTelemetry.IsCommandMetricEnabled(operation))
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && (ReadCache is null || !command.GetClientCacheMetadata(operation).CacheableRead);

    private ValueTask<RespValue> SendOnReadyPrimaryAsync<TCommand>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        // Raw sends keep their mutation fence in the outer SendAsync path.
        => SendOnReadyPrimaryAsync<TCommand, RespValue, RawReadySend>(
            operation, multiplexer, command, cancellationToken, new RawReadySend(this, observation),
            observeSelectionErrors: false, observation: observation);

    private ValueTask<TResult> SendOnReadyPrimaryAsync<TCommand, TResult, TSend>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken, TSend sender, ClientSideCacheCoordinator? cache = null,
        ClientSideCacheCoordinator.MutationFence mutationFence = default, bool observeSelectionErrors = true,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        // Duration senders are reached only through CanUseDirectReplySource, which excluded listeners.
        var durationStarted = sender.ObserveDuration ? RespireTelemetry.CaptureCommandDurationStart(operation) : default;
        RespireConnection? connection;
        try
        {
            connection = UsesPrimaryBacklog && !CommandDispatchAdmission<TCommand>.IsConnectionProtocol(in command)
                && !CursorCommandMetadata.IsServerCursorContinuation(in command)
                ? SelectPrimaryBacklogConnection(cancellationToken)
                : GetCircuitAwareConnection(multiplexer, cancellationToken);
        }
        catch (Exception error)
        {
            // The captured generation names the attempted data node, with or without Sentinel.
            var endpoint = cancellationToken.IsCancellationRequested ? (RespireEndpoint?)null : multiplexer.ActiveConnectionEndpoint;
            if (_core.Sentinel is not null)
                return CaptureReadySendFailure<TResult>(error, cache, mutationFence, observeSelectionErrors, endpoint);
            if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
            if (observeSelectionErrors) RespireTelemetry.RecordError(error, internallyHandled: false, endpoint: endpoint);
            throw;
        }
        if (connection is null)
            return SendBackloggedReadyAsync<TCommand, TResult, TSend>(operation, command, cancellationToken, sender,
                durationStarted, cache, mutationFence, observeSelectionErrors, observation);
        if (sender.ObserveDuration && _core.Circuits is not null && !_snapshotPrefixedBinaryKeys)
            return SendCircuitReadyAsync<TCommand, TResult, TSend>(operation, connection, command, cancellationToken, sender,
                durationStarted, cache, mutationFence, observation);
        if (mutationFence.IsRequired)
            return SendReadyMutationAsync<TCommand, TResult, TSend>(connection, operation, in command, cancellationToken, sender,
                cache!, mutationFence, durationStarted, observation);
        try
        {
            return sender.Send(connection, operation, in command, cancellationToken, durationStarted, errorObservation: observation);
        }
        // _core is readonly: this filter observes the same core captured by the caller.
        catch (Exception error) when (_core.Sentinel is not null || durationStarted.MetricEnabled)
        {
            return CaptureReadySendFailure<TResult>(error);
        }
    }

    private async ValueTask<TResult> SendBackloggedReadyAsync<TCommand, TResult, TSend>(
        string operation, TCommand command, CancellationToken cancellationToken, TSend sender,
        RespireTelemetry.OperationStart durationStarted, ClientSideCacheCoordinator? cache,
        ClientSideCacheCoordinator.MutationFence mutationFence, bool observeErrors,
        RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        var dispatched = false;
        try
        {
            var deadline = CreateCircuitDeadline();
            var snapshot = new BacklogCommand<TCommand>(command, operation);
            return await RunPrimaryBacklogAsync(operation, cancellationToken, deadline, (connection, originalDeadline) =>
            {
                dispatched = true;
                if (sender.ObserveDuration && _core.Circuits is not null && !_snapshotPrefixedBinaryKeys)
                    return SendCircuitReadyAsync<BacklogCommand<TCommand>, TResult, TSend>(operation, connection, snapshot,
                        cancellationToken, sender, durationStarted, cache, mutationFence, observation, originalDeadline);
                if (mutationFence.IsRequired)
                    return SendReadyMutationAsync<BacklogCommand<TCommand>, TResult, TSend>(connection, operation, snapshot,
                        cancellationToken, sender, cache!, mutationFence, durationStarted, observation, originalDeadline);
                return sender.Send(connection, operation, snapshot, cancellationToken, durationStarted, originalDeadline,
                    errorObservation: observation);
            }, observation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!dispatched)
            {
                if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
                if (observeErrors) RespireTelemetry.RecordError(error, internallyHandled: false,
                    endpoint: _core.Multiplexer.ActiveConnectionEndpoint);
            }
            throw;
        }
    }

    // A typed source reports in GetResult, while the awaiting fence is still open. The fenced
    // path lends it an owner instead and reports once, after the logical mutation completes.
    private static ValueTask<TResult> SendReadyMutationAsync<TCommand, TResult, TSend>(
        RespireConnection connection, string operation, in TCommand command, CancellationToken cancellationToken,
        TSend sender, ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.MutationFence mutationFence,
        RespireTelemetry.OperationStart durationStarted, RespireTelemetry.ErrorObservation observation,
        CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        ValueTask<TResult> response;
        try
        {
            response = sender.Send(connection, operation, new MutationCommand<TCommand>(command, mutationFence),
                cancellationToken, durationStarted, commandDeadline, errorObservation: observation);
        }
        catch (Exception error)
        {
            // The async boundary keeps cancellation status, as the former ready-send failure path did.
            response = ValueTask.FromException<TResult>(error);
        }
        return CompleteObservedMutationAsync(response, cache, mutationFence, observation);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<TResult> CompleteObservedMutationAsync<TResult>(
        ValueTask<TResult> response, ClientSideCacheCoordinator cache,
        ClientSideCacheCoordinator.MutationFence fence, RespireTelemetry.ErrorObservation observation)
    {
        try
        {
            TResult result;
            try { result = await response.ConfigureAwait(false); }
            catch
            {
                try { cache.CompleteMutation(in fence, false); }
                catch (Exception)
                {
                    // Cleanup cannot replace the response failure or turn cancellation into a fault.
                }
                throw;
            }
            cache.CompleteMutation(in fence, true);
            return result;
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
        finally { observation.Dispose(); }
    }

    // Constrained value-type dispatch keeps each specialized response source without a delegate or boxing.
    private interface IReadySend<TResult>
    {
        bool ObserveDuration { get; }
        ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted, CommandDeadline commandDeadline = default,
            bool pinToConnection = false, RespireTelemetry.ErrorObservation errorObservation = default) where TCommand : struct, IRespCommand;
    }

    private readonly struct RawReadySend(RespireClient client, RespireTelemetry.ErrorObservation observation) : IReadySend<RespValue>
    {
        public bool ObserveDuration => false;
        public ValueTask<RespValue> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted, CommandDeadline commandDeadline = default,
            bool pinToConnection = false, RespireTelemetry.ErrorObservation errorObservation = default) where TCommand : struct, IRespCommand
            => client.SendOnConnectionAsync(operation, connection, command, cancellationToken,
                commandDeadline: commandDeadline, pinToConnection: pinToConnection, observation: observation);
    }

    private readonly struct StringReadySend : IReadySend<string?>, IClusterReadySend<string?>
    {
        public bool ObserveDuration => true;
        public bool TransferOwnership => false;
        public string? Convert(in RespValue response) => ResponseReader.StringOrNull(in response);

        public ValueTask<string?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted, CommandDeadline commandDeadline = default,
            bool pinToConnection = false, RespireTelemetry.ErrorObservation errorObservation = default) where TCommand : struct, IRespCommand
            => connection.SendStringAsync(in command, cancellationToken, operation,
                commandDeadline: commandDeadline, durationStarted: durationStarted, pinToConnection: pinToConnection, observation: errorObservation);

        public ValueTask<string?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted)
            where TCommand : struct, IRespCommand
            => connection.SendStringAsync(in command, cancellationToken, operation, observation: observation, durationStarted: durationStarted);
    }

    private readonly struct BytesReadySend : IReadySend<byte[]?>, IClusterReadySend<byte[]?>
    {
        public bool ObserveDuration => true;
        public bool TransferOwnership => false;
        public byte[]? Convert(in RespValue response) => ResponseReader.BytesOrNull(in response);

        public ValueTask<byte[]?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted, CommandDeadline commandDeadline = default,
            bool pinToConnection = false, RespireTelemetry.ErrorObservation errorObservation = default) where TCommand : struct, IRespCommand
            => connection.SendBytesAsync(in command, cancellationToken, operation,
                commandDeadline: commandDeadline, durationStarted: durationStarted, pinToConnection: pinToConnection, observation: errorObservation);

        public ValueTask<byte[]?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted)
            where TCommand : struct, IRespCommand
            => connection.SendBytesAsync(in command, cancellationToken, operation, observation: observation, durationStarted: durationStarted);
    }

    private readonly struct ConvertedReadySend<TState, TResult>(
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership) : IReadySend<TResult>
    {
        public bool ObserveDuration => true;
        public ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted, CommandDeadline commandDeadline = default,
            bool pinToConnection = false, RespireTelemetry.ErrorObservation errorObservation = default) where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, state, converter, transferOwnership, cancellationToken, operation,
                commandDeadline: commandDeadline, durationStarted: durationStarted, pinToConnection: pinToConnection, observation: errorObservation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TResult> CaptureReadySendFailure<TResult>(Exception error,
        ClientSideCacheCoordinator? cache = null, ClientSideCacheCoordinator.MutationFence mutationFence = default,
        bool reportError = false, RespireEndpoint? endpoint = null)
    {
        // Readiness is an observation, not a lease: socket loss, retirement, or disposal
        // can make GetConnection or admission throw after validation. A command writer
        // can also throw before admission. Preserve the former async acquisition result.
        if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
        if (reportError) RespireTelemetry.RecordError(error, internallyHandled: false, endpoint: endpoint);
        return ReadySendFailureAsync<TResult>(error);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<TResult> ReadySendFailureAsync<TResult>(Exception error)
        // Keep the async route's cancellation status and original exception/token,
        // including OperationCanceledException carrying an uncanceled token. Only failures use this.
        => await ValueTask.FromException<TResult>(error).ConfigureAwait(false);
}
