using Respire.Commands;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    // Ordered batches must await admission of both frames before admitting their next member,
    // while leaving reply completion independent. Retain and validate the caching reply.
    private async ValueTask<ValueTask<RespValue>> EnqueueOptOutAsync<TCommand>(
        TCommand command, CancellationToken cancellationToken, string commandName,
        RespireTelemetry.ErrorObservation observation, bool pinToConnection, CommandDeadline deadline)
        where TCommand : struct, IRespCommand
    {
        if (_inflight.Capacity < 2)
        {
            observation.UseEndpoint(null);
            throw new InvalidOperationException("An OPTOUT bypass read needs 2 in-flight slots.");
        }
        observation.UseEndpoint(new(Host, Port), cancellationToken);
        if (cancellationToken.IsCancellationRequested)
            throw new RespireCommandNotSubmittedException(new OperationCanceledException(cancellationToken));
        if (command is IStreamingRespCommand)
            throw new NotSupportedException("Ordered admission does not support streaming command payloads.");
        if (!deadline.IsSet) deadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var prefixed = new PrefixedCommand<ClientCachingCommand, TCommand>(new(optOut: true), command, _cacheMutationAdmission);
        var source = MultiReplyPendingResponseSource.Rent(2, 0, commandName);
        source.ErrorAttempts = observation.Attempts;
        bool enqueued;
        bool startedBatch;
        try { enqueued = TryEnqueue(in prefixed, source, deadline, out startedBatch, 1, retainRepliesBefore: true); }
        catch (RespireConnectionRetiredException error) when (TryReroute(pinToConnection, deadline, out var target, out var rerouted, preferredZone: null))
        {
            ReclaimUnpublished(source, 3);
            observation.Handled(error, new(Host, Port));
            return await target.EnqueuePinnedAsync(command, cancellationToken, commandName, observation,
                pinToConnection, rerouted, skipTracking: true).ConfigureAwait(false);
        }
        catch { ReclaimUnpublished(source, 3); throw; }
        if (!enqueued)
        {
            try
            {
                startedBatch = await WaitForInflightCapacityAsync(prefixed, source, 1,
                    cancellationToken, deadline, retainRepliesBefore: true).ConfigureAwait(false);
            }
            catch (RespireConnectionRetiredException error) when (TryReroute(pinToConnection, deadline, out var target, out var rerouted, preferredZone: null))
            {
                // The capacity waiter already reclaimed all three unpublished references.
                observation.Handled(error, new(Host, Port));
                return await target.EnqueuePinnedAsync(command, cancellationToken, commandName, observation,
                    pinToConnection, rerouted, skipTracking: true).ConfigureAwait(false);
            }
        }
        source.RegisterCancellation(command.GetResponseCancellationToken(cancellationToken));
        ScheduleFlush(startedBatch);
        return ObserveScriptingReply(source.Task, in command, commandName, cancellationToken, deadline);
    }
}
