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
/// thread-safe: build, send once, discard. Use <c>await using</c>, as for transactions. Disposing an
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
public sealed partial class RespireBatch : IAsyncDisposable, IDisposable, IRespireCommandQueue, IPendingSink
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
            ErrorObservation.FinishFinal(default, error);
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
        RespireKey key, RespireExpiry expiry, RespireExpireWhen when = RespireExpireWhen.Always)
        => Keys.Expire(key, expiry, when);

    /// <inheritdoc cref="IRespireCommandQueue.Execute"/>
    [Experimental("RSP0005", UrlFormat = "https://thomhurst.github.io/Respire/docs/diagnostics/{0}")]
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
    public ValueTask<RespireBatchResult> TryExecuteAsync(CancellationToken cancellationToken = default)
        => _client.Core.Options.PipelineHook is { } hook
            ? TryExecuteHookedAsync(hook, cancellationToken)
            : TryExecuteCoreAsync(cancellationToken);

    private async ValueTask<RespireBatchResult> TryExecuteCoreAsync(CancellationToken cancellationToken, bool admitted = false)
    {
        using var importUsage = admitted ? null : EnterObservedOperation();
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
            if (_client.UsesPrimaryBacklog && ConnectionPolicy.PinnedConnection is null
                && !_ops.Exists(static op => op.IsCursorRead))
            {
                try
                {
                    connection = _client.SelectPrimaryBacklogConnection(cancellationToken);
                    if (connection is null)
                    {
                        if (core.Sentinel is not null)
                            telemetry = RespireTelemetry.StartBatchOperation("PIPELINE", _ops, static op => op.Operation,
                                core.Options.Database, out telemetryOperation, sentinelStarted);
                        connection = await RunBackloggedBatchAsync(cancellationToken).ConfigureAwait(false);
                        if (core.Sentinel is not null && connection is not null)
                            telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                        var queuedFailures = CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence);
                        telemetry.Complete(core, telemetryOperation, error: queuedFailures is { Length: > 0 } ? queuedFailures[0].Error : null,
                            connection: connection, batchSize: _ops.Count == 1 ? null : _ops.Count);
                        return new RespireBatchResult(_ops.Count, queuedFailures);
                    }
                }
                catch (Exception error)
                {
                    foreach (var op in _ops) op.Fail(error);
                    telemetry.Complete(core, telemetryOperation, error: error, batchSize: _ops.Count == 1 ? null : _ops.Count);
                    return new RespireBatchResult(_ops.Count,
                        CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence));
                }
            }
            if (core.CommandRetries is not null && !ConnectionPolicy.IsImportSession
                && _ops.TrueForAll(static op => !op.IsCursorRead && op.RetryCategory != RespireCommandRetryCategory.Never))
            {
                if (core.Sentinel is not null)
                    telemetry = RespireTelemetry.StartBatchOperation("PIPELINE", _ops, static op => op.Operation,
                        core.Options.Database, out telemetryOperation, sentinelStarted);
                connection = await RunRetryableBatchAsync(cancellationToken, initialConnection: connection).ConfigureAwait(false);
                if (core.Sentinel is not null && connection is not null)
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                var failures = CompleteMutationAndCollectFailures(ref cacheToInvalidate, in mutationFence);
                telemetry.Complete(core, telemetryOperation, error: failures is { Length: > 0 } ? failures[0].Error : null,
                    connection: connection, batchSize: _ops.Count == 1 ? null : _ops.Count);
                return new RespireBatchResult(_ops.Count, failures);
            }
            try
            {
                connection ??= ConnectionPolicy.PinnedConnection ?? await _client.AcquireConnectionAsync(cancellationToken).ConfigureAwait(false);
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
                connection = await RunStandaloneBatchAsync(connection, cancellationToken).ConfigureAwait(false);
                if (core.Sentinel is not null && core.Circuits is not null)
                    telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
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

    private async ValueTask<RespireConnection?> RunBackloggedBatchAsync(CancellationToken cancellationToken)
    {
        var deadline = LogicalCommandRetry.CreateDeadline(_client.Core.Options.CommandTimeout);
        try
        {
            foreach (var op in _ops) op.Snapshot();
            return await _client.RunPrimaryBacklogAsync<RespireConnection?>("PIPELINE", cancellationToken, deadline,
                async (connection, originalDeadline) =>
                {
                    if (_client.Core.CommandRetries is not null
                        && _ops.TrueForAll(static op => op.RetryCategory != RespireCommandRetryCategory.Never))
                        return await RunRetryableBatchAsync(cancellationToken, initialConnection: connection,
                            deadline: originalDeadline).ConfigureAwait(false);
                    return await RunStandaloneBatchAsync(connection, cancellationToken, originalDeadline).ConfigureAwait(false);
                }, _ops[0].Observation).ConfigureAwait(false);
        }
        finally { foreach (var op in _ops) op.ReleaseSnapshot(); }
    }

    private async ValueTask<RespireConnection?> RunRetryableBatchAsync(CancellationToken callerToken, List<Op>? group = null,
        int? slot = null, RespireReadFrom readFrom = RespireReadFrom.Primary,
        RespireConnection? initialConnection = null, CommandDeadline deadline = default)
    {
        var core = _client.Core;
        var operations = group ?? _ops;
        var canBacklogInitialAcquisition = initialConnection is null;
        if (!deadline.IsSet) deadline = LogicalCommandRetry.CreateDeadline(core.Options.CommandTimeout);
        using var timeout = deadline.IsSet
            ? CommandTimeoutCancellation.Create(callerToken, TimeSpan.FromMilliseconds(Math.Max(1, deadline.RemainingMilliseconds)))
            : null;
        var token = timeout?.Token ?? callerToken;
        var sends = ArrayPool<ValueTask<RespValue>>.Shared.Rent(operations.Count);
        var observations = ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(operations.Count);
        var category = RespireCommandRetryCategory.Never;
        for (var i = 0; i < operations.Count; i++)
        {
            observations[i] = operations[i].Observation;
            // Numeric ordering describes risk only; permission is checked separately for every member.
            if (operations[i].RetryCategory > category) category = operations[i].RetryCategory;
        }
        RespireConnection? connection = null;
        var retries = 0;
        try
        {
            callerToken.ThrowIfCancellationRequested();
            // Serialize before any asynchronous route selection. Borrowed arguments may change
            // after execution starts, but every attempt must send the original bytes.
            foreach (var op in operations) op.Snapshot();
            for (var attempt = 1; ; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var outcome = CommandWriteOutcome.DefinitelyUnwritten;
                try
                {
                    if (core.Cluster is { } cluster)
                    {
                        try
                        {
                            if (slot is null && operations.Exists(static operation => operation.Operation is "FLUSHDB" or "FLUSHALL"))
                            {
                                slot = await cluster.GetPrimaryRoutingSlotAsync(token).ConfigureAwait(false);
                                initialConnection = null;
                            }
                            if (attempt > 1) await cluster.WaitForRouteReplacementAsync(slot, token).ConfigureAwait(false);
                            // Retry groups hold no continuations, but a fresh cursor page must still record
                            // the server that issues its cursor. Each attempt revalidates that pin, so the next
                            // page finds the server that answered the final attempt.
                            (connection, _) = attempt == 1 && initialConnection is not null
                                && !operations.Exists(static op => op.IsCursorContinuation.HasValue) ? (initialConnection, null)
                                : await AcquireGroupConnectionsAsync(slot, operations, readFrom, token,
                                    operations[0].Observation).ConfigureAwait(false);
                        }
                        catch (RespireConnectionException error) when (attempt == 1 && canBacklogInitialAcquisition
                            && error is not RespireReconnectLimitException && !operations.Exists(static op => op.IsCursorRead)
                            && cluster.ShouldBacklog(slot, readFrom))
                        {
                            // No attempt was submitted. The queued unit keeps these snapshots and starts
                            // the retry owner only after recovery, without spending a command retry here.
                            await RunBackloggedClusterGroupAsync(slot, operations, readFrom, callerToken, deadline).ConfigureAwait(false);
                            return null;
                        }
                        DeferredOperation.SetAttempts(operations, operations[0].Observation.Attempts);
                        DeferredOperation.ShareEndpoint(operations, operations[0].Observation);
                    }
                    else if (attempt > 1)
                    {
                        await core.EnsureConnectedAsync(token, operations[0].Observation).ConfigureAwait(false);
                        // Reject an open circuit before waiting for a replacement. Disposing
                        // this ignored admission releases half-open capacity without an outcome;
                        // each dispatched member still owns its own permit below.
                        core.Circuits?.Acquire(core.Multiplexer.ActiveConnectionEndpoint, token).Dispose();
                        connection = await core.Multiplexer.GetHealthyConnectionAsync(token).ConfigureAwait(false);
                    }
                    else connection = initialConnection ?? await _client.AcquireConnectionAsync(null, token, operations[0].Observation).ConfigureAwait(false);
                    // Ordered admission has no immutable per-member write evidence. Once any
                    // send starts, treat the complete unit as a possible execution.
                    outcome = CommandWriteOutcome.Unknown;
                    var acceptedRetry = false;
                    // Like the default path, an idle circuit-free connection admits the whole attempt
                    // under one write gate and schedules a single flush, so no producer interleaves.
                    if (core.Cluster is not null)
                    {
                        for (var i = 0; i < operations.Count; i++)
                        {
                            try { sends[i] = operations[i].StartClusterSend(_client, connection, token, observations[i], deadline); }
                            catch (Exception error) { sends[i] = ValueTask.FromException<RespValue>(error); }
                        }
                        acceptedRetry = true;
                    }
                    else if (core.Circuits is null && connection.TryEnqueueMany(operations, sends, observations, token, deadline))
                        acceptedRetry = true;
                    else
                    {
                        for (var i = 0; i < operations.Count; i++)
                        {
                            // Admission failures become faulted reply tasks, so a rejected later
                            // member cannot skip consumption of earlier accepted replies.
                            var accepted = await operations[i].StartOrderedSendAsync(_client, connection, token,
                                observations[i], deadline, core.Circuits).ConfigureAwait(false);
                            sends[i] = accepted.Reply;
                            if (accepted.Connection is { } endpoint)
                            {
                                connection = endpoint;
                                acceptedRetry = true;
                            }
                        }
                    }
                    // Consume every native source once, even when an earlier member fails.
                    for (var i = 0; i < operations.Count; i++)
                    {
                        if (core.Cluster is not null)
                            _ = await operations[i].CompleteClusterSendAsync(this, connection, sends[i], readFrom, token, observations[i], deadline).ConfigureAwait(false);
                        else
                            await operations[i].CaptureReplyAsync(sends[i]).ConfigureAwait(false);
                    }
                    if (attempt > 1 && acceptedRetry && (core.Cluster is null
                        || !operations.TrueForAll(static op => op.AttemptError is RespireCircuitOpenException)))
                    {
                        retries++;
                        RespireTelemetry.RecordCommandRetry(category, "attempted");
                    }
                }
                catch (Exception error)
                {
                    foreach (var op in operations) op.CaptureFailure(error);
                }

                if (!TryGetBatchRetryDelay(operations, attempt, outcome, deadline, token, out var delay)) break;
                var attempts = 0;
                foreach (var op in operations)
                {
                    if (op.AttemptError is { } error) op.Observation.Handled(error);
                    if (op.Observation.Attempts > attempts) attempts = op.Observation.Attempts;
                    op.DiscardReply();
                }
                DeferredOperation.SetAttempts(operations, attempts);
                await core.DelayUnitRetryAsync(delay, token).ConfigureAwait(false);
            }
        }
        catch (Exception error)
        {
            if (error is OperationCanceledException canceled && canceled.CancellationToken == token
                && token.IsCancellationRequested)
                error = callerToken.IsCancellationRequested
                    ? new OperationCanceledException(canceled.Message, canceled, callerToken)
                    : new RespireTimeoutException("PIPELINE", core.Options.CommandTimeout!.Value, canceled);
            foreach (var op in operations) op.CaptureFailure(error);
        }
        finally
        {
            if (core.Cluster is null) DeferredOperation.ShareEndpoint(operations, operations[0].Observation);
            foreach (var op in operations)
            {
                op.NormalizeCancellation(callerToken, token, core.Options.CommandTimeout);
                op.PublishReply(_client);
                op.ReleaseSnapshot();
            }
            if (retries != 0)
                RespireTelemetry.RecordCommandRetry(category, operations.Exists(static op => op.Error is not null) ? "exhausted" : "succeeded");
            ArrayPool<ValueTask<RespValue>>.Shared.Return(sends, clearArray: true);
            ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Return(observations, clearArray: true);
        }
        return connection;
    }

    private bool TryGetBatchRetryDelay(List<Op> operations, int attempt, CommandWriteOutcome outcome, CommandDeadline deadline,
        CancellationToken token, out TimeSpan delay)
    {
        delay = default;
        var remaining = deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue;
        foreach (var failed in operations)
        {
            if (failed.AttemptError is not { } error) continue;
            var permitted = true;
            var candidate = TimeSpan.Zero;
            foreach (var member in operations)
            {
                if (!_client.Core.CommandRetries!.TryGetDelay(error, outcome, member.RetryCategory,
                    attempt, remaining, token, out var memberDelay))
                {
                    permitted = false;
                    break;
                }
                if (memberDelay > candidate) candidate = memberDelay;
            }
            if (!permitted) continue;
            delay = candidate;
            return true;
        }
        return false;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<RespireConnection> RunStandaloneBatchAsync(RespireConnection connection, CancellationToken cancellationToken,
        CommandDeadline deadline = default)
    {
        // Both admission paths include write-gate contention in the original timeout budget.
        if (!deadline.IsSet) deadline = connection.CreateCommandDeadline();
        var sends = ArrayPool<ValueTask<RespValue>>.Shared.Rent(_ops.Count);
        var observations = ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(_ops.Count);
        var executingConnection = connection;
        try
        {
            for (var i = 0; i < _ops.Count; i++)
            {
                observations[i] = _ops[i].Observation;
            }
            var circuits = _client.Core.Circuits;
            // Circuit-enabled queues acquire and observe each permit independently.
            if (circuits is not null || !connection.TryEnqueueMany(_ops, sends, observations, cancellationToken, deadline))
            {
                // Await admission, not replies, so a full ring or credential fence cannot
                // reverse this batch's queue order. The response thread frees ring slots
                // without awaiting these reply tasks. Capacity waits share the same budget.
                for (var i = 0; i < _ops.Count; i++)
                {
                    // Cursor continuations retain the original socket. Ordinary entries and fresh scans follow the
                    // last accepted route so a replacement pool cannot split their wire order.
                    var operationConnection = _ops[i].IsCursorRead ? connection : executingConnection;
                    var accepted = await _ops[i].StartOrderedSendAsync(_client, operationConnection, cancellationToken,
                        observations[i], deadline, circuits).ConfigureAwait(false);
                    sends[i] = accepted.Reply;
                    // A pipeline can span generations. Attribute its scope to the last accepted entry.
                    if (accepted.Connection is { } endpoint) executingConnection = endpoint;
                }
            }
            for (var i = 0; i < _ops.Count; i++)
                _ = await _ops[i].CompleteSendAsync(_client, sends[i]).ConfigureAwait(false);
            return executingConnection;
        }
        finally
        {
            ArrayPool<ValueTask<RespValue>>.Shared.Return(sends, clearArray: true);
            ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Return(observations, clearArray: true);
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
            operation.AllowErrorInspection();
        }
    }

    /// <summary>Discards an unsent batch or preserves completed results, using the same disposal pattern as transactions.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    private async Task RunClusterGroupAsync(
        int? slot,
        List<Op> operations,
        RespireReadFrom readFrom,
        CancellationToken cancellationToken, RespireConnection? initialConnection = null, CommandDeadline deadline = default)
    {
        if (initialConnection is null && !operations.Exists(static op => op.IsCursorRead)
            && _client.Core.Cluster!.ShouldBacklog(slot, readFrom))
        {
            await RunBackloggedClusterGroupAsync(slot, operations, readFrom, cancellationToken, deadline).ConfigureAwait(false);
            return;
        }
        if (_client.Core.CommandRetries is not null
            && operations.TrueForAll(static op => !op.IsCursorRead && op.RetryCategory != RespireCommandRetryCategory.Never))
        {
            _ = await RunRetryableBatchAsync(cancellationToken, operations, slot, readFrom, initialConnection, deadline).ConfigureAwait(false);
            return;
        }
        RespireConnection connection;
        RespireConnection? continuationConnection;
        // Read selection borrows the first pending's existing owner. Copy the completed
        // selection count to the other members before any command starts its own retries.
        var selectionObservation = operations[0].Observation;
        if (!deadline.IsSet && _client.Core.Options.BacklogPolicy == RespireBacklogPolicy.Queue)
            deadline = LogicalCommandRetry.CreateDeadline(_client.Core.Options.CommandTimeout);
        var acquisition = new CommandAcquisitionScope(cancellationToken, deadline, _client.Core.Options.CommandTimeout);
        try
        {
            var resolvePrimary = slot is null && operations.Exists(static operation => operation.Operation is "FLUSHDB" or "FLUSHALL");
            if (resolvePrimary)
                slot = await _client.Core.Cluster!.GetPrimaryRoutingSlotAsync(acquisition.Token).ConfigureAwait(false);
            // Unkeyed selection cannot establish a primary route or a cursor pin.
            (connection, continuationConnection) = initialConnection is not null && !resolvePrimary
                && !operations.Exists(static op => op.IsCursorContinuation.HasValue) ? (initialConnection, null)
                : await AcquireGroupConnectionsAsync(slot, operations, readFrom, acquisition.Token, selectionObservation).ConfigureAwait(false);
            acquisition.CheckDeadline("PIPELINE", _client.Core);
        }
        catch (RespireConnectionException error) when (initialConnection is null
            && error is not RespireReconnectLimitException && !operations.Exists(static op => op.IsCursorRead)
            && _client.Core.Cluster!.ShouldBacklog(slot, readFrom))
        {
            // Acquisition submitted no member. Join once; a draining entry must not queue behind itself.
            acquisition.Dispose();
            await RunBackloggedClusterGroupAsync(slot, operations, readFrom, cancellationToken, deadline).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException cancelled && acquisition.IsDeadlineCancellation(cancelled))
                ex = acquisition.CreateTimeout("PIPELINE", _client.Core, cause: cancelled);
            else if (ex is OperationCanceledException callerCancelled && acquisition.IsCallerCancellation(callerCancelled))
                ex = new OperationCanceledException(callerCancelled.Message, callerCancelled, cancellationToken);
            DeferredOperation.SetAttempts(operations, selectionObservation.Attempts);
            foreach (var operation in operations)
            {
                operation.Fail(ex);
            }

            return;
        }
        finally { acquisition.Dispose(); }
        var selectionAttempts = selectionObservation.Attempts;
        if (selectionAttempts != 0) DeferredOperation.SetAttempts(operations, selectionAttempts);

        var sends = new ValueTask<RespValue>[operations.Count];
        var observations = ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Rent(operations.Count);
        try
        {
            // Start every send in queue order before awaiting responses to retain pipelining.
            for (var i = 0; i < operations.Count; i++)
            {
                observations[i] = operations[i].Observation;
                try
                {
                    var operationConnection = operations[i].IsCursorContinuation == true ? continuationConnection ?? connection : connection;
                    sends[i] = operations[i].StartClusterSend(_client, operationConnection, cancellationToken, observations[i], deadline);
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
                        this, operationConnection, sends[i], readFrom, cancellationToken, observations[i], deadline)
                    .ConfigureAwait(false);
                if (initialConnection is not null) operations[i].PublishReply(_client);
            }
        }
        finally { ArrayPool<RespireTelemetry.ErrorObservation>.Shared.Return(observations, clearArray: true); }
    }

    private async Task RunBackloggedClusterGroupAsync(int? slot, List<Op> operations, RespireReadFrom readFrom,
        CancellationToken cancellationToken, CommandDeadline deadline)
    {
        if (!deadline.IsSet) deadline = LogicalCommandRetry.CreateDeadline(_client.Core.Options.CommandTimeout);
        try
        {
            foreach (var op in operations) op.Snapshot();
            await _client.RunClusterBacklogAsync("PIPELINE", slot, readFrom, cancellationToken, deadline,
                operations[0].Observation, async (ready, originalDeadline) =>
                {
                    await RunClusterGroupAsync(slot, operations, readFrom, cancellationToken, ready, originalDeadline).ConfigureAwait(false);
                    return true;
                }).ConfigureAwait(false);
        }
        catch (Exception error) { foreach (var op in operations) op.Fail(error); }
        finally { foreach (var op in operations) op.ReleaseSnapshot(); }
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
            var primary = await _client.AcquireConnectionAsync(slot, cancellationToken, RespireReadFrom.Primary, observation).ConfigureAwait(false);
            if (!ReferenceEquals(connection.Multiplexer, primary.Multiplexer)
                || (continuationConnection is not null && !ReferenceEquals(continuationConnection.Multiplexer, primary.Multiplexer)))
            {
                // Local validation rejects the group before submission, so no node owns this failure.
                observation.UseEndpoint(null);
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
        // Fence completion takes priority over publication, including if cleanup throws.
        cache?.CompleteMutation(in fence);
        cache = null;
        return CollectFailures(_ops);
    }

    private static RespireBatchFailure[]? CollectFailures(List<Op> operations, bool reportErrors = true)
    {
        var failureCount = reportErrors ? DeferredOperation.ReportErrors(operations) : 0;
        if (!reportErrors)
        {
            foreach (var operation in operations)
            {
                if (operation.Error is not null) failureCount++;
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

        ConnectionPolicy.ValidateQueuedCommand(operation);
        var pending = new RespirePending<T>();
        _ops.Add(new Op<TCommand, T>(operation, command, pending, convert));
        return pending;
    }

    private abstract class Op(string operation) : DeferredOperation(operation), RespireConnection.IBatchCommand
    {
        internal ClientSideCacheCoordinator.MutationFence MutationFence;
        // Only retry-enabled execution retains replies and snapshot state. Default batches
        // keep their existing native reply completion path.
        private RetryAttempt? _attempt;
        private sealed class RetryAttempt
        {
            internal RespValue Reply;
            internal bool HasReply;
            internal Exception? Error;
            internal ImmediateRetryState? Snapshot;
            internal Exception? SnapshotError;
        }
        internal Exception? AttemptError => _attempt?.Error;
        protected ImmediateRetryState? SnapshotState => _attempt?.Snapshot;
        protected Exception? SnapshotError => _attempt?.SnapshotError;

        public abstract RespireCommandRetryCategory RetryCategory { get; }
        protected abstract ImmediateRetryState SnapshotArguments();
        protected abstract void PublishResponse(RespireClient client, RespValue value);

        internal void Snapshot()
        {
            if (_attempt is not null) return;
            _attempt = new();
            try { _attempt.Snapshot = SnapshotArguments(); }
            catch (Exception error) { _attempt.SnapshotError = error; }
        }

        internal void ReleaseSnapshot()
        {
            _attempt?.Snapshot?.Return();
            _attempt = null;
        }

        internal async ValueTask CaptureReplyAsync(ValueTask<RespValue> reply)
        {
            try
            {
                _attempt!.Reply = await reply.ConfigureAwait(false);
                _attempt.HasReply = true;
                _attempt.Error = _attempt.Reply.IsError ? ResponseReader.ServerError(in _attempt.Reply, Operation) : null;
            }
            catch (Exception error) { (_attempt ??= new()).Error = error; }
        }

        internal void DiscardReply()
        {
            if (_attempt is null) return;
            if (_attempt.HasReply) _attempt.Reply.Dispose();
            _attempt.Reply = default;
            _attempt.HasReply = false;
            _attempt.Error = null;
        }

        internal void CaptureFailure(Exception error)
        {
            DiscardReply();
            (_attempt ??= new()).Error = error;
        }

        internal void NormalizeCancellation(CancellationToken callerToken, CancellationToken token, TimeSpan? timeout)
        {
            if (AttemptError is not OperationCanceledException error || error.CancellationToken != token
                || !token.IsCancellationRequested) return;
            _attempt!.Error = callerToken.IsCancellationRequested
                ? new OperationCanceledException(error.Message, error, callerToken)
                : timeout is { } duration ? new RespireTimeoutException(Operation, duration, error) : error;
        }

        internal void PublishReply(RespireClient client)
        {
            if (AttemptError is { } error)
            {
                Fail(error);
                DiscardReply();
            }
            else if (_attempt is { HasReply: true })
            {
                var value = _attempt.Reply;
                _attempt.Reply = default;
                _attempt.HasReply = false;
                PublishResponse(client, value);
            }
        }

        public abstract Exception? Error { get; }

        public abstract bool IsReadOnly { get; }

        public abstract bool AllowsReadRouting { get; }
        public abstract bool IsCursorRead { get; }

        public abstract ValueTask<RespValue> StartSend(RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool deferFlush,
            CommandDeadline deadline);

        public abstract ValueTask<(ValueTask<RespValue> Reply, RespireConnection? Connection)> StartOrderedSendAsync(RespireClient client, RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline,
            StandaloneCircuitRegistry? circuits);

        public abstract ValueTask<Exception?> CompleteSendAsync(RespireClient client, ValueTask<RespValue> reply);

        public abstract ValueTask<Task<Exception?>> StartImportAsync(
            RespireClient client, RespireConnection connection, CancellationToken cancellationToken);

        public abstract bool TryGetClusterSlot(out int slot);

        public abstract bool? IsCursorContinuation { get; }

        public abstract ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation, CommandDeadline deadline = default);
        public abstract Task<Exception?> CompleteClusterSendAsync(
            RespireBatch batch,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation, CommandDeadline deadline = default);

    }

    private sealed class Op<TCommand, T>(
        string operation,
        TCommand command,
        RespirePending<T> pending,
        Func<RespireClient, RespValue, T> convert) : Op(operation)
        where TCommand : struct, IRespCommand
    {
        public override RespireCommandRetryCategory RetryCategory => command.RetryCategory;
        protected override ImmediateRetryState SnapshotArguments() => ImmediateRetryState.Rent(in command);
        protected override void PublishResponse(RespireClient client, RespValue value) => Complete(client, value);

        public override Exception? Error => pending.Error;

        public override bool IsCompleted => pending.IsCompleted;

        public override bool IsReadOnly => command.ReadKind != ReadCommandKind.None
            || command.GetCacheMutation(Operation) == RespireCacheMutation.ReadOnly;

        public override bool AllowsReadRouting => command.ReadKind != ReadCommandKind.None;
        public override bool IsCursorRead => CursorCommandMetadata.IsServerCursorContinuation(in command);

        // ARSCAN pages are index ranges without an issuing server cursor. They can follow
        // same-slot writes on the primary while the catalog retains its CursorRead classification.
        public override bool? IsCursorContinuation => command.ReadKind == ReadCommandKind.CursorRead && Operation != "ARSCAN"
            ? CursorCommandMetadata.IsCursorContinuation(in command) : null;

        public override void Fail(Exception error) => pending.Fail(error);
        public override bool ReportError() => pending.ReportError();
        public override void AllowErrorInspection() => pending.AllowErrorInspection();
        public override RespireTelemetry.ErrorObservation Observation => pending.Observation;

        public override bool TryGetClusterSlot(out int slot) => command.TryGetClusterSlot(out slot);

        public override ValueTask<RespValue> StartClusterSend(
            RespireClient client,
            RespireConnection connection,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation, CommandDeadline deadline = default)
        {
            var bound = new MutationCommand<TCommand>(command, MutationFence);
            if (SnapshotError is { } error) return ValueTask.FromException<RespValue>(error);
            if (SnapshotState is { } snapshot)
                return client.SendOnConnectionAsync(Operation, connection, new SnapshotCommand<TCommand>(bound, snapshot), cancellationToken,
                    observation: observation, commandDeadline: deadline);
            return client.SendOnConnectionAsync(Operation, connection, bound, cancellationToken,
                observation: observation, commandDeadline: deadline);
        }

        public override async Task<Exception?> CompleteClusterSendAsync(
            RespireBatch batch,
            RespireConnection connection,
            ValueTask<RespValue> send,
            RespireReadFrom readFrom,
            CancellationToken cancellationToken,
            RespireTelemetry.ErrorObservation observation, CommandDeadline deadline = default)
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
                    value = await ResumeAsync(error, retired: true).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (batch.ConnectionPolicy.CanReplayRejectedCommands
                    && command.TryGetClusterSlot(out var readSlot)
                    && ReadFallbackPolicy.CanFallBackToOtherRole(error, readFrom, readSlot,
                        ReadFallbackPolicy.IsReplicaConnection(connection)))
                {
                    // Complete each operation in queue order; retry only its rejected read.
                    value = await ResumeAsync(error, retired: false).ConfigureAwait(false);
                }
                catch (RespireServerException error) when (
                    batch.ConnectionPolicy.CanRecoverRejectedCommand(error, command.TryGetClusterSlot(out var slot) ? slot : null))
                {
                    value = await ResumeAsync(error, retired: false).ConfigureAwait(false);
                }

                if (SnapshotState is not null || SnapshotError is not null)
                {
                    await CaptureReplyAsync(new ValueTask<RespValue>(value)).ConfigureAwait(false);
                    return AttemptError;
                }
                return Complete(batch._client, value);
            }
            catch (Exception ex)
            {
                if (SnapshotState is not null || SnapshotError is not null) CaptureFailure(ex);
                else pending.Fail(ex);
                return ex;
            }

            ValueTask<RespValue> ResumeAsync(Exception error, bool retired)
            {
                var bound = new MutationCommand<TCommand>(command, MutationFence);
                if (SnapshotState is { } snapshot)
                    return ResumeCommandAsync(new SnapshotCommand<TCommand>(bound, snapshot), error, retired);
                return ResumeCommandAsync(bound, error, retired);
            }

            ValueTask<RespValue> ResumeCommandAsync<TReplay>(TReplay replay, Exception error, bool retired) where TReplay : struct, IRespCommand
                => retired
                    ? batch._client.ResumeRetiredClusterSendAsync(Operation, replay, connection,
                        (RespireConnectionRetiredException)error, readFrom, cancellationToken, observation: observation, deadline: deadline)
                    : batch._client.ResumeRejectedClusterSendAsync(Operation, replay, connection,
                        (RespireServerException)error, readFrom, cancellationToken, observation: observation, deadline: deadline);
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
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, bool deferFlush,
            CommandDeadline deadline)
        {
            var bound = new MutationCommand<TCommand>(command, MutationFence);
            // Retryable batches bulk-admit their serialized snapshot so every attempt sends the original bytes.
            if (SnapshotError is { } error) return ValueTask.FromException<RespValue>(error);
            if (SnapshotState is { } snapshot)
            {
                var replay = new SnapshotCommand<TCommand>(bound, snapshot);
                return connection.SendAsync(in replay, cancellationToken,
                    commandName: Operation, observation: observation, commandDeadline: deadline, deferFlush: deferFlush);
            }
            return connection.SendAsync(in bound, cancellationToken,
                commandName: Operation, observation: observation, commandDeadline: deadline, deferFlush: deferFlush);
        }

#if NET
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
        public override async ValueTask<(ValueTask<RespValue> Reply, RespireConnection? Connection)> StartOrderedSendAsync(RespireClient client, RespireConnection connection,
            CancellationToken cancellationToken, RespireTelemetry.ErrorObservation observation, CommandDeadline deadline,
            StandaloneCircuitRegistry? circuits)
        {
            try
            {
                var bound = new MutationCommand<TCommand>(command, MutationFence);
                if (SnapshotError is { } error) throw error;
                if (SnapshotState is { } snapshot)
                {
                    var replay = new SnapshotCommand<TCommand>(bound, snapshot);
                    if (circuits is not null)
                        return await QueuedCircuitDispatch.EnqueueWithConnectionAsync(circuits, connection, replay, Operation,
                            cancellationToken, observation, deadline, client).ConfigureAwait(false);
                    return (await connection.EnqueuePinnedAsync(replay, cancellationToken, Operation, observation,
                        pinToConnection: false, deadline: deadline).ConfigureAwait(false), connection);
                }
                if (circuits is not null)
                {
                    return await QueuedCircuitDispatch.EnqueueWithConnectionAsync(circuits, connection, bound, Operation,
                        cancellationToken, observation, deadline, client).ConfigureAwait(false);
                }
                return (await connection.EnqueuePinnedAsync(bound, cancellationToken, Operation, observation,
                    pinToConnection: false, deadline: deadline).ConfigureAwait(false), connection);
            }
            catch (Exception ex)
            {
                return (ValueTask.FromException<RespValue>(ex), null);
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

    private readonly struct SnapshotCommand<TCommand>(MutationCommand<TCommand> command, ImmediateRetryState snapshot) : IRespCommandWrapper
        where TCommand : struct, IRespCommand
    {
        public void Write(ref RespWriter writer) => writer.WriteRaw(snapshot.Frame.Span);
        public int GetWriteSizeHint() => snapshot.Frame.Length;
        public void ValidateAdmission() => command.ValidateAdmission();
        public void OnAccepted() => command.OnAccepted();
        public CancellationToken GetResponseCancellationToken(CancellationToken token) => command.GetResponseCancellationToken(token);
        public ClientSideCacheCoordinator.MutationFence GetMutationFence() => command.GetMutationFence();
        public ReadCommandKind ReadKind => command.ReadKind;
        public RespireCommandRetryCategory RetryCategory => command.RetryCategory;
        public bool IsConnectionProtocol => command.IsConnectionProtocol;
        public RespireCacheMutation GetCacheMutation(string operation) => command.GetCacheMutation(operation);
        public ClientCacheCommandMetadata GetClientCacheMetadata(string operation) => command.GetClientCacheMetadata(operation);
        public int CursorArgumentIndex => command.CursorArgumentIndex;
        public bool TryGetArgument(int index, out RespireValue value) => command.TryGetArgument(index, out value);
        public bool TryGetPrimaryKey(out RespireValue key) => command.TryGetPrimaryKey(out key);
        public bool TryGetClusterSlot(out int slot) => snapshot.TryGetClusterSlot(out slot);
    }
}
