using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

public sealed partial class RespireClient
{
    // Uploads retain one deadline through acquisition, source reads, redirects and retirement.
    // DedicatedLeaseAcquisition owns safe pool replacement; the connection owns frame admission.
    // Upload orchestration is separate from blocking commands: blocking sends keep their
    // response-timeout exemption and read-role fallback. Both share the rental helper and return
    // or discard a lease through the pool that supplied it. Pre-header retries (MOVED/ASK,
    // retirement, maintenance relaxation) preserve prefetched bytes; completed redirect replies
    // reset only replayable sources. One telemetry scope follows each logical upload.
    private CommandDeadline CreateUploadDeadline()
        => _core.Options.CommandTimeout is { } timeout
            ? CommandDeadline.After(Math.Max(1L, (long)timeout.TotalMilliseconds))
            : CommandDeadline.None;

    private static CancellationToken ArmDedicatedAcquisition(
        DedicatedAcquisitionCancellation? source, CommandDeadline deadline, CancellationToken callerToken)
    {
        if (source is null) return callerToken;
        source.Arm(deadline);
        return source.Token;
    }

    private Exception? TranslateDedicatedAcquisitionCancellation(
        Exception error, DedicatedAcquisitionCancellation? source, CancellationToken callerToken, string operation,
        CommandDeadline deadline)
    {
        if (source is null || error is not OperationCanceledException cancelled
            || cancelled.CancellationToken != source.Token || !source.IsCancellationRequested) return null;
        // A source exists only for a deadline from CreateUploadDeadline, which requires
        // CommandTimeout. The client's options are immutable for this operation.
        return callerToken.IsCancellationRequested
            ? new OperationCanceledException(cancelled.Message, cancelled, callerToken)
            : new RespireTimeoutException(operation,
                deadline.IsRelaxed ? _core.Options.MaintenanceRelaxedTimeout : _core.Options.CommandTimeout!.Value,
                cancelled, RespireTimeoutDiagnostics.Capture(RespireCommandStage.Connecting));
    }

