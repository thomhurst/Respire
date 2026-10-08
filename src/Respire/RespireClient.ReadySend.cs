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
        => !RespireTelemetry.Source.HasListeners()
            && (!RespireTelemetry.IsOperationEnabled(operation) || !ScriptingEngineInfo.IsScriptingCommand(operation))
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && (ReadCache is null || !command.GetClientCacheMetadata(operation).CacheableRead);

    private ValueTask<RespValue> SendOnReadyPrimaryAsync<TCommand>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
        // Raw sends keep their mutation fence in the outer SendAsync path.
        => SendOnReadyPrimaryAsync<TCommand, RespValue, RawReadySend>(
            operation, multiplexer, command, cancellationToken, new RawReadySend(this, observation), observeSelectionErrors: false);

    private ValueTask<TResult> SendOnReadyPrimaryAsync<TCommand, TResult, TSend>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken, TSend sender, ClientSideCacheCoordinator? cache = null,
        ClientSideCacheCoordinator.MutationFence mutationFence = default, bool observeSelectionErrors = true)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        var durationStarted = sender.ObserveDuration ? RespireTelemetry.CaptureOperationStart(operation) : default;
        RespireConnection connection;
        try { connection = multiplexer.GetConnection(); }
        catch (Exception error)
        {
            if (_core.Sentinel is not null)
                return CaptureReadySendFailure<TResult>(error, cache, mutationFence, observeSelectionErrors);
            if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
            if (observeSelectionErrors) RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
        if (mutationFence.IsRequired)
            return SendReadyMutationAsync<TCommand, TResult, TSend>(connection, operation, in command, cancellationToken, sender,
                cache!, mutationFence, durationStarted);
        try
        {
            return sender.Send(connection, operation, in command, cancellationToken, durationStarted);
        }
        // _core is readonly: this filter observes the same core captured by the caller.
        catch (Exception error) when (_core.Sentinel is not null || durationStarted.MetricEnabled)
        {
            return CaptureReadySendFailure<TResult>(error);
        }
    }

    // A typed source reports in GetResult, while the awaiting fence is still open. The fenced
    // path lends it an owner instead and reports once, after the logical mutation completes.
    private static ValueTask<TResult> SendReadyMutationAsync<TCommand, TResult, TSend>(
        RespireConnection connection, string operation, in TCommand command, CancellationToken cancellationToken,
        TSend sender, ClientSideCacheCoordinator cache, ClientSideCacheCoordinator.MutationFence mutationFence,
        RespireTelemetry.OperationStart durationStarted)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        ValueTask<TResult> response;
        try
        {
            response = sender.Send(connection, operation, new MutationCommand<TCommand>(command, mutationFence),
                cancellationToken, observation, durationStarted);
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
            catch (Exception error)
            {
                cache.CompleteMutation(in fence, false);
                observation.Final(error);
                throw;
            }
            cache.CompleteMutation(in fence, true);
            return result;
        }
        finally { observation.Dispose(); }
    }

    // Constrained value-type dispatch keeps each specialized response source without a delegate or boxing.
    private interface IReadySend<TResult>
    {
        bool ObserveDuration { get; }
        ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand;
        // A supplied observation is borrowed: the source records nothing itself.
        ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand;
    }

    private readonly struct RawReadySend(RespireClient client, RespireTelemetry.ErrorObservation observation) : IReadySend<RespValue>
    {
        public bool ObserveDuration => false;
        public ValueTask<RespValue> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => client.SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: observation);
        public ValueTask<RespValue> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation borrowed,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => client.SendOnConnectionAsync(operation, connection, command, cancellationToken, observation: borrowed);
    }

    private readonly struct StringReadySend : IReadySend<string?>, IClusterReadySend<string?>
    {
        public bool ObserveDuration => true;
        public bool TransferOwnership => false;
        public string? Convert(in RespValue response) => ResponseReader.StringOrNull(in response);

        public ValueTask<string?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => connection.SendStringAsync(in command, cancellationToken, operation, durationStarted: durationStarted);

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
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => connection.SendBytesAsync(in command, cancellationToken, operation, durationStarted: durationStarted);

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
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, state, converter, transferOwnership, cancellationToken, operation,
                durationStarted: durationStarted);
        public ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation,
            RespireTelemetry.OperationStart durationStarted) where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, state, converter, transferOwnership, cancellationToken, operation,
                observation: observation, durationStarted: durationStarted);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TResult> CaptureReadySendFailure<TResult>(Exception error,
        ClientSideCacheCoordinator? cache = null, ClientSideCacheCoordinator.MutationFence mutationFence = default,
        bool reportError = false)
    {
        // Readiness is an observation, not a lease: socket loss, retirement, or disposal
        // can make GetConnection or admission throw after validation. A command writer
        // can also throw before admission. Preserve the former async acquisition result.
        if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
        if (reportError) RespireTelemetry.RecordError(error, internallyHandled: false);
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
