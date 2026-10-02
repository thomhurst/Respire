using System.Diagnostics.CodeAnalysis;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// An explicit pipeline: queue commands, then <see cref="ExecuteAsync"/> flushes them to one
/// connection together and completes every queued <see cref="RespirePending{T}"/>. Not atomic —
/// use <see cref="RespireTransaction"/> for MULTI/EXEC semantics. Single-shot and not
/// thread-safe: build, send once, discard. Always use a <c>using</c> declaration. Disposing an
/// unsent batch faults its queued pendings with <see cref="RespireBatchDiscardedException"/>;
/// disposing after execution preserves their results and errors.
/// </summary>
/// <remarks>
/// Commands are grouped into the same facets as the client — <c>batch.Hashes.Set</c>
/// mirrors <c>client.Hashes.SetAsync</c> — with the <c>Async</c> suffix removed because queuing is
/// synchronous, but otherwise identical parameter shapes. The return type is a pending, not a task,
/// and there is no per-command cancellation token:
/// <see cref="ExecuteAsync"/> owns cancellation. List pop and move commands expose only their
/// non-blocking form, without the client's <c>waitFor</c> argument. Streaming and leased reads
/// (<c>ScanAsync</c>, <c>GetLeaseAsync</c>) have no deferred form. Script commands use
/// <c>Evaluate</c> rather than the client's <c>ExecuteAsync</c> name and return owned results.
/// Raw Execute supports known nonblocking key layouts and owned results. Unknown layouts are rejected.
/// Stream append, range, count, remove, trim, and acknowledge commands support deferred execution.
/// Blocking stream reads, consumer loops, group administration, server administration, and distributed
/// locks remain client-only because their blocking, streaming, connection-scoped, or managed-lifetime
/// semantics do not fit a deferred single-flush command queue.
/// </remarks>
public sealed partial class RespireBatch : IDisposable, IRespireCommandQueue, IPendingSink
{
    private readonly RespireClient _client;
    private readonly List<Op> _ops = [];
    private bool _disposed;
    private bool _sent;

    private IBatchStringCommands? _strings;
    private IBatchKeyCommands? _keys;
    private IBatchHashCommands? _hashes;
    private IBatchListCommands? _lists;
    private IBatchSetCommands? _sets;
    private IBatchSortedSetCommands? _sortedSets;
    private IBatchBitmapCommands? _bitmaps;
    private IBatchHyperLogLogCommands? _hyperLogLog;
    private IBatchGeoCommands? _geo;
    private IBatchVectorSetCommands? _vectorSets;
    private IBatchScriptCommands? _scripts;
    private IBatchFunctionCommands? _functions;
    private IBatchStreamCommands? _streams;

    internal RespireBatch(RespireClient client) => _client = client;

    /// <summary>Gets whether any batch execution method has started sending this batch.</summary>
    public bool IsSent => _sent;

    /// <summary>Gets the number of queued commands.</summary>
    public int Count => _ops.Count;

    // Deferred command facets, grouped like the client's — synchronous names, same parameter
    // shapes, each returning a RespirePending instead of a ValueTask. Created on first use.

    /// <summary>String (plain value) commands. Redis: GET, SET, INCR, …</summary>
    public IBatchStringCommands Strings => _strings ??= new BatchStringCommands(this);

    /// <summary>Generic key management commands. Redis: DEL, EXPIRE, TYPE, …</summary>
    public IBatchKeyCommands Keys => _keys ??= new BatchKeyCommands(this);

    /// <summary>Hash (field → value map) commands. Redis: HSET, HGET, HGETALL, …</summary>
    public IBatchHashCommands Hashes => _hashes ??= new BatchHashCommands(this);

    /// <summary>List commands. Redis: LPUSH, RPUSH, LRANGE, …</summary>
    public IBatchListCommands Lists => _lists ??= new BatchListCommands(this);

    /// <summary>Set (unordered, unique members) commands. Redis: SADD, SMEMBERS, …</summary>
    public IBatchSetCommands Sets => _sets ??= new BatchSetCommands(this);

