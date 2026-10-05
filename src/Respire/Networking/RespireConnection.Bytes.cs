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

    private bool TryCompleteBytesDirect(
        ReadOnlySpan<byte> buffer, RespDataType type, long payloadLength, int headerEnd, out int frameEnd)
    {
        frameEnd = 0;
        if (type != RespDataType.BulkString || !_inflight.TryPeek(out var head)
            || head is not BytesPendingResponseSource source) return false;
        byte[]? result;
        if (payloadLength == -1)
        {
            result = null;
            frameEnd = headerEnd;
        }
        else
        {
            if (payloadLength < 0 || payloadLength > int.MaxValue - 2) return false;
            var length = (int)payloadLength;
            if (buffer.Length - headerEnd < length + 2
                || buffer[headerEnd + length] != RespConstants.CarriageReturn
                || buffer[headerEnd + length + 1] != RespConstants.LineFeed) return false;
            result = buffer.Slice(headerEnd, length).ToArray();
            frameEnd = headerEnd + length + 2;
        }
        _inflight.TryDequeue(out _);
        MarkReplyReceived();
        source.SetDirectResult(result);
        _completions.Add(source, default);
        return true;
    }
}
