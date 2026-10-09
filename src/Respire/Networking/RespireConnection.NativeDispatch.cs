using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // Only an immediately admitted ordinary command transfers final ownership to its
    // native source. Capacity waits and retirement recovery keep the dispatch owner.
    private bool TryAdmitNative<TCommand>(in TCommand command, PendingResponse source,
        CancellationToken cancellationToken, bool asyncAdmissionFailure, out RespireConnection destination,
        out CommandDeadline deadline, out RespireConnectionRetiredException? retirement)
        where TCommand : struct, IRespCommand
    {
        destination = this;
        deadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        retirement = null;
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, deadline, out startedBatch);
        }
        catch (RespireConnectionRetiredException error) when (TryReroute(false, deadline, out destination, out deadline, preferredZone: null))
        {
            ReclaimUnpublished(source);
            retirement = error;
            return false;
        }
        catch (Exception error)
        {
            if (!asyncAdmissionFailure)
            {
                // Uninstrumented ready sends retain their synchronous failure boundary.
                // Neither the caller nor the receive ring owns this unpublished source.
                ReclaimUnpublished(source);
                RespireTelemetry.RecordError(error, internallyHandled: false);
                throw;
            }
            // Preserve the async admission boundary and let native inspection publish
            // duration and final error once. No receive entry owns the second reference.
            source.TrySetException(error);
            source.ReleaseRef();
            return true;
        }
        if (!enqueued)
        {
            ReclaimUnpublished(source);
            return false;
        }
        source.RegisterCancellation(command.GetResponseCancellationToken(cancellationToken));
        ScheduleFlush(startedBatch);
        return true;
    }

    internal ValueTask<RespValue> SendNativeCheckedAsync<TCommand>(in TCommand command, CancellationToken cancellationToken,
        string operation)
        where TCommand : struct, IRespCommand
    {
        var source = _sourcePool.Rent(throwOnError: true, operation, observeErrors: true);
        // Capture the token before admission: completion may race publication.
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, false, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<RespValue>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline, Retirement: retirement),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement);
                return state.Connection.SendCheckedAsync(state.Command, state.Token, state.Operation,
                    commandDeadline: state.Deadline, observation: observation);
            });
    }

    internal ValueTask<string?> SendNativeStringAsync<TCommand>(in TCommand command, CancellationToken cancellationToken,
        string operation)
        where TCommand : struct, IRespCommand
    {
        var started = RespireTelemetry.CaptureOperationStart(operation);
        var source = StringPendingResponseSource.Rent(operation,
            duration: new RespireTelemetry.DurationObservation(this, started));
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, started.Timestamp != 0, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<string?>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline, Retirement: retirement, Started: started),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement);
                return state.Connection.SendStringAsync(state.Command, state.Token, state.Operation,
                    commandDeadline: state.Deadline, observation: observation, durationStarted: state.Started);
            });
    }

    internal ValueTask<byte[]?> SendNativeBytesAsync<TCommand>(in TCommand command, CancellationToken cancellationToken,
        string operation)
        where TCommand : struct, IRespCommand
    {
        var started = RespireTelemetry.CaptureOperationStart(operation);
        var source = BytesPendingResponseSource.Rent(operation,
            duration: new RespireTelemetry.DurationObservation(this, started));
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, started.Timestamp != 0, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<byte[]?>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline, Retirement: retirement, Started: started),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement);
                return state.Connection.SendBytesAsync(state.Command, state.Token, state.Operation,
                    commandDeadline: state.Deadline, observation: observation, durationStarted: state.Started);
            });
    }

    internal ValueTask<TResult> SendNativeConvertedAsync<TCommand, TState, TResult>(in TCommand command,
        CancellationToken cancellationToken, string operation, TState state,
        ResponseConverter<TState, TResult> converter, bool transferOwnership)
        where TCommand : struct, IRespCommand
    {
        var started = RespireTelemetry.CaptureOperationStart(operation);
        var source = ConvertedPendingResponseSource<TState, TResult>.Rent(state, converter, transferOwnership, operation,
            duration: new RespireTelemetry.DurationObservation(this, started));
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, started.Timestamp != 0, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<TResult>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline,
                Retirement: retirement, Started: started, State: state, Converter: converter, Transfer: transferOwnership),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement);
                return state.Connection.SendConvertedAsync(state.Command, state.State, state.Converter, state.Transfer,
                    state.Token, state.Operation, commandDeadline: state.Deadline, observation: observation, durationStarted: state.Started);
            });
    }
}