    /// <summary>Sorted set (score-ordered members) commands. Redis: ZADD, ZRANGE, …</summary>
    public IBatchSortedSetCommands SortedSets => _sortedSets ??= new BatchSortedSetCommands(this);

    /// <summary>Bitmap commands. Redis: SETBIT, BITCOUNT, BITOP, …</summary>
    public IBatchBitmapCommands Bitmaps => _bitmaps ??= new BatchBitmapCommands(this);

    /// <summary>HyperLogLog commands. Redis: PFADD, PFCOUNT, PFMERGE.</summary>
    public IBatchHyperLogLogCommands HyperLogLog => _hyperLogLog ??= new BatchHyperLogLogCommands(this);

    /// <summary>Geospatial commands. Redis: GEOADD, GEODIST, GEOSEARCH, …</summary>
    public IBatchGeoCommands Geo => _geo ??= new BatchGeoCommands(this);

    /// <summary>Redis vector-set commands.</summary>
    public IBatchVectorSetCommands VectorSets => _vectorSets ??= new BatchVectorSetCommands(this);

    /// <summary>Lua script evaluation. Redis: EVAL.</summary>
    public IBatchScriptCommands Scripts => _scripts ??= new BatchScriptCommands(this);

    /// <summary>Redis Functions, without automatic reload or replay.</summary>
    public IBatchFunctionCommands Functions => _functions ??= new BatchFunctionCommands(this);

    /// <summary>Non-blocking stream append, range, count, acknowledge, remove, and trim commands.</summary>
    public IBatchStreamCommands Streams => _streams ??= new BatchStreamCommands(this);

    // Root shortcuts, mirroring the client's.

    /// <inheritdoc cref="IBatchStringCommands.GetString"/>
    public RespirePending<string?> GetString(RespireKey key) => Strings.GetString(key);

    /// <inheritdoc cref="IBatchStringCommands.Get{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<T?> Get<T>(RespireKey key) => Strings.Get<T>(key);

    /// <inheritdoc cref="IBatchStringCommands.TryGet{T}"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<RespireGet<T>> TryGet<T>(RespireKey key) => Strings.TryGet<T>(key);

    /// <inheritdoc cref="IBatchStringCommands.GetBytes"/>
    public RespirePending<byte[]?> GetBytes(RespireKey key) => Strings.GetBytes(key);

    /// <inheritdoc cref="IBatchStringCommands.Set(RespireKey, RespireValue, RespireExpiry, SetWhen)"/>
    public RespirePending<bool> Set(
        RespireKey key, RespireValue value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => Strings.Set(key, value, expiry, when);

    /// <inheritdoc cref="IBatchStringCommands.Set{T}(RespireKey, T, RespireExpiry, SetWhen)"/>
    [RequiresUnreferencedCode(SerializationWarnings.UnreferencedCode)]
    [RequiresDynamicCode(SerializationWarnings.DynamicCode)]
    public RespirePending<bool> Set<T>(
        RespireKey key, T value, RespireExpiry expiry = default, SetWhen when = SetWhen.Always)
        => Strings.Set(key, value, expiry, when);

    /// <inheritdoc cref="IBatchKeyCommands.Delete(ReadOnlySpan{RespireKey})"/>
    public RespirePending<long> Delete(params ReadOnlySpan<RespireKey> keys) => Keys.Delete(keys);

    /// <inheritdoc cref="IBatchKeyCommands.Exists"/>
    public RespirePending<bool> Exists(RespireKey key) => Keys.Exists(key);

    /// <inheritdoc cref="IBatchStringCommands.Increment(RespireKey, long)"/>
    public RespirePending<long> Increment(RespireKey key, long by = 1) => Strings.Increment(key, by);

    /// <inheritdoc cref="IBatchStringCommands.Decrement"/>
    public RespirePending<long> Decrement(RespireKey key, long by = 1) => Strings.Decrement(key, by);

    /// <inheritdoc cref="IBatchKeyCommands.Expire"/>
    public RespirePending<bool> Expire(
        RespireKey key, RespireExpiry expiry, ExpireWhen when = ExpireWhen.Always)
        => Keys.Expire(key, expiry, when);

    /// <inheritdoc cref="IRespireCommandQueue.Execute"/>
    public RespirePending<RespireResult> Execute(RespireCommand command, params RespireValue[] args)
        => DeferredRawCommands.Enqueue(this, command, args);

    RespireClient IPendingSink.Client => _client;

    bool IPendingSink.DefersSerialization => true;

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, ReadOnlySpan<RespireKey> keys,
        Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, RespireKey second,
        Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, ReadOnlySpan<RespireKey> rest,
        Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    /// <summary>
    /// Sends every queued command and completes all pendings, then rethrows the first failure
    /// in original queue order. Successful pending results remain available when another command
    /// fails. Use <see cref="TryExecuteAsync"/> and <see cref="RespireBatchResult.Failures"/>
    /// to inspect every failure without rethrowing the first one.
    /// </summary>
    public async ValueTask<RespireBatchResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        var result = await TryExecuteAsync(cancellationToken).ConfigureAwait(false);
        result.ThrowIfAnyFailed();
        return result;
    }

