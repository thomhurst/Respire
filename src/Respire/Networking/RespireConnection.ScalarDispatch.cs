using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // SendNativeConvertedAsync for the stateless scalar shapes: the admitted source is
    // specialized on the converter struct. Capacity waits and retirement recovery keep the
    // dispatch owner and the general converted route, as the converted path does.
    internal ValueTask<TResult> SendNativeScalarAsync<TCommand, TConverter, TResult>(in TCommand command,
        CancellationToken cancellationToken, string operation)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        var started = RespireTelemetry.CaptureOperationStart(operation);
        var source = ScalarPendingResponseSource<TConverter, TResult>.Rent(operation,
            duration: new RespireTelemetry.DurationObservation(this, started));
        var response = source.Task;
        if (TryAdmitNative(in command, source, cancellationToken, started.Timestamp != 0, out var destination, out var deadline, out var retirement)) return response;
        return DispatchResponseSource<TResult>.Run(
            (Connection: destination, Command: command, Token: cancellationToken, Operation: operation, Deadline: deadline,
                Retirement: retirement, Started: started, FailedEndpoint: new RespireEndpoint(Host, Port)),
            static (state, observation) =>
            {
                if (state.Retirement is not null) observation.Handled(state.Retirement, state.FailedEndpoint);
                return state.Connection.SendConvertedAsync(state.Command, default(TConverter),
                    ScalarResponseConverter<TConverter, TResult>.Delegate, false,
                    state.Token, state.Operation, commandDeadline: state.Deadline, observation: observation, durationStarted: state.Started);
            });
    }

    // SendConvertedAsync for the stateless scalar shapes under a dispatch owner (the ready-send
    // route). Only immediate admission uses the specialized source; reroutes, capacity waits and
    // scripting replies take the general converted route with the same owner and deadline.
    internal ValueTask<TResult> SendScalarAsync<TCommand, TConverter, TResult>(
        in TCommand command, CancellationToken cancellationToken, string? commandName,
        CommandDeadline commandDeadline, RespireTelemetry.ErrorObservation observation,
        RespireTelemetry.OperationStart durationStarted, bool pinToConnection)
        where TCommand : struct, IRespCommand
        where TConverter : struct, IScalarResponseConverter<TResult>
    {
        if (observation.IsEmpty || ScriptingEngineInfo.IsScriptingCommand(commandName))
            return SendConvertedAsync(in command, default(TConverter), ScalarResponseConverter<TConverter, TResult>.Delegate,
                false, cancellationToken, commandName, commandDeadline, observation: observation,
                durationStarted: durationStarted, pinToConnection: pinToConnection);
        observation.UseEndpoint(new(Host, Port), cancellationToken);
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var duration = new RespireTelemetry.DurationObservation(this, durationStarted);
        var source = ScalarPendingResponseSource<TConverter, TResult>.Rent(commandName, observation.Attempts,
            observeErrors: false, duration: duration);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, commandDeadline, out startedBatch);
        }
        catch (RespireConnectionRetiredException error) when (TryReroute(pinToConnection, commandDeadline, out var target, out var rerouted, preferredZone: null))
        {
            ReclaimUnpublished(source);
            observation.Handled(error, new(Host, Port));
            return target.SendConvertedAsync(in command, default(TConverter), ScalarResponseConverter<TConverter, TResult>.Delegate,
                false, cancellationToken, commandName, rerouted, 1, observation, durationStarted, pinToConnection);
        }
        catch (Exception error)
        {
            duration.Complete(commandName, error);
            ReclaimUnpublished(source);
            throw;
        }

        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }

        // Nothing was published: the converted route retries admission and waits for capacity.
        ReclaimUnpublished(source);
        return SendConvertedAsync(in command, default(TConverter), ScalarResponseConverter<TConverter, TResult>.Delegate,
            false, cancellationToken, commandName, commandDeadline, observation: observation,
            durationStarted: durationStarted, pinToConnection: pinToConnection);
    }
}