    /// <summary>Sends an upload on a dedicated lease without blocking multiplexed traffic.</summary>
    private ValueTask<RespValue> SendStreamedUploadAsync<TCommand>(
        string operation, TCommand command, CancellationToken cancellationToken, bool noRedirect,
        RespireTelemetry.ErrorObservation observation = default)
        where TCommand : struct, IRespCommand
    {
        var ownsObservation = observation.IsEmpty;
        if (observation.IsEmpty) observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        var response = SendStreamedUploadCoreAsync(operation, command, cancellationToken, noRedirect, observation);
        return ownsObservation ? RespireTelemetry.ObserveFinalError(response, observation) : response;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendStreamedUploadCoreAsync<TCommand>(
        string operation,
        TCommand command,
        CancellationToken cancellationToken,
        bool noRedirect, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeforeCommand(operation, in command);
        try
        {
            if (mutationFence.IsRequired && typeof(TCommand) == typeof(StreamedSetCommand))
            {
                var bound = Unsafe.As<TCommand, StreamedSetCommand>(ref command).WithMutationFence(mutationFence);
                command = Unsafe.As<StreamedSetCommand, TCommand>(ref bound);
            }
            var started = RespireTelemetry.CaptureOperationStart(operation);
            if (core.Cluster is { } cluster)
            {
                return await SendStreamedUploadClusterAsync(
                        operation, cluster, command, cancellationToken, noRedirect, started, observation)
                    .ConfigureAwait(false);
            }

            RespireTelemetry.OperationScope telemetry = default;
            var telemetryStarted = false;
            RespireConnection? connection = null;
            DedicatedConnectionPool? pool = null;
            var returned = false;
            var commandDeadline = CreateUploadDeadline();
            using var acquisitionCancellation = commandDeadline.IsSet
                ? new DedicatedAcquisitionCancellation(cancellationToken) : null;
            try
            {
                RespValue response;
                for (var attempt = 0; ; attempt++)
                {
                    var acquisitionToken = ArmDedicatedAcquisition(acquisitionCancellation, commandDeadline, cancellationToken);
                    pool = await core.GetDedicatedPoolAsync(acquisitionToken).ConfigureAwait(false);
                    (pool, connection) = await core.RentDedicatedConnectionAsync(pool, acquisitionToken,
                        kind: DedicatedLeaseKind.Streaming).ConfigureAwait(false);
                    acquisitionCancellation?.Disarm();
                    if (!telemetryStarted)
                    {
                        telemetry = RespireTelemetry.StartOperation(operation, connection.Host, connection.Port,
                            core.Options.Database, started: started);
                        telemetryStarted = true;
                    }
                    else
                    {
                        // Keep one logical span while routing advances to a replacement lease.
                        telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    }
                    try
                    {
                        var route = new DedicatedStreamRoute(core, pool, connection);
                        if (core.Circuits is not null)
                        {
                            // A handoff can invalidate the rented lease before admission. Retry
                            // before an open source circuit rejects the replacement's upload.
                            connection.ThrowIfRetired();
                            if (!route.IsCurrent()) throw new RespireConnectionRetiredException(connection.Host, connection.Port);
                        }
                        var admission = core.Circuits is not null ? AcquireCircuit(connection, cancellationToken) : default;
                        try
                        {
                            response = await connection.SendCheckedAsync(in command, cancellationToken, commandName: operation,
                                commandDeadline: commandDeadline, allowStreamingConnectionReroute: false,
                                streamingRoute: route,
                                observation: observation).ConfigureAwait(false);
                            admission.Success();
                        }
                        catch (Exception error)
                        {
                            admission.Failed(error, cancellationToken);
                            throw;
                        }
                        finally { admission.Dispose(); }
                        break;
                    }
                    catch (RespireConnectionRetiredException error) when (attempt < ClusterRouter.RedirectLimit
                        && !core.Disposed && !cancellationToken.IsCancellationRequested)
                    {
                        // The header was rejected before acceptance; the streaming command retains
                        // any prefetched bytes. Retry the same command under the original deadline.
                        commandDeadline = connection.GetReroutedCommandDeadline(commandDeadline);
                        pool.Return(connection);
                        connection = null;
                        observation.Handled(error);
                    }
                }
                pool.Return(connection);
                returned = true;
                if (response.IsError)
                {
                    var error = ResponseReader.ServerError(in response, operation);
                    response.Dispose();
                    throw error;
                }

                telemetry.Complete(core, operation, null, connection: connection);
                return response;
            }
            catch (Exception ex)
            {
                var timeoutError = TranslateDedicatedAcquisitionCancellation(
                    ex, acquisitionCancellation, cancellationToken, operation, commandDeadline);
                if (!telemetryStarted)
                    RespireTelemetry.RecordUnroutedFailure(operation, core.Options.Database,
                        started, timeoutError ?? ex, null,
                        endpoint: core.Sentinel is null ? pool?.Endpoint ?? core.Multiplexer.ActiveConnectionEndpoint : (RespireEndpoint?)null);
                telemetry.Complete(core, operation, null, timeoutError ?? ex, connection);
                if (connection is not null && !returned)
                {
                    if (ex is RespireCircuitOpenException) pool!.Return(connection);
                    else await pool!.DiscardAsync(connection).ConfigureAwait(false);
                }

                if (timeoutError is not null) throw timeoutError;
                throw;
            }
        }
        finally
        {
            if (mutationFence.IsRequired)
            {
                cache!.CompleteMutation(in mutationFence);
            }
        }
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<RespValue> SendStreamedUploadClusterAsync<TCommand>(
        string operation,
        ClusterRouter cluster,
        TCommand command,
        CancellationToken cancellationToken,
        bool noRedirect,
        RespireTelemetry.OperationStart started, RespireTelemetry.ErrorObservation observation)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        var slot = command.TryGetClusterSlot(out var commandSlot) ? commandSlot : (int?)null;
        var commandDeadline = CreateUploadDeadline();
        using var acquisitionCancellation = commandDeadline.IsSet
            ? new DedicatedAcquisitionCancellation(cancellationToken) : null;
        var acquisitionToken = ArmDedicatedAcquisition(acquisitionCancellation, commandDeadline, cancellationToken);
        DedicatedConnectionPool pool;
        ClusterRouter.StreamRouteVersion routeVersion;
        RespireTelemetry.OperationScope telemetry = default;
        var telemetryStarted = false;
        RespireConnection? telemetryConnection = null;
        UploadAskState asking = default;

        ClusterRouter.DiscoveryRound? discovery = null;
        try
        {
            (pool, routeVersion) = await cluster.GetDedicatedStreamPoolAsync(slot, acquisitionToken, discovery: null)
                .ConfigureAwait(false);
            for (var attempt = 0; ; attempt++)
            {
                RespireConnection? connection = null;
                var returned = false;
                var acquiringRedirectPool = false;
                try
                {
                    acquisitionToken = ArmDedicatedAcquisition(acquisitionCancellation, commandDeadline, cancellationToken);
                    (pool, connection) = await cluster.RentDedicatedConnectionAsync(
                        pool, new ClusterRouter.DedicatedRoute(slot, RespireReadFrom.Primary, asking.Redirect, asking.Source),
                        acquisitionToken, discovery,
                        kind: DedicatedLeaseKind.Streaming).ConfigureAwait(false);
                    acquisitionCancellation?.Disarm();
                    telemetryConnection = connection;
                    if (!telemetryStarted)
                    {
                        telemetry = RespireTelemetry.StartOperation(
                            operation,
                            connection.Host,
                            connection.Port,
                            core.Options.Database, started: started);
                        telemetryStarted = true;
                    }
                    else
                    {
                        // Keep one logical span while routing advances to a replacement lease.
                        telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    }

                    RespValue response = default;
                    RespireServerException? serverError = null;
                    CircuitAdmission admission = default;
                    try
                    {
                        // ASK validates the target pool against the captured slot generation.
                        var route = new DedicatedStreamRoute(cluster, pool, connection, slot, routeVersion, asking.IsActive);
                        if (core.Circuits is not null)
                        {
                            connection.ThrowIfRetired();
                            if (!route.IsCurrent()) throw new RespireConnectionRetiredException(connection.Host, connection.Port);
                            admission = AcquireCircuit(connection, cancellationToken);
                        }
                        response = await (asking.IsActive
                            ? ClusterRouter.SendAskingAsync(connection, in command, cancellationToken,
                                operation, commandDeadline, allowStreamingConnectionReroute: false,
                                streamingRoute: route, observation: observation)
                            : connection.SendCheckedAsync(in command, cancellationToken, commandName: operation,
                                commandDeadline: commandDeadline, allowStreamingConnectionReroute: false,
                                streamingRoute: route, observation: observation))
                            .ConfigureAwait(false);
                        admission.Success();
                    }
                    catch (RespireServerException error) { admission.Failed(error, cancellationToken); serverError = error; }
                    catch (Exception error) { admission.Failed(error, cancellationToken); throw; }
                    finally { admission.Dispose(); }
                    asking = default;
                    if (serverError is not null || response.IsError)
                    {
                        var error = serverError ?? ResponseReader.ServerError(in response, operation);
                        if (serverError is null) response.Dispose();
                        if (!noRedirect && attempt < ClusterRouter.RedirectLimit
                            && ClusterRouter.CanRecover(error, slot))
                        {
                            core.ClientCache?.FlushForContinuityLoss();
                            // The source reply completed; no redirected command has been accepted yet.
                            cluster.RecordRejection(ref discovery, connection, error);
                            observation.Handled(error);
                            acquiringRedirectPool = true;
                            commandDeadline = connection.GetReroutedCommandDeadline(commandDeadline);
                            acquisitionToken = ArmDedicatedAcquisition(acquisitionCancellation, commandDeadline, cancellationToken);
                            var redirect = await AcquireUploadRedirectAsync(
                                    cluster, error, connection, slot, discovery, acquisitionToken)
                                .ConfigureAwait(false);
                            acquiringRedirectPool = false;
                            pool.Return(connection);
                            returned = true;
                            (pool, routeVersion, asking) = redirect;
                            // Release the source lease before invoking user-controlled replay reset.
                            ResetUploadSourceForReplay(in command, error);
                            continue;
                        }

                        pool.Return(connection);
                        returned = true;
                        // NoRedirect callers handle redirects themselves and must see the ASK reply.
                        throw error;
                    }

                    pool.Return(connection);
                    returned = true;
                    telemetry.Complete(core, operation, null, connection: connection);
                    return response;
                }
                catch (RespireConnectionRetiredException error)
                    when (cluster.CanRetryRetirement(attempt, cancellationToken))
                {
                    core.ClientCache?.FlushForContinuityLoss();
                    if (connection is not null)
                    {
                        commandDeadline = connection.GetReroutedCommandDeadline(commandDeadline);
                        cluster.RecordRejection(ref discovery, connection, error);
                        if (!returned) pool.Return(connection);
                    }
                    observation.Handled(error);
                    acquisitionToken = ArmDedicatedAcquisition(acquisitionCancellation, commandDeadline, cancellationToken);
                    if (asking.IsActive && cluster.CaptureSlotVersion(slot) == routeVersion)
                        pool = await cluster.GetRedirectDedicatedPoolAsync(
                            asking.Redirect!, asking.Source!, acquisitionToken, slot, discovery).ConfigureAwait(false);
                    else
                    {
                        asking = default;
                        (pool, routeVersion) = await cluster.GetDedicatedStreamPoolAsync(
                            slot, acquisitionToken, discovery).ConfigureAwait(false);
                    }
                    continue;
                }
                catch (Exception ex)
                {
                    var timeoutError = TranslateDedicatedAcquisitionCancellation(
                        ex, acquisitionCancellation, cancellationToken, operation, commandDeadline);
                    discovery?.RecordCommandFailure(timeoutError ?? ex,
                        acquiringRedirectPool || connection is null, slot, noRedirect);
                    if (connection is not null && !returned)
                    {
                        if (ex is RespireCircuitOpenException) pool.Return(connection);
                        else await pool.DiscardAsync(connection).ConfigureAwait(false);
                    }

                    if (timeoutError is not null) throw timeoutError;
                    throw;
                }
            }
        }
        // Initial discovery and acquisition after retirement also belong to the selected operation.
        catch (Exception error)
        {
            var timeoutError = TranslateDedicatedAcquisitionCancellation(
                error, acquisitionCancellation, cancellationToken, operation, commandDeadline);
            if (!telemetryStarted)
                RespireTelemetry.RecordUnroutedFailure(operation, core.Options.Database, started, timeoutError ?? error);
            else
                telemetry.Complete(core, operation, null, timeoutError ?? error, telemetryConnection);
            if (timeoutError is not null) throw timeoutError;
            throw;
        }
        finally { discovery?.Finish(); }
    }

    private static async ValueTask<UploadRedirect>
        AcquireUploadRedirectAsync(ClusterRouter cluster, RespireServerException error, RespireConnection source,
            int? slot, ClusterRouter.DiscoveryRound? discovery, CancellationToken cancellationToken)
    {
        var asking = error.Code == RespireErrorCodes.Ask;
        // ASK does not publish an owner; a concurrent topology change must invalidate it.
        var version = asking ? cluster.CaptureSlotVersion(slot) : default;
        var pool = await cluster.GetRedirectDedicatedPoolAsync(error, source, cancellationToken, slot, discovery)
            .ConfigureAwait(false);
        // MOVED and READONLY recovery can publish an owner during acquisition.
        if (!asking) version = cluster.CaptureSlotVersion(slot);
        return new(pool, version, asking ? new UploadAskState(source, error) : default);
    }

    private static void ResetUploadSourceForReplay<TCommand>(in TCommand command, RespireServerException error)
        where TCommand : struct, IRespCommand
    {
        if (command is not IReplayableStreamingRespCommand replayable) return;
        try { replayable.ResetSourceForReplay(); }
        catch (Exception resetError) when (resetError is not OutOfMemoryException
            and not AccessViolationException and not StackOverflowException)
        {
            RethrowPreservingStackTrace(error);
        }
    }

    private readonly record struct UploadRedirect(
        DedicatedConnectionPool Pool, ClusterRouter.StreamRouteVersion Version, UploadAskState Asking);

    // ASK identity must be set and cleared together. Slot version and discovery also serve
    // ordinary routes, so they remain independent of this optional redirect state.
    private readonly record struct UploadAskState(RespireConnection? Source, RespireServerException? Redirect)
    {
        internal bool IsActive => Redirect is not null;
    }
}
