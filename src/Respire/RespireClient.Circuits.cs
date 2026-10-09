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
    private bool UsesCircuitResponsePath(string operation, bool pinToConnection)
        => _core.Circuits is not null && !_snapshotPrefixedBinaryKeys && !pinToConnection
            && operation != "MULTI";

    private CommandDeadline CreateCircuitDeadline(CommandDeadline deadline = default)
        => deadline.IsSet || _core.Options.CommandTimeout is not { } timeout
            ? deadline : CommandDeadline.After(Math.Max(1L, (long)timeout.TotalMilliseconds));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private RespireConnection GetCircuitAwareConnection(RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken)
        => _core.Circuits is null || _snapshotPrefixedBinaryKeys
            ? multiplexer.GetConnection()
            : GetCircuitConnectionSlow(multiplexer, cancellationToken);

    private RespireConnection GetCircuitConnectionSlow(RespireConnectionMultiplexer multiplexer, CancellationToken cancellationToken)
    {
        while (true)
        {
            var routing = multiplexer.CaptureMovingPublication();
            try { return multiplexer.GetConnection(); }
            catch (RespireConnectionException error)
            {
                // A handoff can publish after selection observes the dead source. Retry that
                // stale selection before rejecting or recording availability for either endpoint.
                if (!ReferenceEquals(routing.Publication, multiplexer.MovingPublication))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    continue;
                }
                // Selection can fail before reaching the dispatch guard. Prefer circuit rejection
                // when the selected endpoint is open; otherwise record its lost availability.
                var admission = _core.Circuits!.Acquire(routing.Endpoint, cancellationToken);
                try { admission.Failed(error, cancellationToken); }
                finally { admission.Dispose(); }
                throw;
            }
        }
    }

    private ValueTask<RespValue> SendBlockingOnConnectionAsync<TCommand>(
        RespireConnection connection, TCommand command, CancellationToken cancellationToken, int errorAttempts = 0)
        where TCommand : struct, IRespCommand
        => _core.Circuits is not null
            ? SendCircuitBlockingAsync(connection, command, cancellationToken, errorAttempts)
            : connection.SendWithoutResponseTimeoutAsync(command, cancellationToken, errorAttempts);

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendCircuitBlockingAsync<TCommand>(
        RespireConnection connection, TCommand command, CancellationToken cancellationToken, int errorAttempts)
        where TCommand : struct, IRespCommand
    {
        var admission = AcquireCircuit(connection, cancellationToken);
        try
        {
            var response = await connection.SendWithoutResponseTimeoutAsync(command, cancellationToken, errorAttempts).ConfigureAwait(false);
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
        bool sendAsking, CommandDeadline commandDeadline, bool allowStreamingConnectionReroute,
        RespireTelemetry.ErrorObservation observation, RespireTelemetry.OperationScope telemetry = default,
        string? storedProcedureName = null)
        where TCommand : struct, IRespCommand
    {
        commandDeadline = CreateCircuitDeadline(commandDeadline);
        RespValue response;
        try
        {
            while (true)
            {
                CircuitAdmission admission = default;
                try
                {
                    // Check retirement before admission so an old open endpoint cannot reject
                    // a command that will be sent on its healthy maintenance replacement.
                    connection.ThrowIfRetired();
                    admission = AcquireCircuit(connection, cancellationToken);
                    response = await SendOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                        sendAsking, commandDeadline, allowStreamingConnectionReroute, pinToConnection: true,
                        observation: observation).ConfigureAwait(false);
                    admission.Success();
                    break;
                }
                catch (RespireConnectionRetiredException error) when (allowStreamingConnectionReroute
                    && connection.TryReroute(false, commandDeadline, out var target, out var rerouted, GetTransportReadZone(in command)))
                {
                    observation.Handled(error);
                    connection = target;
                    commandDeadline = rerouted;
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                }
                catch (Exception error)
                {
                    admission.Failed(error, cancellationToken);
                    throw;
                }
                finally { admission.Dispose(); }
            }
        }
        catch (Exception error)
        {
            telemetry.Complete(_core, operation, storedProcedureName, error, connection);
            throw;
        }
        // Listener callbacks run only after the final admission records its outcome.
        // A callback failure must not rewrite a successful endpoint response.
        telemetry.Complete(_core, operation, storedProcedureName, connection: connection);
        return response;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<TResult> SendCircuitReadyAsync<TCommand, TResult, TSend>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        TSend sender, RespireTelemetry.OperationStart durationStarted, ClientSideCacheCoordinator? cache,
        ClientSideCacheCoordinator.MutationFence mutationFence, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
        where TSend : struct, IReadySend<TResult>
    {
        var commandDeadline = CreateCircuitDeadline();
        var attemptStarted = durationStarted with { SuppressRetirement = true };
        var durationOwnedByTransport = false;
        var succeeded = false;
        Exception? finalError = null;
        // Circuit rejection must release the logical command's mutation fence too.
        try
        {
            while (true)
            {
                durationOwnedByTransport = false;
                CircuitAdmission admission = default;
                try
                {
                    connection.ThrowIfRetired();
                    admission = AcquireCircuit(connection, cancellationToken);
                    durationOwnedByTransport = true;
                    var response = mutationFence.IsRequired
                        ? await sender.Send(connection, operation, new MutationCommand<TCommand>(command, mutationFence),
                            cancellationToken, attemptStarted, commandDeadline, pinToConnection: true,
                            errorObservation: observation).ConfigureAwait(false)
                        : await sender.Send(connection, operation, command, cancellationToken, attemptStarted,
                            commandDeadline, pinToConnection: true, errorObservation: observation).ConfigureAwait(false);
                    succeeded = true;
                    admission.Success();
                    return response;
                }
                catch (RespireConnectionRetiredException error) when (connection.TryReroute(false, commandDeadline,
                    out var target, out var rerouted, GetTransportReadZone(in command)))
                {
                    observation.Handled(error);
                    connection = target;
                    commandDeadline = rerouted;
                }
                catch (Exception error)
                {
                    admission.Failed(error, cancellationToken);
                    throw;
                }
                finally { admission.Dispose(); }
            }
        }
        catch (Exception error)
        {
            finalError = error;
            // Final response sources own completion before user conversion. Undispatched
            // retirement retains the original start for retry or terminal failure here.
            if (!durationOwnedByTransport || error is RespireConnectionRetiredException)
            {
                var duration = new RespireTelemetry.DurationObservation(connection, durationStarted);
                duration.Complete(operation, error);
            }
            throw;
        }
        finally
        {
            try
            {
                if (mutationFence.IsRequired) cache!.CompleteMutation(in mutationFence, succeeded);
            }
            catch (Exception) when (finalError is not null)
            {
                // Cleanup cannot replace the response failure or turn cancellation into a fault.
            }
            catch (Exception error)
            {
                finalError = error;
                throw;
            }
            finally
            {
                if (finalError is not null) observation.Final(finalError);
            }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespValue> SendCircuitTrackedAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        bool sendAsking, bool track, RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        var commandDeadline = CreateCircuitDeadline();
        while (true)
        {
            CircuitAdmission admission = default;
            try
            {
                connection.ThrowIfRetired();
                admission = AcquireCircuit(connection, cancellationToken);
                var response = await SendTrackedOnConnectionUncheckedAsync(operation, connection, command,
                    cancellationToken, sendAsking, track, commandDeadline, pinToConnection: true,
                    observation: observation).ConfigureAwait(false);
                admission.Success();
                return response;
            }
            catch (RespireConnectionRetiredException error) when (connection.TryReroute(false, commandDeadline,
                out var target, out var rerouted, GetTransportReadZone(in command)))
            {
                observation.Handled(error);
                connection = target;
                commandDeadline = rerouted;
            }
            catch (Exception error)
            {
                admission.Failed(error, cancellationToken);
                throw;
            }
            finally { admission.Dispose(); }
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask SendCircuitFireAndForgetAsync<TCommand>(
        string operation, RespireConnection connection, TCommand command, CancellationToken cancellationToken,
        string? storedProcedureName, RespireTelemetry.ErrorObservation observation) where TCommand : struct, IRespCommand
    {
        var commandDeadline = CreateCircuitDeadline();
        while (true)
        {
            CircuitAdmission admission = default;
            try
            {
                connection.ThrowIfRetired();
                admission = AcquireCircuit(connection, cancellationToken);
                await SendFireAndForgetOnConnectionUncheckedAsync(operation, connection, command, cancellationToken,
                    storedProcedureName, commandDeadline, pinToConnection: true,
                    observation: observation).ConfigureAwait(false);
                // Queue acceptance cannot establish endpoint health without observing its reply.
                return;
            }
            catch (RespireConnectionRetiredException error) when (connection.TryReroute(false, commandDeadline,
                out var target, out var rerouted, GetTransportReadZone(in command)))
            {
                observation.Handled(error);
                connection = target;
                commandDeadline = rerouted;
            }
            catch (Exception error)
            {
                admission.Failed(error, cancellationToken);
                throw;
            }
            finally { admission.Dispose(); }
        }
    }
}
