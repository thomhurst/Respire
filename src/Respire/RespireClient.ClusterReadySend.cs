using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private bool TryGetDirectReplyCluster<TCommand>(in TCommand command,
        [NotNullWhen(true)] out ClusterRouter? cluster) where TCommand : struct, IRespCommand
    {
        cluster = _core.Cluster;
        // Cache fences and server-local cursors keep the normal route. StringOrNullAsync
        // lacks an outer streaming exclusion, so this shared cluster guard must retain it.
        return cluster is not null && _core.ClientCache is null
            && command.ReadKind != ReadCommandKind.CursorRead && command is not IStreamingRespCommand;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> SendOnReadyClusterAsync<TCommand, TResult, TSend>(
        string operation, ClusterRouter cluster, TCommand command, CancellationToken cancellationToken, TSend sender)
        where TCommand : struct, IRespCommand
        where TSend : struct, IClusterReadySend<TResult>
    {
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var connection = cluster.TryAcquireReadyConnection(slot, cancellationToken);
        RespValue response;
        if (connection is null)
        {
            // Discovery and connection initialization retain the normal routing path.
            response = await SendAsync(operation, command, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                return await sender.Send(connection, operation, in command, cancellationToken).ConfigureAwait(false);
            }
            catch (ClusterConverterException error)
            {
                // A caller converter can throw a Redis-shaped exception after success.
                // It must never be mistaken for a rejected command and replayed.
                ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
                throw;
            }
            catch (RespireConnectionRetiredException error) when (cluster.CanRetryRetirement(0, cancellationToken))
            {
                response = await ResumeRetiredClusterSendAsync(operation, command, connection, error,
                    RespireReadFrom.Primary, cancellationToken).ConfigureAwait(false);
            }
            catch (RespireServerException error) when (ClusterRouter.CanRecover(error, slot))
            {
                response = await ResumeRejectedClusterSendAsync(operation, command, connection, error,
                    RespireReadFrom.Primary, cancellationToken).ConfigureAwait(false);
            }
        }

        var transferred = false;
        try
        {
            var result = sender.Convert(in response);
            transferred = sender.TransferOwnership;
            return result;
        }
        finally
        {
            if (!transferred) response.Dispose();
        }
    }

    private interface IClusterReadySend<TResult> : IReadySend<TResult>
    {
        TResult Convert(in RespValue response);
        bool TransferOwnership { get; }
    }

    // Only this variant invokes caller code; built-in string/bytes senders need no converter exception wrapper.
    private readonly struct ClusterConvertedReadySend<TState, TResult>(
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership) : IClusterReadySend<TResult>
    {
        public bool TransferOwnership => transferOwnership;
        public TResult Convert(in RespValue response) => converter(state, in response);

        public ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, this,
                static (ClusterConvertedReadySend<TState, TResult> sender, in RespValue response) =>
                {
                    try { return sender.Convert(in response); }
                    catch (Exception error) { throw new ClusterConverterException(error); }
                }, transferOwnership, cancellationToken, operation);
    }

    private sealed class ClusterConverterException(Exception error)
        : Exception("The command succeeded, but its result converter failed.", error);
}
