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
        => !RespireTelemetry.IsOperationEnabled(operation)
            && (_readFrom == RespireReadFrom.Primary || command.ReadKind == ReadCommandKind.None)
            && (ReadCache is null || !command.GetClientCacheMetadata(operation).CacheableRead);

    private ValueTask<RespValue> SendOnReadyPrimaryAsync<TCommand>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken) where TCommand : struct, IRespCommand
        // Raw sends keep their mutation fence in the outer SendAsync path.
        => SendOnReadyPrimaryAsync<TCommand, RespValue, RawReadySend>(
            operation, multiplexer, command, cancellationToken, new RawReadySend(this));

    private ValueTask<TResult> SendOnReadyPrimaryAsync<TCommand, TResult, TSend>(
        string operation, RespireConnectionMultiplexer multiplexer, in TCommand command,
        CancellationToken cancellationToken, TSend sender, ClientSideCacheCoordinator? cache = null,
        ClientSideCacheCoordinator.MutationFence mutationFence = default)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        try
        {
            var response = sender.Send(multiplexer.GetConnection(), operation, in command, cancellationToken);
            return mutationFence.IsRequired ? CompleteMutationAsync(response, cache!, mutationFence) : response;
        }
        // _core is readonly: this filter observes the same core captured by the caller.
        catch (Exception error) when (_core.Sentinel is not null)
        {
            return CaptureReadySendFailure<TResult>(error, cache, mutationFence);
        }
    }

    // Constrained value-type dispatch keeps each specialized response source without a delegate or boxing.
    private interface IReadySend<TResult>
    {
        ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand;
    }

    private readonly struct RawReadySend(RespireClient client) : IReadySend<RespValue>
    {
        public ValueTask<RespValue> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
            => client.SendOnConnectionAsync(operation, connection, command, cancellationToken);
    }

    private readonly struct StringReadySend : IClusterReadySend<string?>
    {
        public bool TransferOwnership => false;
        public string? Convert(in RespValue response) => ResponseReader.StringOrNull(in response);

        public ValueTask<string?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
            => connection.SendStringAsync(in command, cancellationToken, operation);
    }

    private readonly struct BytesReadySend : IClusterReadySend<byte[]?>
    {
        public bool TransferOwnership => false;
        public byte[]? Convert(in RespValue response) => ResponseReader.BytesOrNull(in response);

        public ValueTask<byte[]?> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
            => connection.SendBytesAsync(in command, cancellationToken, operation);
    }

    private readonly struct ConvertedReadySend<TState, TResult>(
        TState state, ResponseConverter<TState, TResult> converter, bool transferOwnership) : IReadySend<TResult>
    {
        public ValueTask<TResult> Send<TCommand>(RespireConnection connection, string operation,
            in TCommand command, CancellationToken cancellationToken) where TCommand : struct, IRespCommand
            => connection.SendConvertedAsync(in command, state, converter, transferOwnership, cancellationToken, operation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ValueTask<TResult> CaptureReadySendFailure<TResult>(Exception error,
        ClientSideCacheCoordinator? cache = null, ClientSideCacheCoordinator.MutationFence mutationFence = default)
    {
        // Readiness is an observation, not a lease: socket loss, retirement, or disposal
        // can make GetConnection or admission throw after validation. A command writer
        // can also throw before admission. Preserve the former async acquisition result.
        if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
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
