using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
/// Prefixed binary keys are snapshotted when queued, including resolved keys passed to an
/// unprefixed batch. Other binary arguments remain borrowed
/// until execution completes unless their command explicitly snapshots them.
/// Stream append, range, count, remove, trim, and acknowledge commands support deferred execution.
/// Server flush commands affect only their execution node. Other server administration,
/// blocking stream reads, consumer loops, group administration, and distributed
/// locks remain client-only because their blocking, streaming, connection-scoped, or managed-lifetime
/// semantics do not fit a deferred single-flush command queue.
/// </remarks>
public sealed partial class RespireBatch : IDisposable, IRespireCommandQueue, IPendingSink
{
    private readonly RespireClient _client;
    private readonly RespireHashImportSession? _importSession;
    private QueuedConnectionPolicy ConnectionPolicy => new(_importSession);
    private readonly List<Op> _ops = [];
    private bool _disposed;
    private bool _sent;

    private IBatchStringCommands? _strings;
    private IBatchKeyCommands? _keys;
    private IBatchServerCommands? _server;
    private IBatchHashCommands? _hashes;
    private IBatchListCommands? _lists;
    private IBatchArrayCommands? _arrays;
    private IBatchSetCommands? _sets;
    private IBatchSortedSetCommands? _sortedSets;
    private IBatchBitmapCommands? _bitmaps;
    private IBatchHyperLogLogCommands? _hyperLogLog;
    private IBatchGeoCommands? _geo;
    private IBatchVectorSetCommands? _vectorSets;
    private IBatchScriptCommands? _scripts;
    private IBatchFunctionCommands? _functions;
    private IBatchStreamCommands? _streams;

    internal RespireBatch(RespireClient client) => _client = client.ForDeferredBatch();

    internal RespireBatch(RespireClient client, RespireHashImportSession importSession)
    {
        _client = client.ForDeferredBatch();
        _importSession = importSession;
    }

