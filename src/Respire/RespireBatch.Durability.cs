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
            var replicas = ResponseReader.Integer(in counts[1]);
            if (local is < 0 or > 1 || replicas < 0)
                throw new RespireProtocolException("WAITAOF returned invalid acknowledgement counts.");
            return new RespireAofAcknowledgement(local, replicas);
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
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(_client.Core.Disposed, _client);
        if (_sent) throw new InvalidOperationException("This batch has already been sent.");
        if (_ops.Count == 0) throw new InvalidOperationException("A durability acknowledgement requires a nonempty batch.");
        cancellationToken.ThrowIfCancellationRequested();
        var core = _client.Core;
        int? slot = null;
        if (core.Cluster is not null)
        {
            foreach (var op in _ops)
            {
                if (!op.TryGetClusterSlot(out var current))
                    throw new NotSupportedException("Cluster durability batches require a routing key for every command.");
                if (slot is { } previous && previous != current)
                    throw new NotSupportedException("Cluster durability batches require every command to use the same hash slot.");
                slot = current;
            }
        }

        _sent = true;
        var telemetry = RespireTelemetry.StartBatchOperation(
            operation, _ops, static op => op.Operation,
            core.Multiplexer.Host, core.Multiplexer.Port, core.Options.Database, out var telemetryOperation);
        DedicatedConnectionPool? pool = null;
        RespireConnection? connection = null;
        Exception? operationError = null;
        var reusable = false;
        core.ClientCache?.FlushForUnknownCommand();
        try
        {
            pool = core.Cluster is { } cluster
                ? await cluster.GetDedicatedPoolAsync(slot, cancellationToken).ConfigureAwait(false)
                : core.DedicatedPool;
            connection = await pool.RentAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var writes = new Task<Exception?>[_ops.Count];
            for (var index = 0; index < _ops.Count; index++)
                writes[index] = _ops[index].RunAsync(_client, connection, cancellationToken);
            await Task.WhenAll(writes).ConfigureAwait(false);
            new RespireBatchResult(_ops.Count, CollectFailures(_ops)).ThrowIfAnyFailed();
            cancellationToken.ThrowIfCancellationRequested();

            using var response = await connection.SendWithoutResponseTimeoutAsync(acknowledgement, cancellationToken).ConfigureAwait(false);
            if (response.IsError) throw ResponseReader.ServerError(in response, operation);
            var result = convert(response);
            reusable = true;
            return result;
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
            core.ClientCache?.FlushForUnknownCommand();
            try
            {
                if (connection is not null)
                {
                    if (reusable) pool!.Return(connection);
                    // Deliberately discard every failed execution, even a fully read server error.
                    else await pool!.DiscardAsync(connection).ConfigureAwait(false);
                }
            }
            finally
            {
                telemetry.Complete(core, telemetryOperation, error: operationError, connection: connection,
                    batchSize: _ops.Count == 1 ? null : _ops.Count);
            }
        }
    }
}
