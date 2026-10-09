using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>Counts returned by WAITAOF, including when the requested acknowledgement level was not reached.</summary>
/// <param name="Local">Whether the local server has fsynced the preceding writes: 0 or 1.</param>
/// <param name="Replicas">The number of replicas that have fsynced the preceding writes.</param>
public readonly record struct RespireAofAcknowledgement(long Local, long Replicas);

public sealed partial class RespireBatch
{
    private static readonly Verb WaitVerb = new(-1, "WAIT");
    private static readonly Verb WaitAofVerb = new(-1, "WAITAOF");

    /// <summary>Executes this batch and then WAIT on the same dedicated connection. Redis 3.0+.</summary>
    /// <remarks>
    /// Returns the actual replica count, which may be below <paramref name="replicas"/> when the server timeout expires.
    /// Zero timeout waits indefinitely; positive durations round up to milliseconds. Cancellation applies to acquisition,
    /// writes, and the acknowledgement. Writes retain CommandTimeout; WAIT itself uses the server timeout and cancellation.
    /// No writes are replayed after a disconnect or Cluster redirect. A failed write prevents WAIT, but other writes may
    /// already have executed. Acknowledgement failure does not undo writes or invalidate successful pending results.
    /// Each execution creates and closes a fresh connection so no earlier borrower's replication offset is inherited.
    /// This is a pipeline, not a transaction, and WAIT does not guarantee strong consistency or lossless failover.
    /// </remarks>
    public ValueTask<long> ExecuteAndWaitForReplicationAsync(
        int replicas, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);
        var command = new Cmd2(WaitVerb, replicas, DurabilityTimeoutMilliseconds(timeout));
        return ExecuteWithAcknowledgementAsync("WAIT", command, static value =>
        {
            var count = ResponseReader.Integer(in value);
            if (count < 0) throw new RespireProtocolException("WAIT returned a negative replica count.");
            return count;
        }, cancellationToken);
    }

    /// <summary>Executes this batch and then WAITAOF on the same dedicated connection. Redis 7.2+.</summary>
    /// <remarks>
    /// Compare both returned counts with the requested levels. A true <paramref name="requireLocal"/> requires local AOF
    /// to be enabled. Timeout, cancellation, partial failures, and no-replay semantics match ExecuteAndWaitForReplicationAsync.
    /// The acknowledgement runs after all write replies, outside MULTI/EXEC. Unsupported servers can reject WAITAOF after
    /// the batch's writes have already completed. Scripts that suppress replication propagation are not supported by WAITAOF.
    /// </remarks>
    public ValueTask<RespireAofAcknowledgement> ExecuteAndWaitForAofAsync(
        bool requireLocal, int replicas, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(replicas);
        var command = new Cmd3(WaitAofVerb, requireLocal ? 1 : 0, replicas, DurabilityTimeoutMilliseconds(timeout));
        return ExecuteWithAcknowledgementAsync("WAITAOF", command, static value =>
        {
            var counts = value.AsArray();
            if (counts.Length != 2) throw new RespireProtocolException("WAITAOF must return two acknowledgement counts.");
            var local = ResponseReader.Integer(in counts[0]);
            var replicaCount = ResponseReader.Integer(in counts[1]);
            if (local is < 0 or > 1 || replicaCount < 0)
                throw new RespireProtocolException("WAITAOF returned invalid acknowledgement counts.");
            return new RespireAofAcknowledgement(local, replicaCount);
        }, cancellationToken);
    }

    private static long DurabilityTimeoutMilliseconds(TimeSpan timeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        // Do not turn a finite sub-millisecond timeout into Redis's infinite wait (zero).
        return timeout.Ticks / TimeSpan.TicksPerMillisecond
            + (timeout.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1);
    }

    private async ValueTask<T> ExecuteWithAcknowledgementAsync<TCommand, T>(
        string operation, TCommand acknowledgement, Func<RespValue, T> convert, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var slot = ValidateDurabilityAdmission(cancellationToken);
        var core = _client.Core;

        _sent = true;
        var telemetryOperation = operation;
        var started = RespireTelemetry.CaptureBatchStart(operation, _ops, static op => op.Operation);
        RespireTelemetry.OperationScope telemetry = default;
        DedicatedConnectionPool? pool = null;
        RespireConnection? connection = null;
        Exception? operationError = null;
        var cache = core.ClientCache;
        var mutationFence = cache is null ? default : cache.BeginUnknownMutation();
        try
        {
            foreach (var op in _ops) op.MutationFence = mutationFence;
            pool = core.Cluster is { } cluster
                ? await cluster.GetDedicatedPoolAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false)
                : await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false);
            // WAIT uses connection-local replication history, including when this batch only reads.
            if (core.Cluster is { } router)
                (pool, connection) = await router.RentDedicatedConnectionAsync(
                    pool, slot, cancellationToken, discovery: null, reuseIdle: false).ConfigureAwait(false);
            else
                (pool, connection) = await core.RentDedicatedConnectionAsync(pool, cancellationToken, reuseIdle: false).ConfigureAwait(false);
            telemetry = RespireTelemetry.StartBatchOperation(
                operation, _ops, static op => op.Operation,
                connection.Host, connection.Port, core.Options.Database, out telemetryOperation, started);
            cancellationToken.ThrowIfCancellationRequested();
            await RunStandaloneBatchAsync(connection, cancellationToken).ConfigureAwait(false);
            new RespireBatchResult(_ops.Count, CollectFailures(_ops, reportErrors: false)).ThrowIfAnyFailed();
            cancellationToken.ThrowIfCancellationRequested();

            var admittedAcknowledgement = new MutationCommand<TCommand>(acknowledgement, mutationFence);
            using var response = core.Circuits is { } circuits
                ? await QueuedCircuitDispatch.SendAsync(circuits, connection, admittedAcknowledgement, operation,
                    cancellationToken, default, withoutResponseTimeout: true).ConfigureAwait(false)
                : await connection.SendWithoutResponseTimeoutAsync(admittedAcknowledgement, cancellationToken).ConfigureAwait(false);
            if (response.IsError) throw ResponseReader.ServerError(in response, operation);
            return convert(response);
        }
        catch (Exception error)
        {
            operationError = error;
            foreach (var op in _ops)
                if (!op.IsCompleted) op.Fail(error);
            throw;
        }
        finally
        {
            try
            {
                if (connection is not null)
                {
                    // Never lend this execution's replication offset to another borrower.
                    await pool!.DiscardAsync(connection).ConfigureAwait(false);
                }
            }
            catch (Exception) when (operationError is not null)
            {
                // The pool reports cleanup failures. Preserve the original operation exception.
            }
            catch (Exception error)
            {
                operationError = error;
                throw;
            }
            finally
            {
                try
                {
                    if (operationError is not null)
                    {
                        var pendingErrors = false;
                        foreach (var op in _ops) pendingErrors |= op.ReportError();
                        if (!pendingErrors) RespireTelemetry.RecordError(operationError, internallyHandled: false);
                    }
                    if (connection is null && operationError is not null)
                        RespireTelemetry.RecordUnroutedBatchFailure(operation, _ops, static op => op.Operation,
                            core.Options.Database, started, operationError,
                            endpoint: pool?.Endpoint ?? (core.Cluster is null && core.Sentinel is null
                                ? core.Multiplexer.ActiveConnectionEndpoint : (RespireEndpoint?)null));
                    telemetry.Complete(core, telemetryOperation, error: operationError, connection: connection,
                        batchSize: _ops.Count == 1 ? null : _ops.Count);
                }
                finally { cache?.CompleteMutation(in mutationFence); }
            }
        }
    }

    private int? ValidateDurabilityAdmission(CancellationToken cancellationToken)
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_client.Core.Disposed, _client);
            if (_sent) throw new InvalidOperationException("This batch has already been sent.");
            ConnectionPolicy.ValidateDurabilityExecution();
            if (_ops.Count == 0) throw new InvalidOperationException("A durability acknowledgement requires a nonempty batch.");
            cancellationToken.ThrowIfCancellationRequested();
            int? slot = null;
            if (_client.Core.Cluster is not null)
            {
                // Deferred operations already contain keys resolved through the client's prefix view.
                foreach (var op in _ops)
                {
                    if (!op.TryGetClusterSlot(out var current))
                        throw new NotSupportedException("Cluster durability batches require a routing key for every command.");
                    if (slot is { } previous && previous != current)
                        throw new NotSupportedException("Cluster durability batches require every command to use the same hash slot.");
                    slot = current;
                }
            }
            return slot;
        }
        catch (Exception error)
        {
            // Execution has not begun. Report the caller's rejection without completing or
            // reporting queued results that never reached transport.
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }
}
