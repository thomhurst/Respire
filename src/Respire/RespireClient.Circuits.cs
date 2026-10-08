using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Infrastructure;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    private CircuitAdmission AcquireCircuit(RespireConnection connection, CancellationToken cancellationToken)
        => _core.Circuits!.Acquire(new(connection.Host, connection.Port), cancellationToken);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RespireConnection GetCircuitAwareConnection(RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken)
        => _core.Circuits is null || _snapshotPrefixedBinaryKeys
            ? multiplexer.GetConnection()
            : GetCircuitConnectionSlow(multiplexer, cancellationToken);

    private RespireConnection GetCircuitConnectionSlow(RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken)
    {
        try { return multiplexer.GetConnection(); }
        catch (RespireConnectionException)
        {
            // Selection can fail before reaching the dispatch guard. Prefer circuit rejection
            // when this endpoint is open; otherwise release the undispatched admission.
            using var admission = _core.Circuits!.Acquire(multiplexer.ActiveConnectionEndpoint, cancellationToken);
            throw;
        }
    }

    private ValueTask<RespValue> SendBlockingOnConnectionAsync<TCommand>(
        RespireConnection connection, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
        => _core.Circuits is not null
            ? SendCircuitBlockingAsync(connection, command, cancellationToken)
            : connection.SendWithoutResponseTimeoutAsync(command, cancellationToken);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendCircuitBlockingAsync<TCommand>(
        RespireConnection connection, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var admission = AcquireCircuit(connection, cancellationToken);
        try
        {
            var response = await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken).ConfigureAwait(false);
            admission.Success();
            return response;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendCircuitResponseAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool sendAsking, CommandDeadline commandDeadline, bool allowStreamingConnectionReroute)
        where TCommand : struct, IRespCommand
    {
        var admission = AcquireCircuit(connection, cancellationToken);
        try
        {
            var response = await SendOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                sendAsking, commandDeadline, allowStreamingConnectionReroute).ConfigureAwait(false);
            admission.Success();
            return response;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> SendCircuitReadyAsync<TCommand, TResult, TSend>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        TSend sender, RespireTelemetry.OperationStart durationStarted, ClientSideCacheCoordinator? cache,
        ClientSideCacheCoordinator.MutationFence mutationFence)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        // Acquire inside the fence's try/finally: rejection must release cache admission too.
        CircuitAdmission admission = default;
        try
        {
            admission = AcquireCircuit(connection, cancellationToken);
            var response = mutationFence.IsRequired
                ? await sender.Send(connection, operation, new MutationCommand<TCommand>(command, mutationFence),
                    cancellationToken, durationStarted).ConfigureAwait(false)
                : await sender.Send(connection, operation, command, cancellationToken, durationStarted).ConfigureAwait(false);
            admission.Success();
            return response;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally
        {
            admission.Dispose();
            if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence);
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendCircuitTrackedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool sendAsking, bool track) where TCommand : struct, IRespCommand
    {
        var admission = AcquireCircuit(connection, cancellationToken);
        try
        {
            var response = await SendTrackedOnConnectionUncheckedAsync(operation, connection, command,
                cancellationToken, sendAsking, track).ConfigureAwait(false);
            admission.Success();
            return response;
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendCircuitFireAndForgetAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        string? storedProcedureName) where TCommand : struct, IRespCommand
    {
        var admission = AcquireCircuit(connection, cancellationToken);
        try
        {
            await SendFireAndForgetOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                storedProcedureName).ConfigureAwait(false);
            // Queue acceptance cannot establish endpoint health without observing its reply.
        }
        catch (Exception error)
        {
            admission.Failed(error, cancellationToken);
            throw;
        }
        finally { admission.Dispose(); }
    }
}