    /// <summary>
    /// Sends every queued command in one flush and completes all pendings. Per-command failures
    /// (server errors, <see cref="RespireOptions.CommandTimeout"/> expiry) fault that command's
    /// pending and are summarized in the returned result.
    /// In cluster mode, commands are grouped by slot and each group shares one connection so its
    /// commands retain queue order. Different slot groups may run out of order, and an acquisition
    /// failure faults only its group. Connection-acquisition failures use the same result contract
    /// in standalone and cluster modes. Command, conversion, timeout, cancellation, and
    /// connection-acquisition failures are reported in the result rather than rethrown.
    /// Invalid lifecycle use, such as executing a disposed or already-sent batch, still throws.
    /// </summary>
    public async ValueTask<RespireBatchResult> TryExecuteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        _sent = true;
        var core = _client.Core;
        var telemetryOperation = "PIPELINE";
        var sentinelStarted = core.Sentinel is null ? 0 : RespireTelemetry.CaptureStartTimestamp();
        var telemetry = core.Sentinel is null ? RespireTelemetry.StartBatchOperation(
            "PIPELINE",
            _ops,
            static op => op.Operation,
            core.Endpoint,
            core.Options.Database,
            out telemetryOperation) : default;
        if (_ops.Count == 0)
        {
            if (core.Sentinel is not null)
            {
                telemetry = RespireTelemetry.StartBatchOperation(
                    "PIPELINE", _ops, static op => op.Operation, core.Options.Database,
                    out telemetryOperation, sentinelStarted);
            }
            if (core.Sentinel is null)
                telemetry.Complete(core, telemetryOperation, batchSize: 0);
            else
                telemetry.Complete(telemetryOperation, host: null, port: 6379,
                    database: core.Options.Database, batchSize: 0);
            return new RespireBatchResult(0, null);
        }

        // Batch commands bypass the client's per-command mutation classifier. Conservatively
        // fence older reads unless the complete policy-routed batch is known to be read-only.
        var cacheToInvalidate = core.ClientCache;
        if (cacheToInvalidate is not null && core.Cluster is not null
            && GetGroupReadFrom(_ops) != RespireReadFrom.Primary)
            cacheToInvalidate = null;
        cacheToInvalidate?.FlushForUnknownCommand();

