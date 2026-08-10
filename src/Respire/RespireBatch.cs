using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// An explicit pipeline: queue commands, then <see cref="SendAsync"/> flushes them to one
/// connection together and completes every queued <see cref="RespirePending{T}"/>. Not atomic —
/// use <see cref="RespireTransaction"/> for MULTI/EXEC semantics. Single-shot and not
/// thread-safe: build, send once, discard. Dispose an unsent batch to fault its queued pendings
/// with <see cref="RespireBatchDiscardedException"/>.
/// </summary>
/// <remarks>
/// Commands are grouped into the same facets as the client — <c>batch.Hashes.SetAsync</c>
/// mirrors <c>client.Hashes.SetAsync</c> — with identical names and parameter shapes. Only the
/// return type differs (a pending, not a task) and there is no per-command cancellation token:
/// <see cref="SendAsync"/> owns cancellation. Members that block (a <c>waitFor</c> argument) or
/// stream (<c>ScanAsync</c>, <c>GetLeaseAsync</c>) have no deferred form.
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

    internal RespireBatch(RespireClient client) => _client = client;

    /// <summary>Gets whether <see cref="SendAsync"/> has started.</summary>
    public bool IsSent => _sent;

    /// <summary>Gets the number of queued commands.</summary>
    public int Count => _ops.Count;

    // Deferred command facets, grouped exactly like the client's — same names, same parameter
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

    // Root shortcuts, mirroring the client's.

    /// <inheritdoc cref="IBatchStringCommands.GetStringAsync"/>
    public RespirePending<string?> GetStringAsync(RespireKey key) => Strings.GetStringAsync(key);

    /// <inheritdoc cref="IBatchStringCommands.GetAsync{T}"/>
    public RespirePending<T?> GetAsync<T>(RespireKey key) => Strings.GetAsync<T>(key);

    /// <inheritdoc cref="IBatchStringCommands.GetBytesAsync"/>
    public RespirePending<byte[]?> GetBytesAsync(RespireKey key) => Strings.GetBytesAsync(key);

    /// <inheritdoc cref="IBatchStringCommands.SetAsync(RespireKey, RespireValue, RespireTtl, SetWhen)"/>
    public RespirePending<bool> SetAsync(
        RespireKey key, RespireValue value, RespireTtl expiry = default, SetWhen when = SetWhen.Always)
        => Strings.SetAsync(key, value, expiry, when);

    /// <inheritdoc cref="IBatchStringCommands.SetAsync{T}(RespireKey, T, RespireTtl, SetWhen)"/>
    public RespirePending<bool> SetAsync<T>(
        RespireKey key, T value, RespireTtl expiry = default, SetWhen when = SetWhen.Always)
        => Strings.SetAsync(key, value, expiry, when);

    /// <inheritdoc cref="IBatchKeyCommands.DeleteAsync(ReadOnlySpan{RespireKey})"/>
    public RespirePending<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys) => Keys.DeleteAsync(keys);

    /// <inheritdoc cref="IBatchKeyCommands.ExistsAsync"/>
    public RespirePending<bool> ExistsAsync(RespireKey key) => Keys.ExistsAsync(key);

    /// <inheritdoc cref="IBatchStringCommands.IncrementAsync(RespireKey, long)"/>
    public RespirePending<long> IncrementAsync(RespireKey key, long by = 1) => Strings.IncrementAsync(key, by);

    /// <inheritdoc cref="IBatchStringCommands.DecrementAsync"/>
    public RespirePending<long> DecrementAsync(RespireKey key, long by = 1) => Strings.DecrementAsync(key, by);

    /// <inheritdoc cref="IBatchKeyCommands.ExpireAsync"/>
    public RespirePending<bool> ExpireAsync(RespireKey key, TimeSpan expiry) => Keys.ExpireAsync(key, expiry);

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
    /// pending, not this call; failing to obtain a connection at all faults every pending and
    /// rethrows.
    /// In cluster mode, commands are grouped by slot and each group shares one connection so its
    /// commands retain queue order. Different slot groups may run out of order, and an acquisition
    /// failure faults only its group; this method completes normally after recording the first
    /// error in telemetry.
    /// </summary>
    public async ValueTask SendAsync(CancellationToken cancellationToken = default)
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
            return;
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

            var clusterTasks = new Task<Exception?>[groups.Count];
            for (var i = 0; i < groups.Count; i++)
            {
                clusterTasks[i] = RunClusterGroupAsync(
                    groups[i].Slot, groups[i].Operations, cancellationToken);
            }

            var clusterErrors = await Task.WhenAll(clusterTasks).ConfigureAwait(false);
            telemetry.Complete(
                core,
                telemetryOperation,
                error: clusterErrors.FirstOrDefault(static error => error is not null),
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return;
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
            throw;
        }

        var timeout = _client.Core.Options.CommandTimeout;
        using var timeoutSource = timeout is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource?.CancelAfter(timeout!.Value);
        var effectiveToken = timeoutSource?.Token ?? cancellationToken;

        var tasks = new Task<Exception?>[_ops.Count];
        for (var i = 0; i < _ops.Count; i++)
        {
            tasks[i] = _ops[i].RunAsync(_client, connection, effectiveToken, cancellationToken, timeout);
        }

        var errors = await Task.WhenAll(tasks).ConfigureAwait(false);
        telemetry.Complete(
            core,
            telemetryOperation,
            error: errors.FirstOrDefault(static error => error is not null),
            connection: connection,
            batchSize: _ops.Count == 1 ? null : _ops.Count);
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

    private async Task<Exception?> RunClusterGroupAsync(
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

            return ex;
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
        for (var i = 0; i < operations.Count; i++)
        {
            var error = await operations[i].CompleteClusterSendAsync(
                    _client, sends[i], cancellationToken)
                .ConfigureAwait(false);
            firstError ??= error;
        }

        return firstError;
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

        public abstract Task<Exception?> RunAsync(
            RespireClient client, RespireConnection connection, CancellationToken effectiveToken,
            CancellationToken callerToken, TimeSpan? timeout);

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
            RespireClient client, RespireConnection connection, CancellationToken effectiveToken,
            CancellationToken callerToken, TimeSpan? timeout)
        {
            try
            {
                var value = await connection.SendAsync(in command, effectiveToken).ConfigureAwait(false);
                return Complete(client, value);
            }
            catch (OperationCanceledException) when (timeout is { } expired && !callerToken.IsCancellationRequested)
            {
                var error = new RespireTimeoutException(Operation, expired);
                pending.Fail(error);
                return error;
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
                    var error = ResponseReader.ServerError(in value);
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
