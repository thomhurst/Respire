using System.Runtime.CompilerServices;
using Respire.Internal;
using Respire.Protocol;

namespace Respire.Networking;

internal sealed partial class RespireConnection
{
    internal ValueTask<byte[]?> SendBytesAsync<TCommand>(
        in TCommand command, CancellationToken cancellationToken = default,
        string? commandName = null, CommandDeadline commandDeadline = default)
        where TCommand : struct, IRespCommand
    {
        if (!commandDeadline.IsSet) commandDeadline = CommandDeadline.After(_commandTimeoutMilliseconds);
        var source = BytesPendingResponseSource.Rent(commandName);
        bool enqueued;
        bool startedBatch;
        try
        {
            enqueued = TryEnqueue(in command, source, out startedBatch);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted, preferredZone: null))
        {
            ReclaimUnpublished(source);
            return target.SendBytesAsync(in command, cancellationToken, commandName, rerouted);
        }
        catch
        {
            ReclaimUnpublished(source);
            throw;
        }
        if (enqueued)
        {
            ClampDeadline(source, commandDeadline);
            source.RegisterCancellation(cancellationToken);
            ScheduleFlush(startedBatch);
            return source.Task;
        }
        return SendBytesSlowAsync(command, source, cancellationToken, commandName, commandDeadline);
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<byte[]?> SendBytesSlowAsync<TCommand>(
        TCommand command, BytesPendingResponseSource source, CancellationToken cancellationToken,
        string? commandName, CommandDeadline commandDeadline)
        where TCommand : struct, IRespCommand
    {
        bool startedBatch;
        try
        {
            startedBatch = await WaitForInflightCapacityAsync(
                command, source, 0, cancellationToken, commandDeadline: commandDeadline).ConfigureAwait(false);
        }
        catch (RespireConnectionRetiredException) when (TryReroute(pinToConnection: false, commandDeadline, out var target, out var rerouted, preferredZone: null))
        {
            return await target.SendBytesAsync(in command, cancellationToken, commandName, rerouted).ConfigureAwait(false);
        }
        source.RegisterCancellation(cancellationToken);
        ScheduleFlush(startedBatch);
        return await source.Task.ConfigureAwait(false);
    }
}