        if (core.Cluster is not null)
        {
            var groups = new List<(int? Slot, List<Op> Operations)>();
            var groupIndexes = new Dictionary<int, int>();
            foreach (var op in _ops)
            {
                var groupKey = op.TryGetClusterSlot(out var slot) ? slot + 1 : 0;
                if (!groupIndexes.TryGetValue(groupKey, out var groupIndex))
                {
                    groupIndex = groups.Count;
                    groupIndexes.Add(groupKey, groupIndex);
                    groups.Add((groupKey == 0 ? null : slot, []));
                }

                groups[groupIndex].Operations.Add(op);
            }

            try
            {
                var clusterTasks = new Task[groups.Count];
                for (var i = 0; i < groups.Count; i++)
                {
                    clusterTasks[i] = RunClusterGroupAsync(
                        groups[i].Slot, groups[i].Operations,
                        GetGroupReadFrom(groups[i].Operations), cancellationToken);
                }

                await Task.WhenAll(clusterTasks).ConfigureAwait(false);
            }
            finally
            {
                cacheToInvalidate?.FlushForUnknownCommand();
            }

            var failures = CollectFailures(_ops);
            var firstError = failures is { Length: > 0 } ? failures[0].Error : null;
            telemetry.Complete(
                core,
                telemetryOperation,
                error: firstError,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return new RespireBatchResult(_ops.Count, failures);
        }

        RespireConnection? connection = null;
        try
        {
            connection = await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (core.Sentinel is not null)
                telemetry = RespireTelemetry.StartBatchOperation(
                    "PIPELINE", _ops, static op => op.Operation,
                    connection.Host, connection.Port, core.Options.Database, out telemetryOperation, sentinelStarted);
        }
        catch (Exception ex)
        {
            // The batch is single-shot: without a connection nothing ran, so every pending
            // must observe the acquisition failure rather than stay unreadable forever.
            foreach (var op in _ops)
            {
                op.Fail(ex);
            }

            if (connection is null)
                RespireTelemetry.RecordUnroutedBatchFailure("PIPELINE", _ops, static op => op.Operation,
                    core.Options.Database, sentinelStarted, ex);
            telemetry.Complete(
                core,
                telemetryOperation,
                error: ex,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return new RespireBatchResult(_ops.Count, CollectFailures(_ops));
        }

        // CommandTimeout is enforced per command by the connection's deadline sweep, which
        // fails an expired operation with a RespireTimeoutException carrying its name — no
        // batch-level CancellationTokenSource or per-operation registrations needed.
        try
        {
            var tasks = new Task<Exception?>[_ops.Count];
            for (var i = 0; i < _ops.Count; i++)
            {
                tasks[i] = _ops[i].RunAsync(_client, connection, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            cacheToInvalidate?.FlushForUnknownCommand();
        }

        var batchFailures = CollectFailures(_ops);
        var batchFirstError = batchFailures is { Length: > 0 } ? batchFailures[0].Error : null;
        telemetry.Complete(
            core,
            telemetryOperation,
            error: batchFirstError,
            connection: connection,
            batchSize: _ops.Count == 1 ? null : _ops.Count);
        return new RespireBatchResult(_ops.Count, batchFailures);
    }

    /// <summary>
    /// Discards an unsent batch and faults every queued pending with
    /// <see cref="RespireBatchDiscardedException"/>. After execution, preserves all pending results
    /// and errors. Repeated disposal is safe; always use a <c>using</c> declaration.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_sent)
        {
            return;
        }

        var error = new RespireBatchDiscardedException();
        foreach (var operation in _ops)
        {
            operation.Fail(error);
        }
    }

    private async Task RunClusterGroupAsync(
        int? slot,
        List<Op> operations,
        RespireReadFrom readFrom,
        CancellationToken cancellationToken)
    {
        RespireConnection connection;
        try
        {
            connection = await _client.AcquireConnectionAsync(slot, cancellationToken, readFrom).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var operation in operations)
            {
                operation.Fail(ex);
            }

            return;
        }

        var sends = new ValueTask<RespValue>[operations.Count];
        // Start every send in queue order before awaiting responses to retain pipelining.
        for (var i = 0; i < operations.Count; i++)
        {
            try
            {
                sends[i] = operations[i].StartClusterSend(_client, connection, cancellationToken);
            }
            catch (Exception ex)
            {
                sends[i] = ValueTask.FromException<RespValue>(ex);
            }
        }

        for (var i = 0; i < operations.Count; i++)
        {
            _ = await operations[i].CompleteClusterSendAsync(
                    _client, connection, sends[i], readFrom, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // Operations in one slot group share a pipeline and run in order. One write sends the whole
    // group to the primary: splitting reads onto a replica would let a later read in the batch
    // miss an earlier write to the same slot.
    private RespireReadFrom GetGroupReadFrom(List<Op> operations)
    {
        var policy = _client.GetBatchReadFromPolicy();
        if (policy == RespireReadFrom.Primary) return RespireReadFrom.Primary;
        for (var index = 0; index < operations.Count; index++)
        {
            if (!operations[index].IsReadOnly) return RespireReadFrom.Primary;
        }
        return policy;
    }

    private static RespireBatchFailure[]? CollectFailures(IReadOnlyList<Op> operations)
    {
        var failureCount = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            if (operations[i].Error is not null)
            {
                failureCount++;
            }
        }

        if (failureCount == 0)
        {
            return null;
        }

        var failures = new RespireBatchFailure[failureCount];
        var failureIndex = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            if (operation.Error is { } error)
            {
                failures[failureIndex++] = new RespireBatchFailure(
                    i, operation.Operation, error);
            }
        }

        return failures;
    }

    private RespirePending<T> Add<TCommand, T>(string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        var pending = new RespirePending<T>();
        _ops.Add(new Op<TCommand, T>(operation, command, pending, convert));
        return pending;
    }

    private abstract class Op
    {
        protected Op(string operation) => Operation = operation;

        public string Operation { get; }

        public abstract Exception? Error { get; }

        public abstract bool IsCompleted { get; }

        public abstract bool IsReadOnly { get; }

        public abstract Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken);

        public abstract bool TryGetClusterSlot(out int slot);

        public abstract ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken);

        public abstract Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken);

