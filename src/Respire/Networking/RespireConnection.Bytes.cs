using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    internal ValueTask<byte[]?> SendBytesAsync<TCommand>(
        in TCommand command, CancellationToken cancellationToken = default,
        string? commandName = null, CommandDeadline commandDeadline = default, int errorAttempts = 0,
        RespireTelemetry.ErrorObservation observation = default, RespireTelemetry.OperationStart durationStarted = default, bool pinToConnection = false)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var duration = new RespireTelemetry.DurationObservation(this, durationStarted);
        var source = BytesPendingResponseSource.Rent(commandName, (observation.IsEmpty ? errorAttempts : observation.Attempts),
            observeErrors: observation.IsEmpty, duration: duration);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, commandDeadline, out startedBatch);
        }
        catch (RespireConnectionRetiredException error) when (TryReroute(pinToConnection, commandDeadline, out var target, out var rerouted, preferredZone: null))
        {
            ReclaimUnpublished(source);
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
            else observation.Handled(error);
            return target.SendBytesAsync(in command, cancellationToken, commandName, rerouted, errorAttempts + 1, observation, durationStarted, pinToConnection);
        }
        catch (Exception error)
        {
            duration.Complete(commandName, error);
            ReclaimUnpublished(source);
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false, errorAttempts);
            throw;
        }
        if (enqueued)
        {
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }
        return SendBytesSlowAsync(command, source, cancellationToken, commandName, commandDeadline, errorAttempts, observation, durationStarted, pinToConnection);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<byte[]?> SendBytesSlowAsync<TCommand>(
        TCommand command, BytesPendingResponseSource source, CancellationToken cancellationToken,
        string? commandName, CommandDeadline commandDeadline, int errorAttempts, RespireTelemetry.ErrorObservation observation,
        RespireTelemetry.OperationStart durationStarted, bool pinToConnection)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                command, source, 0, cancellationToken, commandDeadline: commandDeadline).ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException error) when (TryReroute(pinToConnection, commandDeadline, out var target, out var rerouted, preferredZone: null))
        {
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: true, errorAttempts);
            else observation.Handled(error);
            return await target.SendBytesAsync(in command, cancellationToken, commandName, rerouted, errorAttempts + 1, observation, durationStarted, pinToConnection).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            var duration = new RespireTelemetry.DurationObservation(this, durationStarted);
            duration.Complete(commandName, error);
            if (observation.IsEmpty) RespireTelemetry.RecordError(error, internallyHandled: false, errorAttempts);
            throw;
        }
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }
}
