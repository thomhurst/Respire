using System.Diagnostics.CodeAnalysis;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// An explicit pipeline: queue commands, then <see cref="ExecuteAsync"/> flushes them to one
/// connection together and completes every queued <see cref="RespirePending{T}"/>. Not atomic —
/// use <see cref="RespireTransaction"/> for MULTI/EXEC semantics. Single-shot and not
/// thread-safe: build, send once, discard. Dispose an unsent batch to fault its queued pendings
/// with <see cref="RespireBatchDiscardedException"/>.
/// </summary>
/// <remarks>
/// Commands are grouped into the same facets as the client — <c>batch.Hashes.Set</c>
/// mirrors <c>client.Hashes.SetAsync</c> — with the <c>Async</c> suffix removed because queuing is
/// synchronous, but otherwise identical parameter shapes. The return type is a pending, not a task,
/// and there is no per-command cancellation token:
/// <see cref="ExecuteAsync"/> owns cancellation. Members that block (a <c>waitFor</c> argument) or
/// stream (<c>ScanAsync</c>, <c>GetLeaseAsync</c>) have no deferred form.
/// Streams, server administration, and distributed locks remain client-only because their
/// blocking, streaming, connection-scoped, or managed-lifetime semantics do not fit a deferred
/// single-flush command queue.
/// </remarks>
public sealed class RespireBatch : IDisposable, IPendingSink
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
    private IBatchScriptCommands? _scripts;

    internal RespireBatch(RespireClient client) => _client = client;

    /// <summary>Gets whether <see cref="ExecuteAsync"/> has started.</summary>
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

    /// <summary>Lua script evaluation. Redis: EVAL.</summary>
    public IBatchScriptCommands Scripts => _scripts ??= new BatchScriptCommands(this);

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

    RespireClient IPendingSink.Client => _client;

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
    /// Sends every queued command in one flush and completes all pendings. Per-command failures
    /// (server errors, <see cref="RespireOptions.CommandTimeout"/> expiry) fault that command's
    /// pending and are summarized in the returned result.
    /// In cluster mode, commands are grouped by slot and each group shares one connection so its
    /// commands retain queue order. Different slot groups may run out of order, and an acquisition
    /// failure faults only its group. Connection-acquisition failures use the same result contract
    /// in standalone and cluster modes; call <see cref="RespireBatchResult.ThrowIfAnyFailed"/> when
    /// fail-fast behavior is preferred.
    /// </summary>
    public async ValueTask<RespireBatchResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sent)
        {
            throw new InvalidOperationException("This batch has already been sent.");
        }

        _sent = true;
        var core = _client.Core;
        var telemetry = RespireTelemetry.StartBatchOperation(
            "PIPELINE",
            _ops,
            static op => op.Operation,
            core.Multiplexer.Host,
            core.Multiplexer.Port,
            core.Options.Database,
            out var telemetryOperation);
        if (_ops.Count == 0)
        {
            telemetry.Complete(core, telemetryOperation, batchSize: 0);
            return new RespireBatchResult(0, 0, null);
        }

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

            var clusterTasks = new Task<RespireBatchResult>[groups.Count];
            for (var i = 0; i < groups.Count; i++)
            {
                clusterTasks[i] = RunClusterGroupAsync(
                    groups[i].Slot, groups[i].Operations, cancellationToken);
            }

            var clusterResults = await Task.WhenAll(clusterTasks).ConfigureAwait(false);
            var firstError = _ops
                .Select(static operation => operation.Error)
                .FirstOrDefault(static error => error is not null);
            telemetry.Complete(
                core,
                telemetryOperation,
                error: firstError,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return new RespireBatchResult(
                _ops.Count,
                clusterResults.Sum(static result => result.FailureCount),
                firstError);
        }

        RespireConnection? connection = null;
        try
        {
            connection = await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The batch is single-shot: without a connection nothing ran, so every pending
            // must observe the acquisition failure rather than stay unreadable forever.
            foreach (var op in _ops)
            {
                op.Fail(ex);
            }

            telemetry.Complete(
                core,
                telemetryOperation,
                error: ex,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return new RespireBatchResult(_ops.Count, _ops.Count, ex);
        }

        // CommandTimeout is enforced per command by the connection's deadline sweep, which
        // fails an expired operation with a RespireTimeoutException carrying its name — no
        // batch-level CancellationTokenSource or per-operation registrations needed.
        var tasks = new Task<Exception?>[_ops.Count];
        for (var i = 0; i < _ops.Count; i++)
        {
            tasks[i] = _ops[i].RunAsync(_client, connection, cancellationToken);
        }

        var errors = await Task.WhenAll(tasks).ConfigureAwait(false);
        var batchFirstError = errors.FirstOrDefault(static error => error is not null);
        telemetry.Complete(
            core,
            telemetryOperation,
            error: batchFirstError,
            connection: connection,
            batchSize: _ops.Count == 1 ? null : _ops.Count);
        return new RespireBatchResult(
            _ops.Count,
            errors.Count(static error => error is not null),
            batchFirstError);
    }

    /// <summary>
    /// Discards an unsent batch and faults every queued pending with
    /// <see cref="RespireBatchDiscardedException"/>.
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

    private async Task<RespireBatchResult> RunClusterGroupAsync(
        int? slot,
        List<Op> operations,
        CancellationToken cancellationToken)
    {
        RespireConnection connection;
        try
        {
            connection = await _client.AcquireConnectionAsync(slot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var operation in operations)
            {
                operation.Fail(ex);
            }

            return new RespireBatchResult(operations.Count, operations.Count, ex);
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

        Exception? firstError = null;
        var failureCount = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            var error = await operations[i].CompleteClusterSendAsync(
                    _client, sends[i], cancellationToken)
                .ConfigureAwait(false);
            if (error is not null)
            {
                firstError ??= error;
                failureCount++;
            }
        }

        return new RespireBatchResult(operations.Count, failureCount, firstError);
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

        public abstract Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken);

        public abstract bool TryGetClusterSlot(out int slot);

        public abstract ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken);

        public abstract Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            ValueTask<RespValue> send,
            CancellationToken cancellationToken);

        public abstract void Fail(Exception error);
    }

    private sealed class Op<TCommand, T>(
        string operation, TCommand command, RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : Op(operation)
        where TCommand : struct, IRespCommand
    {
        public override Exception? Error => pending.Error;

        public override void Fail(Exception error) => pending.Fail(error);

        public override bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public override ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken)
            => client.SendOnConnectionAsync(Operation, connection, command, cancellationToken);

        public override async Task<Exception?> CompleteClusterSendAsync(
            RespireClient client,
            ValueTask<RespValue> send,
            CancellationToken cancellationToken)
        {
            try
            {
                RespValue value;
                try
                {
                    value = await send.ConfigureAwait(false);
                }
                catch (RespireServerException error) when (ClusterRouter.IsRedirect(error))
                {
                    value = await client.SendAsync(Operation, command, cancellationToken).ConfigureAwait(false);
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