        public abstract void Fail(Exception error);
    }

    private sealed class Op<TCommand, T>(
        string operation,
        TCommand command,
        RespirePending<T> pending,
        Func<RespireClient, RespValue, T> convert) : Op(operation)
        where TCommand : struct, IRespCommand
    {
        public override Exception? Error => pending.Error;

        public override bool IsCompleted => pending.IsCompleted;

        public override bool IsReadOnly => command.ReadKind != ReadCommandKind.None;

        public override void Fail(Exception error) => pending.Fail(error);

        public override bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public override ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken)
            => client.SendOnConnectionAsync(Operation, connection, command, cancellationToken);

        public override async Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken)
        {
            try
            {
                RespValue value;
                try
                {
                    value = await send.ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException error)
                {
                    // Retry only this rejected operation; other pipeline entries may already be accepted.
                    value = await client.ResumeRetiredClusterSendAsync(
                        Operation, command, connection, error, readFrom, cancellationToken).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (command.TryGetClusterSlot(out var readSlot)
                    && ReadFallbackPolicy.CanFallBackToOtherRole(error, readFrom, readSlot,
                        ReadFallbackPolicy.IsReplicaConnection(connection)))
                {
                    // Complete each operation in queue order; retry only its rejected read.
                    value = await client.ResumeRejectedClusterSendAsync(
                            Operation, command, connection, error, readFrom, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (RespireServerException error) when (
                    ClusterRouter.CanRecover(error, command.TryGetClusterSlot(out var slot) ? slot : null))
                {
                    value = await client.ResumeRejectedClusterSendAsync(
                            Operation, command, connection, error, readFrom, cancellationToken)
                        .ConfigureAwait(false);
                }

                return Complete(client, value);
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return ex;
            }
        }

        public override async Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                var value = await connection.SendAsync(in command, cancellationToken, commandName: Operation)
                    .ConfigureAwait(false);
                return Complete(client, value);
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return ex;
            }
        }

        private Exception? Complete(RespireClient client, RespValue value)
        {
            try
            {
                if (value.IsError)
                {
                    var error = ResponseReader.ServerError(in value, Operation);
                    pending.Fail(error);
                    return error;
                }

                try
                {
                    pending.Succeed(convert(client, value));
                    return null;
                }
                catch (Exception ex)
                {
                    // Conversion failed after Redis completed successfully; not a DB error.
                    pending.Fail(ex);
                    return ex;
                }
            }
            finally
            {
                value.Dispose();
            }
        }
    }
}