    private RespireHashImportSession.Usage? EnterObservedOperation()
    {
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_sent) throw new InvalidOperationException("This batch has already been sent.");
            return ConnectionPolicy.EnterOperation();
        }
        catch (Exception error)
        {
            // No queued command owns an observation until setup succeeds.
            RespireTelemetry.RecordError(error, internallyHandled: false);
            throw;
        }
    }

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

    /// <summary>Server flush commands affecting only the execution node.</summary>
    public IBatchServerCommands Server => _server ??= new BatchServerCommands(this);

    /// <summary>Hash (field → value map) commands. Redis: HSET, HGET, HGETALL, …</summary>
    public IBatchHashCommands Hashes => _hashes ??= new BatchHashCommands(this);

    /// <summary>List commands. Redis: LPUSH, RPUSH, LRANGE, …</summary>
    public IBatchListCommands Lists => _lists ??= new BatchListCommands(this);

    /// <summary>Redis sparse array commands.</summary>
    public IBatchArrayCommands Arrays => _arrays ??= new BatchArrayCommands(this);

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

    RespireHashImportSession? IPendingSink.ImportSession => _importSession;

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
        using var importUsage = EnterObservedOperation();
        _sent = true;
        var core = _client.Core;
        var telemetryOperation = "PIPELINE";
        var sentinelStarted = core.Sentinel is null ? default : RespireTelemetry.CaptureBatchStart("PIPELINE", _ops, static op => op.Operation);
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
        // fence older reads unless a non-primary view's complete batch is known to be read-only.
        // Read-only commands can still require primary routing, such as CLUSTERSCAN.
        var cacheToInvalidate = core.ClientCache;
        if (cacheToInvalidate is not null && core.Cluster is not null
            && _client.GetBatchReadFromPolicy() != RespireReadFrom.Primary
            && _ops.TrueForAll(static operation => operation.IsReadOnly))
            cacheToInvalidate = null;
        var mutationFence = cacheToInvalidate is null ? default : cacheToInvalidate.BeginUnknownMutation();
        try
        {
            foreach (var op in _ops) op.MutationFence = mutationFence;

            if (core.Cluster is not null && ConnectionPolicy.CanReplayRejectedCommands)
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

                var clusterTasks = new Task[groups.Count];
                for (var i = 0; i < groups.Count; i++)
                {
                    clusterTasks[i] = RunClusterGroupAsync(
                        groups[i].Slot, groups[i].Operations,
                        GetGroupReadFrom(groups[i].Operations), cancellationToken);
                }

                await Task.WhenAll(clusterTasks).ConfigureAwait(false);

                var failures = CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence);
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
                connection = ConnectionPolicy.PinnedConnection ?? await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                return new RespireBatchResult(_ops.Count,
                    CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence));
            }

            // CommandTimeout is enforced per command by the connection's deadline sweep, which
            // fails an expired operation with a RespireTimeoutException carrying its name — no
            // batch-level CancellationTokenSource or per-operation registrations needed.
            if (ConnectionPolicy.IsImportSession)
            {
                await RunImportBatchAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await RunStandaloneBatchAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            var batchFailures = CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence);
            var batchFirstError = batchFailures is { Length: > 0 } ? batchFailures[0].Error : null;
            telemetry.Complete(
                core,
                telemetryOperation,
                error: batchFirstError,
                connection: connection,
                batchSize: _ops.Count == 1 ? null : _ops.Count);
            return new RespireBatchResult(_ops.Count, batchFailures);
        }
        finally { cacheToInvalidate?.CompleteMutation(in mutationFence); }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    private async ValueTask RunStandaloneBatchAsync(RespireConnection connection, CancellationToken cancellationToken)
    {
        var sends = ArrayPool<ValueTask<RespValue>>.Shared.Rent(_ops.Count);
        var observations = ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(_ops.Count);
        try
        {
            for (var i = 0; i < _ops.Count; i++)
            {
                observations[i] = RespireTelemetry.ErrorObservation.Rent(force: true);
            }
            if (!connection.TryEnqueueMany(_ops, sends, observations, cancellationToken))
            {
                // Await admission, not replies, so a full ring or credential fence cannot
                // reverse this batch's queue order. The response thread frees ring slots
                // without awaiting these reply tasks. Capture the original budget once;
                // capacity waits must not restart the timeout for every later command.
                var deadline = connection.CreateCommandDeadline();
                for (var i = 0; i < _ops.Count; i++)
                    sends[i] = await _ops[i].StartOrderedSendAsync(connection, cancellationToken, observations[i], deadline).ConfigureAwait(false);
            }
            for (var i = 0; i < _ops.Count; i++)
                _ = await _ops[i].CompleteSendAsync(_client, sends[i]).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<ValueTask<RespValue>>.Shared.Return(sends, clearArray: true);
            CompleteObservations(_ops, observations);
        }
    }

    private async Task RunImportBatchAsync(RespireConnection connection, CancellationToken cancellationToken)
    {
        // Bound retained reply tasks, and await each admission before starting the next.
        // Even an empty ring may be fenced by credential renewal; its waiters are not FIFO.
        var tasks = new Task<Exception?>[Math.Min(_ops.Count, _client.Core.Options.MaxInflightCommands)];
        for (var offset = 0; offset < _ops.Count;)
        {
            var count = Math.Min(tasks.Length, _ops.Count - offset);
            for (var index = 0; index < count; index++)
                tasks[index] = await _ops[offset + index].StartImportAsync(_client, connection, cancellationToken).ConfigureAwait(false);
            // Slots beyond count still contain completed tasks from the preceding chunk.
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);
            offset += count;
            for (var index = 0; index < count; index++)
            {
                if (results[index] is not { } error || !ConnectionPolicy.RequiresExpiration(error)) continue;
                await ConnectionPolicy.ExpireAsync(error).ConfigureAwait(false);
                for (; offset < _ops.Count; offset++) _ops[offset].Fail(error);
                return;
            }
        }
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
        RespireConnection? continuationConnection;
        // Shared read selection precedes each command's deferred owner. Keep its failures once,
        // then copy the completed count into every member without sharing a live pooled handle.
        var selectionObservation = _client.GetBatchReadFromPolicy() != RespireReadFrom.Primary
            ? RespireTelemetry.ErrorObservation.Rent(force: true) : default;
        var selectionAttempts = 0;
        try
        {
            if (slot is null && operations.Exists(static operation => operation.Operation is "FLUSHDB" or "FLUSHALL"))
                slot = await _client.Core.Cluster!.GetPrimaryRoutingSlotAsync(cancellationToken).ConfigureAwait(false);
            (connection, continuationConnection) = await AcquireGroupConnectionsAsync(
                slot, operations, readFrom, cancellationToken, selectionObservation).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var operation in operations)
            {
                operation.AddErrorAttempts(selectionObservation.Attempts);
                operation.Fail(ex);
            }

            return;
        }
        finally
        {
            selectionAttempts = selectionObservation.Attempts;
            selectionObservation.Dispose();
        }

        var sends = new ValueTask<RespValue>[operations.Count];
        var observations = ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(operations.Count);
        try
        {
            // Start every send in queue order before awaiting responses to retain pipelining.
            for (var i = 0; i < operations.Count; i++)
            {
                observations[i] = RespireTelemetry.ErrorObservation.Rent(force: true);
                if (selectionAttempts != 0) observations[i].SetAttempts(selectionAttempts);
                try
                {
                    var operationConnection = operations[i].IsCursorContinuation == true ? continuationConnection ?? connection : connection;
                    sends[i] = operations[i].StartClusterSend(_client, operationConnection, cancellationToken, observations[i]);
                }
                catch (Exception ex)
                {
                    sends[i] = ValueTask.FromException<RespValue>(ex);
                }
            }

            for (var i = 0; i < operations.Count; i++)
            {
                var operationConnection = operations[i].IsCursorContinuation == true ? continuationConnection ?? connection : connection;
                _ = await operations[i].CompleteClusterSendAsync(
                        this, operationConnection, sends[i], readFrom, cancellationToken, observations[i])
                    .ConfigureAwait(false);
            }
        }
        finally { CompleteObservations(operations, observations); }
    }

    // Leases belong to an execution, not to every queued command object's layout.
    // Keep them alive through all replies and recovery, then copy immutable attempt counts.
    private static void CompleteObservations(IReadOnlyList<Op> operations,
        RespireTelemetry.ErrorObservation[] observations)
    {
        for (var i = 0; i < operations.Count; i++)
        {
            operations[i].AddErrorAttempts(observations[i].DisposeAndGetAttempts());
        }
        ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Return(observations, clearArray: true);
    }

    private async ValueTask<(RespireConnection Connection, RespireConnection? Continuation)> AcquireGroupConnectionsAsync(
        int? slot, List<Op> operations, RespireReadFrom readFrom, CancellationToken cancellationToken,
        RespireTelemetry.ErrorObservation observation)
    {
        var configuredReadFrom = _client.GetBatchReadFromPolicy();
        var hasFreshCursor = false;
        var hasContinuation = false;
        if (configuredReadFrom != RespireReadFrom.Primary)
        {
            foreach (var operation in operations)
            {
                if (operation.IsCursorContinuation is not { } continuation) continue;
                hasFreshCursor |= !continuation;
                hasContinuation |= continuation;
            }
        }
        if ((!hasFreshCursor && !hasContinuation) || slot is not { } cursorSlot)
            return (await _client.AcquireConnectionAsync(slot, cancellationToken, readFrom, observation).ConfigureAwait(false), null);

        // Capture the issuing connection before a fresh scan can revalidate and replace the shared pin.
        var cursors = _client.Core.ReadRouter.Cursors;
        var cluster = _client.Core.Cluster!;
        var continuationConnection = hasContinuation
            ? await cursors.GetClusterConnectionAsync(cluster, cursorSlot, configuredReadFrom,
                affinity: null, isContinuation: true, cancellationToken, observation).ConfigureAwait(false)
            : null;
        var connection = hasFreshCursor
            ? await cursors.GetClusterConnectionAsync(cluster, cursorSlot, configuredReadFrom,
                affinity: null, isContinuation: false, cancellationToken, observation).ConfigureAwait(false)
            : continuationConnection!;
        if (readFrom != configuredReadFrom)
        {
            // A cursor pin identifies its issuing connection, not its current role.
            // Resolve the current primary separately before allowing any writes in this group.
            var primary = await _client.AcquireConnectionAsync(slot, cancellationToken, RespireReadFrom.Primary).ConfigureAwait(false);
            if (!ReferenceEquals(connection.Multiplexer, primary.Multiplexer)
                || (continuationConnection is not null && !ReferenceEquals(continuationConnection.Multiplexer, primary.Multiplexer)))
            {
                var names = string.Join(", ", operations.Select(static operation => operation.Operation).Distinct());
                throw new NotSupportedException($"A cursor page pinned to a replica cannot share its batch group with writes. Operations: {names}. Keep cursor pages in a read-only group.");
            }
        }
        return (connection, continuationConnection);
    }

    // One write sends the whole slot group through the primary pipeline in queue order:
    // splitting reads onto a replica would let a later read miss an earlier same-slot write.
    private RespireReadFrom GetGroupReadFrom(List<Op> operations)
    {
        var policy = _client.GetBatchReadFromPolicy();
        if (policy == RespireReadFrom.Primary) return RespireReadFrom.Primary;
        for (var index = 0; index < operations.Count; index++)
        {
            if (!operations[index].AllowsReadRouting) return RespireReadFrom.Primary;
        }
        return policy;
    }

    private RespireBatchFailure[]? CompleteMutationAndCollectFailures(
        ref ClientSideCacheCoordinator? cache, in ClientSideCacheCoordinator.MutationFence fence)
    {
        // Native response ownership may outlive caller cancellation. Complete only the
        // logical fence before reporting; clear the owner to avoid completing it twice.
        cache?.CompleteMutation(in fence);
        cache = null;
        return CollectFailures(_ops);
    }

    private static RespireBatchFailure[]? CollectFailures(IReadOnlyList<Op> operations, bool reportErrors = true)
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
                if (reportErrors) operation.ReportError();
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

        ConnectionPolicy.ValidateQueuedCommand(operation);
        var pending = new RespirePending<T>();
        _ops.Add(new Op<TCommand, T>(operation, command, pending, convert));
        return pending;
    }

    private abstract class Op : RespireConnection.IBatchCommand
    {
        protected Op(string operation) => Operation = operation;

        public string Operation { get; }

        internal ClientSideCacheCoordinator.MutationFence MutationFence;

        public abstract Exception? Error { get; }

        public abstract bool IsCompleted { get; }

        public abstract bool IsReadOnly { get; }

        public abstract bool AllowsReadRouting { get; }

        public abstract ValueTask<RespValue> StartSend(RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool deferFlush);

        public abstract ValueTask<ValueTask<RespValue>> StartOrderedSendAsync(RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline);

        public abstract ValueTask<Exception?> CompleteSendAsync(RespireClient client, ValueTask<RespValue> reply);

        public abstract ValueTask<Task<Exception?>> StartImportAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken);

        public abstract bool TryGetClusterSlot(out int slot);

        public abstract bool? IsCursorContinuation { get; }

        public abstract ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation);

        public abstract Task<Exception?> CompleteClusterSendAsync(
            RespireBatch batch,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation);

        public abstract void Fail(Exception error);
        public abstract bool ReportError();
        public abstract void AddErrorAttempts(int attempts);
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

        public override bool IsReadOnly => command.ReadKind != ReadCommandKind.None
            || command.GetCacheMutation(Operation) == RespireCacheMutation.ReadOnly;

        public override bool AllowsReadRouting => command.ReadKind != ReadCommandKind.None;

        // ARSCAN pages are index ranges without an issuing server cursor. They can follow
        // same-slot writes on the primary while the catalog retains its CursorRead classification.
        public override bool? IsCursorContinuation => command.ReadKind == ReadCommandKind.CursorRead && Operation != "ARSCAN"
            ? CursorCommandMetadata.IsCursorContinuation(in command) : null;

        public override void Fail(Exception error) => pending.Fail(error);
        public override bool ReportError() => pending.ReportError();
        public override void AddErrorAttempts(int attempts) => pending.AddErrorAttempts(attempts);

        public override bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public override ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation)
        {
            return client.SendOnConnectionAsync(Operation, connection, new MutationCommand<TCommand>(command, MutationFence), cancellationToken,
                observation: observation);
        }

        public override async Task<Exception?> CompleteClusterSendAsync(
            RespireBatch batch,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation)
        {
            try
            {
                RespValue value;
                try
                {
                    value = await send.ConfigureAwait(false);
                }
                catch (RespireConnectionRetiredException error) when (batch.ConnectionPolicy.CanReplayRejectedCommands)
                {
                    // Retry only this rejected operation; other pipeline entries may already be accepted.
                    value = await batch._client.ResumeRetiredClusterSendAsync(
                        Operation, new MutationCommand<TCommand>(command, MutationFence), connection, error, readFrom, cancellationToken,
                        observation: observation).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (batch.ConnectionPolicy.CanReplayRejectedCommands
                    && command.TryGetClusterSlot(out var readSlot)
                    && ReadFallbackPolicy.CanFallBackToOtherRole(error, readFrom, readSlot,
                        ReadFallbackPolicy.IsReplicaConnection(connection)))
                {
                    // Complete each operation in queue order; retry only its rejected read.
                    value = await batch._client.ResumeRejectedClusterSendAsync(
                            Operation, new MutationCommand<TCommand>(command, MutationFence), connection, error, readFrom, cancellationToken,
                            observation: observation)
                        .ConfigureAwait(false);
                }
                catch (RespireServerException error) when (
                    batch.ConnectionPolicy.CanRecoverRejectedCommand(error, command.TryGetClusterSlot(out var slot) ? slot : null))
                {
                    value = await batch._client.ResumeRejectedClusterSendAsync(
                            Operation, new MutationCommand<TCommand>(command, MutationFence), connection, error, readFrom, cancellationToken,
                            observation: observation)
                        .ConfigureAwait(false);
                }

                return Complete(batch._client, value);
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return ex;
            }
        }

        public override async ValueTask<Task<Exception?>> StartImportAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                var bound = new MutationCommand<TCommand>(command, MutationFence);
                var reply = client.Core.Circuits is { } circuits
                    ? await QueuedCircuitDispatch.EnqueueAsync(circuits, connection, bound, Operation, cancellationToken).ConfigureAwait(false)
                    : await connection.EnqueuePinnedAsync(bound, cancellationToken, Operation).ConfigureAwait(false);
                return CompleteSendAsync(client, reply).AsTask();
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                return Task.FromResult<Exception?>(ex);
            }
        }

        public override ValueTask<RespValue> StartSend(RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool deferFlush)
        {
            var bound = new MutationCommand<TCommand>(command, MutationFence);
            return connection.SendAsync(in bound, cancellationToken,
                commandName: Operation, observation: observation, deferFlush: deferFlush);
        }

#if NET
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        public override async ValueTask<ValueTask<RespValue>> StartOrderedSendAsync(RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline)
        {
            try
            {
                return await connection.EnqueuePinnedAsync(new MutationCommand<TCommand>(command, MutationFence),
                    cancellationToken, Operation, observation, pinToConnection: false, deadline: deadline).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return ValueTask.FromException<RespValue>(ex);
            }
        }

#if NET
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        public override async ValueTask<Exception?> CompleteSendAsync(RespireClient client, ValueTask<RespValue> reply)
        {
            try
            {
                return Complete(client, await reply.ConfigureAwait(false));
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
