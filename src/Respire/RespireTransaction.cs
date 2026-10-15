using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;
using Respire.Serialization;

namespace Respire;

/// <summary>
/// Shared command-queue surface for MULTI/EXEC transactions. Commands serialize immediately
/// into a pooled buffer and return <see cref="RespirePending{T}"/> values completed by commit.
/// </summary>
/// <remarks>
/// Commands are grouped into the same facets as the client and a batch — see
/// <see cref="RespireBatch"/> for what the deferred surface leaves out.
/// Single-shot and not thread-safe: build, commit once, discard. Always commit or dispose a
/// transaction so its buffer is released. Concrete transactions expose different commit results:
/// <see cref="RespireTransaction"/> cannot abort, while <see cref="RespireWatchedTransaction"/>
/// reports a WATCH abort.
/// </remarks>
public abstract partial class RespireTransactionBase : IAsyncDisposable, IRespireCommandQueue, IPendingSink
{
    private readonly RespireClient _client;
    private readonly RespireHashImportSession? _importSession;
    private QueuedConnectionPolicy ConnectionPolicy => new(_importSession, _watchConnection);
    private readonly RespireConnection? _watchConnection;
    private readonly WriteBuffer _buffer = new(1024);
    private readonly List<TxOp> _ops = [];
    private int _clusterSlot;
    private bool _hasClusterSlot;
    // A queued cursor continuation is server-local, so a retired generation cannot replay it elsewhere.
    private bool _hasCursorContinuation;
    private bool _completed;
    private RetryCategorySet _retryCategories;

    // The transport records the accepting socket after any pre-admission maintenance reroute.
    internal RespireConnection? ExecutingConnection { get; set; }

    private protected virtual WatchedTransactionRetryContext? RetryContext => null;

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

    internal RespireTransactionBase(RespireClient client, RespireConnection? watchConnection,
        int? watchSlot = null, RespireHashImportSession? importSession = null)
    {
        _client = client;
        _importSession = importSession;
        _watchConnection = watchConnection;
        ApplyClusterSlot(watchSlot);
    }

    /// <summary>The number of commands queued for the transaction.</summary>
    public int Count => _ops.Count;

    // Deferred command facets, grouped exactly like the client's — and the same interfaces a
    // batch exposes, so helper code can queue into either. Created on first use.

    /// <summary>String (plain value) commands. Redis: GET, SET, INCR, …</summary>
    public IBatchStringCommands Strings => _strings ??= new BatchStringCommands(this);

    /// <summary>Generic key management commands. Redis: DEL, EXPIRE, TYPE, …</summary>
    public IBatchKeyCommands Keys => _keys ??= new BatchKeyCommands(this);

    /// <summary>Server flush commands affecting only this transaction's node.</summary>
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
    [Experimental("RSP0005", UrlFormat = "https://thomhurst.github.io/Respire/docs/diagnostics/{0}")]
    public RespirePending<RespireResult> Execute(RespireCommand command, params RespireValue[] args)
        => DeferredRawCommands.Enqueue(this, command, args);

    RespireClient IPendingSink.Client => _client;

    RespireHashImportSession? IPendingSink.ImportSession => _importSession;

    bool IPendingSink.DefersSerialization => false;

    // Multi-key validation is read-only. Add applies the command's representative routing slot
    // only after serialization succeeds, so a rejected command cannot pin the transaction.
    private void ValidateClusterKeys(ReadOnlySpan<RespireKey> keys)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        foreach (ref readonly var key in keys)
        {
            ValidateClusterKey(in key, ref slot);
        }
    }

    private void ValidateClusterKeys(RespireKey first, RespireKey second)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        ValidateClusterKey(in first, ref slot);
        ValidateClusterKey(in second, ref slot);
    }

    private void ValidateClusterKeys(RespireKey first, ReadOnlySpan<RespireKey> rest)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        ValidateClusterKey(in first, ref slot);
        foreach (ref readonly var key in rest)
        {
            ValidateClusterKey(in key, ref slot);
        }
    }

    private void ValidateClusterKeys(ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        foreach (ref readonly var pair in pairs)
        {
            ValidateClusterKey(in pair.Key, ref slot);
        }
    }

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, ReadOnlySpan<RespireKey> keys,
        Func<RespireClient, RespValue, T> convert)
    {
        ValidateClusterKeys(keys);
        return Add<TCommand, T>(operation, in command, convert);
    }

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, RespireKey second,
        Func<RespireClient, RespValue, T> convert)
    {
        ValidateClusterKeys(first, second);
        return Add<TCommand, T>(operation, in command, convert);
    }

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, RespireKey first, ReadOnlySpan<RespireKey> rest,
        Func<RespireClient, RespValue, T> convert)
    {
        ValidateClusterKeys(first, rest);
        return Add<TCommand, T>(operation, in command, convert);
    }

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command,
        ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs,
        Func<RespireClient, RespValue, T> convert)
    {
        ValidateClusterKeys(pairs);
        return Add<TCommand, T>(operation, in command, convert);
    }

    /// <summary>Executes the shared transaction path and reports a watched abort.</summary>
    private protected ValueTask<bool> CommitCoreAsync(CancellationToken cancellationToken, bool validateEmptyWatch = false)
        => _client.Core.Options.PipelineHook is { } hook
            ? CommitHookedAsync(hook, cancellationToken, validateEmptyWatch)
            : CommitWithoutHookAsync(cancellationToken, validateEmptyWatch);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<bool> CommitWithoutHookAsync(CancellationToken cancellationToken, bool validateEmptyWatch,
        PipelineHookScope? hook = null)
    {
        var observation = _ops.Count == 0 ? default : _ops[0].Observation;
        using var importUsage = hook is null ? PrepareCommit() : null;
        var core = _client.Core;
        var telemetryOperation = "MULTI";
        var sentinelStarted = core.Sentinel is null ? default : RespireTelemetry.CaptureBatchStart("MULTI", _ops, static op => op.Operation);
        var telemetry = core.Sentinel is null ? RespireTelemetry.StartBatchOperation(
            "MULTI",
            _ops,
            static op => op.Operation,
            core.Endpoint,
            core.Options.Database,
            out telemetryOperation) : default;
        RespireConnection? connection = ConnectionPolicy.PinnedConnection;
        var timeout = core.Options.CommandTimeout;
        var deadline = RetryContext?.Deadline ?? (timeout is { } duration
            ? CommandDeadline.After(Math.Max(1L, (long)duration.TotalMilliseconds)) : default);
        Exception? operationError = null;
        Exception? importError = null;
        var importTransactionStarted = false;
        RespireConnection.CredentialSequenceLease credentialSequence = default;
        var returnWatchConnection = false;
        var sendAttempted = false;
        var mutationFence = default(ClientSideCacheCoordinator.MutationFence);
        try
        {
            if (_ops.Count == 0 && (!validateEmptyWatch || _watchConnection is null))
            {
                // Nonempty commits use SendAsync's before-submission classification.
                // Empty imports must preserve their fieldsets on cancellation too.
                if (ConnectionPolicy.IsImportSession && cancellationToken.IsCancellationRequested)
                    throw new RespireCommandNotSubmittedException(new OperationCanceledException(cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                if (core.Sentinel is not null)
                {
                    telemetry = RespireTelemetry.StartBatchOperation(
                        "MULTI", _ops, static op => op.Operation, core.Options.Database,
                        out telemetryOperation, sentinelStarted);
                }
                return true;
            }

            // MULTI/EXEC bypasses the regular send path and can contain arbitrary mutations.
            if (_ops.Count != 0 && core.ClientCache is { } cache) mutationFence = cache.BeginUnknownMutation();

            RespValue result;
            try
            {
                // The connection sweep owns EXEC's timeout. Acquisition alone needs a timer
                // when it cannot use a ready connection; both stages share one absolute budget.
                if (connection is null && !_hasCursorContinuation && _client.UsesPrimaryBacklog)
                    connection = _client.SelectPrimaryBacklogConnection(cancellationToken);
                result = connection is null && !_hasCursorContinuation && _client.UsesPrimaryBacklog
                    ? await _client.RunPrimaryBacklogAsync("MULTI/EXEC", cancellationToken, deadline,
                        (ready, originalDeadline) =>
                        {
                            connection = ready;
                            deadline = originalDeadline;
                            return SendAsync();
                        }, observation).ConfigureAwait(false)
                    : await SendAsync().ConfigureAwait(false);

                // SendTransactionAsync drains through EXEC before completing, including when it
                // returns a queue error or a null watched-abort reply. Redis has therefore cleared
                // WATCH and the dedicated connection is safe to pool again.
                returnWatchConnection = ReferenceEquals(connection, _watchConnection);
            }
            catch (Exception ex)
            {
                operationError = ex;
                // The commit never produced a reply (connection loss, timeout, cancellation, …):
                // every pending must observe that failure, not a stale "not committed yet" state.
                foreach (var op in _ops)
                {
                    op.Fail(ex);
                }

                throw;
            }

            if (result.IsError)
            {
                var error = ResponseReader.ServerError(in result, "MULTI/EXEC", connection is null ? null : new(connection.Host, connection.Port));
                operationError = error;
                result.Dispose();
                foreach (var op in _ops)
                {
                    op.Fail(error);
                }

                throw error;
            }

            if (result.IsNull)
            {
                result.Dispose();
                if (ConnectionPolicy.IsImportSession || this is RespireTransaction)
                {
                    var error = new RespireProtocolException(ConnectionPolicy.IsImportSession
                        ? "An unwatched hash import EXEC unexpectedly returned a null reply."
                        : "An unwatched EXEC unexpectedly returned a null reply.");
                    operationError = error;
                    foreach (var op in _ops) op.Fail(error);
                    throw error;
                }
                foreach (var op in _ops)
                {
                    op.Abort();
                }

                return false;
            }

            if (ConnectionPolicy.IsImportSession && result.Type != RespDataType.Array)
            {
                var error = new RespireProtocolException("A hash import EXEC must return an array reply.");
                operationError = error;
                result.Dispose();
                foreach (var op in _ops) op.Fail(error);
                throw error;
            }
            var elements = result.AsArray();
            if (ConnectionPolicy.IsImportSession && elements.Length != _ops.Count)
            {
                var error = new RespireProtocolException($"EXEC returned {elements.Length} results for {_ops.Count} queued commands.");
                operationError = error;
                result.Dispose();
                foreach (var op in _ops) op.Fail(error);
                throw error;
            }
            var elementCount = elements.Length;
            var completeCount = Math.Min(_ops.Count, elementCount);
            Dictionary<string, bool>? missingEngines = null;
            for (var i = 0; i < completeCount; i++)
            {
                var element = result.AsArray()[i];
                Exception? itemError;
                if (element.IsError && connection is not null
                    && ScriptingEngineInfo.IsScriptingCommand(_ops[i].Operation))
                {
                    var serverError = ResponseReader.ServerError(in element, _ops[i].Operation, new(connection.Host, connection.Port));
                    var engine = ScriptingEngineInfo.MissingEngine(serverError, _ops[i].ExpectedEngine);
                    itemError = serverError;
                    if (engine is not null)
                    {
                        missingEngines ??= new(StringComparer.OrdinalIgnoreCase);
                        if (missingEngines.TryGetValue(engine, out var missing))
                        {
                            if (missing) itemError = new RespireScriptingEngineUnavailableException(engine,
                                new RespireEndpoint(connection.Host, connection.Port), serverError);
                        }
                        else
                        {
                            // EXEC already ran. One observation per engine bounds diagnostics for
                            // this reply only, including unknown/denied/timed-out probes.
                            itemError = await connection.ClassifyScriptingErrorAsync(serverError,
                                _ops[i].ExpectedEngine, CancellationToken.None, deadline).ConfigureAwait(false);
                            missingEngines.Add(engine, itemError is RespireScriptingEngineUnavailableException);
                        }
                    }
                    _ops[i].Fail(itemError);
                }
                else itemError = _ops[i].Complete(_client, in element);
                operationError ??= itemError;
                if (itemError is not null && ConnectionPolicy.RequiresExpiration(itemError))
                    importError ??= itemError;
            }

            if (completeCount < _ops.Count)
            {
                var mismatch = new RespireProtocolException(
                    $"EXEC returned {elementCount} results for {_ops.Count} queued commands.");
                operationError ??= mismatch;
                for (var i = completeCount; i < _ops.Count; i++)
                {
                    _ops[i].Fail(mismatch);
                }
            }

            result.Dispose();
            return true;
        }
        catch (Exception error)
        {
            operationError ??= error;
            foreach (var op in _ops)
                if (!op.IsCompleted) op.Fail(error);
            throw;
        }
        finally
        {
            try
            {
                if (operationError is not null)
                {
                    await ConnectionPolicy.ExpireAsync(importError ?? operationError,
                        transactionStateUncertain: importTransactionStarted).ConfigureAwait(false);
                }
            }
            finally
            {
                credentialSequence.Dispose();
                core.ClientCache?.CompleteMutation(in mutationFence);
            }

            try
            {
                await ReleaseAsync(returnWatchConnection).ConfigureAwait(false);
            }
            catch (Exception) when (operationError is not null)
            {
                // The pool reports cleanup failures. Preserve the original transaction failure.
            }
            catch (Exception ex)
            {
                operationError ??= ex;
                throw;
            }
            finally
            {
                hook?.Fail(operationError);
                var attempts = observation.Attempts;
                // An empty WATCH commit has no queued owner, but still sends MULTI/EXEC on its
                // pinned connection. Capture the node before ReportErrors closes the owners; an
                // empty commit that returned early (for example on cancellation) contacted no node.
                RespireEndpoint? finalEndpoint = observation.IsEmpty
                    ? sendAttempted && connection is { } attempted
                        ? new RespireEndpoint(attempted.Host, attempted.Port) : (RespireEndpoint?)null
                    : observation.Endpoint;
                DeferredOperation.ShareEndpoint(_ops, observation);
                var pendingErrors = DeferredOperation.ReportErrors(_ops, attempts) != 0;
                if (operationError is not null && !pendingErrors)
                    ErrorObservation.FinishFinal(default, operationError, retryAttempts: attempts, endpoint: finalEndpoint);
                if (connection is null && operationError is not null)
                    RespireTelemetry.RecordUnroutedBatchFailure("MULTI", _ops, static op => op.Operation,
                        core.Options.Database, sentinelStarted, operationError);
                if (core.Sentinel is not null && _ops.Count == 0 && (!validateEmptyWatch || _watchConnection is null))
                {
                    telemetry.Complete(telemetryOperation, host: null, port: 6379,
                        database: core.Options.Database, error: operationError, batchSize: 0);
                }
                else
                {
                    telemetry.Complete(
                        core,
                        telemetryOperation,
                        error: operationError,
                        connection: connection,
                        batchSize: _ops.Count == 1 ? null : _ops.Count);
                }
            }
        }

        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        async ValueTask<RespValue> SendAsync()
        {
            var slot = _hasClusterSlot ? _clusterSlot : (int?)null;
            var acquisition = new CommandAcquisitionScope(cancellationToken, deadline, timeout);
            var importSubmissionAttempted = false;
            ClusterRouter.DiscoveryRound? discovery = null;
            var discoveryPending = false;
            var sentinelTelemetryStarted = false;
            var retryUnit = CanRetryUnit(core);
            var unitAttempts = 0;
            var unitResendPending = false;
            var routingAttempt = 0;
            var retryTelemetry = retryUnit ? new UnitRetryTelemetry(_retryCategories.Label) : null;
            Action? onUnitAccepted = retryTelemetry is null ? null : retryTelemetry.Accepted;
            try
            {
                var cluster = core.Cluster;
                acquisition.CheckDeadline("MULTI/EXEC", core, ConnectionPolicy.ImportConnection);
                if (slot is null && cluster is { } flushCluster
                    && _ops.Exists(static operation => operation.Operation is "FLUSHDB" or "FLUSHALL"))
                    slot = await flushCluster.GetPrimaryRoutingSlotAsync(acquisition.Token).ConfigureAwait(false);
                for (;;)
                {
                    connection ??= await _client.AcquireConnectionAsync(slot, ref acquisition, observation).ConfigureAwait(false);
                    acquisition.Dispose();
                    acquisition.CheckDeadline("MULTI/EXEC", core, ConnectionPolicy.ImportConnection);
                    if (core.Sentinel is not null)
                    {
                        if (!sentinelTelemetryStarted)
                        {
                            telemetry = RespireTelemetry.StartBatchOperation(
                                "MULTI", _ops, static op => op.Operation,
                                connection.Host, connection.Port, core.Options.Database, out telemetryOperation, sentinelStarted);
                            sentinelTelemetryStarted = true;
                        }
                        else telemetry.UpdateServerEndpoint(connection.Host, connection.Port);
                    }
                    RespValue reply = default;
                    CircuitAdmission admission = default;
                    var outcome = CommandWriteOutcome.DefinitelyUnwritten;
                    TimeSpan? retryDelay = null;
                    var unitAttemptCounted = false;
                    try
                    {
                        // The entire MULTI/EXEC sequence owns one endpoint permit. Pin transport
                        // acceptance so a maintenance replacement cannot bypass its own circuit.
                        if (core.Circuits is { } circuits)
                        {
                            connection.ThrowIfRetired();
                            admission = circuits.Acquire(new(connection.Host, connection.Port), cancellationToken);
                        }
                        if (ConnectionPolicy.IsImportSession)
                        {
                            if (!connection.TryAcquireCredentialSequence(cancellationToken, out credentialSequence))
                                credentialSequence = await connection.AcquireCredentialSequenceAsync(
                                    acquisition.Token).ConfigureAwait(false);
                            acquisition.Dispose();
                            acquisition.CheckDeadline("MULTI/EXEC", core, connection);
                            // This lease is exclusive: confirm MULTI before any import can
                            // reach Redis, including when ACLs allow HIMPORT but deny MULTI.
                            importSubmissionAttempted = true;
                            sendAttempted = true;
                            // Import-only MULTI cannot reroute; keep this decision in the shared policy.
                            using var multi = await _client.SendOnConnectionAsync("MULTI", connection,
                                new ProtocolCommand<Cmd>(new Cmd(RespireCommands.Transaction.MULTI.Verb)), cancellationToken, commandDeadline: deadline,
                                allowStreamingConnectionReroute: ConnectionPolicy.CanReplayRejectedCommands,
                                pinToConnection: core.Circuits is not null).ConfigureAwait(false);
                            ResponseReader.ExpectOk(in multi);
                            importTransactionStarted = true;
                        }
                        // Only reaching transport submission lets an empty commit name this node. Local
                        // capacity validation submits nothing, so run it before marking the send.
                        try { connection.ValidateTransactionCapacity(_ops.Count, includeMulti: !ConnectionPolicy.IsImportSession); }
                        catch { observation.UseEndpoint(null); throw; }
                        sendAttempted = true;
                        if (retryUnit)
                        {
                            unitAttempts++;
                            unitAttemptCounted = true;
                            var acceptedResendsBefore = retryTelemetry!.AcceptedResends;
                            var attempted = await connection.SendTransactionAttemptAsync(_buffer.WrittenMemory, _ops.Count,
                                cancellationToken, deadline, this, observation, mutationFence,
                                pinToConnection: core.Circuits is not null,
                                onAccepted: unitResendPending ? onUnitAccepted : null).ConfigureAwait(false);
                            // Acceptance runs under the transport write gate; publish only after leaving it.
                            retryTelemetry.PublishAccepted();
                            if (retryTelemetry.AcceptedResends != acceptedResendsBefore) unitResendPending = false;
                            outcome = attempted.WriteOutcome;
                            reply = attempted.GetResult();
                        }
                        else
                        {
                            reply = await connection.SendTransactionAsync(_buffer.WrittenMemory, _ops.Count,
                                    cancellationToken, includeMulti: !ConnectionPolicy.IsImportSession, commandDeadline: deadline,
                                    transaction: this, observation: observation, mutationFence: mutationFence,
                                    pinToConnection: core.Circuits is not null, writeObservation: RetryContext?.WriteObservation)
                                .ConfigureAwait(false);
                        }
                        admission.Success();
                        connection = ExecutingConnection ?? connection;
                        if (ConnectionPolicy.IsImportSession && (reply.Type == RespDataType.Array || reply.IsNull
                            || reply.TransactionStateCleared))
                            importTransactionStarted = false;
                    }
                    catch (RespireConnectionRetiredException retirement) when (core.Circuits is not null
                        && ConnectionPolicy.CanReplayRejectedCommands && !_hasCursorContinuation
                        && _client.TryRerouteCircuit(connection, deadline, out var target, out var rerouted, preferredZone: null))
                    {
                        // Retirement rejected every frame before dispatch. Release this permit
                        // as ignored; the replacement endpoint owns its own health outcome.
                        // Nothing was accepted, so the rejected frame spends no unit attempt.
                        if (unitAttemptCounted) unitAttempts--;
                        observation.Handled(retirement);
                        connection = target;
                        deadline = rerouted;
                        continue;
                    }
                    catch (RespireConnectionRetiredException retirement) when (cluster is not null
                        && ConnectionPolicy.CanRetryRetirement(cluster, routingAttempt, cancellationToken))
                    {
                        if (unitAttemptCounted) unitAttempts--;
                        // The transport rejects the complete MULTI/EXEC frame before accepting any part.
                        cluster.RecordRejection(ref discovery, connection, retirement);
                        observation.Handled(retirement);
                        discoveryPending = true;
                        connection = await cluster.GetReplacementConnectionAsync(null, slot, null, acquisition.Token, discovery, observation)
                            .ConfigureAwait(false);
                        discoveryPending = false;
                        routingAttempt++;
                        continue;
                    }
                    catch (Exception error)
                    {
                        admission.Failed(error, cancellationToken);
                        if (!retryUnit || unitAttempts == 0 || !TryGetUnitRetryDelay(error, outcome, out var delay)) throw;
                        observation.Handled(error);
                        retryDelay = delay;
                    }
                    finally { admission.Dispose(); }
                    Exception? unitResponseError = null;
                    if (retryUnit && retryDelay is null && !(reply.IsError && cluster is not null
                        && ClusterRouter.CanRecover(ResponseReader.ServerError(in reply, "MULTI/EXEC", new(connection.Host, connection.Port)), slot)))
                    {
                        // EXEC can contain both successful members and transient errors. Apply the
                        // whole frame's write evidence and every member's category before publishing any result.
                        if (TryGetUnitResponseRetryDelay(in reply, outcome, out var delay, out unitResponseError))
                        {
                            reply.Dispose();
                            observation.Handled(unitResponseError!);
                            retryDelay = delay;
                        }
                    }
                    if (retryDelay is { } wait)
                    {
                        for (;;)
                        {
                            await core.DelayUnitRetryAsync(wait, cancellationToken).ConfigureAwait(false);
                            acquisition = new CommandAcquisitionScope(cancellationToken, deadline, timeout);
                            acquisition.CheckDeadline("MULTI/EXEC", core);
                            try
                            {
                                if (cluster is not null)
                                {
                                    await cluster.WaitForRouteReplacementAsync(slot, acquisition.Token).ConfigureAwait(false);
                                    connection = await _client.AcquireConnectionAsync(slot, ref acquisition, observation).ConfigureAwait(false);
                                }
                                else
                                    connection = await _client.GetUnitRetryConnectionAsync(acquisition.Token, observation).ConfigureAwait(false);
                                unitResendPending = true;
                                routingAttempt = 0;
                                break;
                            }
                            catch (Exception error)
                            {
                                // A failed replacement sent nothing, but spends an attempt as in the immediate owner.
                                unitAttempts++;
                                if (!TryGetUnitRetryDelay(error, CommandWriteOutcome.DefinitelyUnwritten, out wait)) throw;
                                observation.Handled(error);
                                acquisition.Dispose();
                            }
                        }
                        continue;
                    }
                    var redirect = reply.IsError && cluster is not null
                        ? ResponseReader.ServerError(in reply, "MULTI/EXEC", new(connection.Host, connection.Port)) : null;
                    // EXEC result arrays can contain partial success. Only top-level routing
                    // rejections stay inside this attempt; all other replies finish the unit.
                    if (cluster is null || redirect is null || !ClusterRouter.CanRecover(redirect, slot))
                    {
                        if (retryTelemetry is { AcceptedResends: > 0 })
                            RespireTelemetry.RecordCommandRetry(_retryCategories.Label, unitResponseError is not null ? "exhausted" : "succeeded");
                        return reply;
                    }

                    reply.Dispose();
                    if (routingAttempt >= ClusterRouter.RedirectLimit)
                    {
                        // Throw within the owning round so its outcome and queued operations
                        // retain the same terminal routing rejection.
                        throw redirect;
                    }
                    switch (ConnectionPolicy.RedirectBehavior)
                    {
                        case QueuedRedirectBehavior.RequireFreshWatch:
                            // Replaying on another connection would lose WATCH and could commit stale reads.
                            cluster.LearnWatchedRoute(redirect, connection, slot);
                            throw new RespireTransactionRetryException(redirect);
                        case QueuedRedirectBehavior.Reject:
                            throw redirect; // Replaying would lose the prepared fieldsets.
                        case QueuedRedirectBehavior.Recover:
                            break;
                    }
                    if (ClusterRouter.IsRedirect(redirect)
                        && !ClusterRouter.TryParseRedirect(redirect, connection.Host, out _, out _))
                    {
                        throw redirect;
                    }

                    if (redirect.Code == RespireErrorCodes.Ask)
                    {
                        discovery?.RecordCommandFailure(redirect, discoveryPending: false, slot);
                        throw new RespireConnectionException(
                            "Redis Cluster transactions cannot follow ASK redirects during slot migration.",
                            redirect);
                    }

                    cluster.RecordRejection(ref discovery, connection, redirect);
                    if (unitAttemptCounted) unitAttempts--;
                    observation.Handled(redirect);
                    discoveryPending = true;
                    connection = await cluster.GetRedirectConnectionAsync(redirect, connection, acquisition.Token, slot, discovery, observation)
                        .ConfigureAwait(false);
                    discoveryPending = false;
                    routingAttempt++;
                }
            }
            catch (Exception error)
            {
                if (retryTelemetry is { AcceptedResends: > 0 }) RespireTelemetry.RecordCommandRetry(_retryCategories.Label, "exhausted");
                Exception failure = error;
                if (error is RespireTimeoutException discoveryTimeout && core.Sentinel is not null && connection is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    failure = RespireTimeoutException.BeforeConnectionSelection(
                        "MULTI/EXEC", timeout ?? discoveryTimeout.Timeout, discoveryTimeout);
                }
                else if (error is OperationCanceledException canceled && acquisition.HasCancellation)
                {
                    if (acquisition.IsDeadlineCancellation(canceled))
                        failure = acquisition.CreateTimeout("MULTI/EXEC", core,
                            ConnectionPolicy.ImportConnection, canceled);
                    else if (acquisition.IsCallerCancellation(canceled))
                    {
                        OperationCanceledException callerFailure = new(error.Message, error, cancellationToken);
                        failure = error is RespireCommandNotSubmittedException
                            ? new RespireCommandNotSubmittedException(callerFailure) : callerFailure;
                    }
                }
                if (ConnectionPolicy.IsImportSession)
                {
                    if (error is RespireCommandNotSubmittedException) importError = error;
                    else if (!importSubmissionAttempted)
                    {
                        if (failure is OperationCanceledException callerFailure)
                            failure = importError = new RespireCommandNotSubmittedException(callerFailure);
                        else if (failure is RespireTimeoutException)
                            importError = new RespireCommandNotSubmittedException(
                                new OperationCanceledException(failure.Message, failure, cancellationToken));
                    }
                }
                discovery?.RecordCommandFailure(failure, discoveryPending, slot, callerToken: cancellationToken);
                if (ReferenceEquals(failure, error)) throw;
                throw failure;
            }
            finally
            {
                acquisition.Dispose();
                discovery?.Finish();
            }

            bool TryGetUnitRetryDelay(Exception error, CommandWriteOutcome outcome, out TimeSpan delay)
                => core.CommandRetries!.TryGetUnitDelay(error, outcome, _retryCategories, unitAttempts,
                    deadline.IsSet ? TimeSpan.FromMilliseconds(deadline.RemainingMilliseconds) : TimeSpan.MaxValue,
                    cancellationToken, out delay);

            bool TryGetUnitResponseRetryDelay(in RespValue reply, CommandWriteOutcome outcome,
                out TimeSpan delay, out Exception? responseError)
            {
                delay = default;
                responseError = null;
                if (reply.IsError)
                {
                    responseError = ResponseReader.ServerError(in reply, "MULTI/EXEC", connection is null ? null : new(connection.Host, connection.Port));
                    return TryGetUnitRetryDelay(responseError, outcome, out delay);
                }
                if (reply.Type != RespDataType.Array) return false;
                var elements = reply.AsArray();
                for (var i = 0; i < Math.Min(elements.Length, _ops.Count); i++)
                {
                    ref readonly var element = ref elements[i];
                    if (!element.IsError) continue;
                    var error = ResponseReader.ServerError(in element, _ops[i].Operation, connection is null ? null : new(connection.Host, connection.Port));
                    responseError ??= error;
                    if (!TryGetUnitRetryDelay(error, outcome, out delay)) continue;
                    responseError = error;
                    return true;
                }
                return false;
            }
        }
    }

    // Allocate only when retries are enabled; the default commit path retains its allocation shape.
    private sealed class UnitRetryTelemetry(RespireCommandRetryCategory category)
    {
        private int _unpublished;

        internal int AcceptedResends { get; private set; }

        // Runs under the connection write gate: mutate state only. Meter listeners run in PublishAccepted.
        internal void Accepted()
        {
            // Acceptance and execution evidence differ: an accepted frame can die before writing.
            AcceptedResends++;
            Interlocked.Increment(ref _unpublished);
        }

        internal void PublishAccepted()
        {
            for (var count = Interlocked.Exchange(ref _unpublished, 0); count > 0; count--)
                RespireTelemetry.RecordCommandRetry(category, "attempted");
        }
    }

    // Only a complete unit that can be rebuilt from its retained frame is resent. WATCH state,
    // import fieldsets and server-local cursor pages cannot follow a new connection.
    private bool CanRetryUnit(ClientCore core)
        => core.CommandRetries is not null && this is RespireTransaction
            && _watchConnection is null && !ConnectionPolicy.IsImportSession && !_hasCursorContinuation
            && _ops.Count != 0;

    /// <summary>
    /// Discards an uncommitted transaction, faults its queued pendings, and releases its resources.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var error = new RespireTransactionDiscardedException();
        foreach (var operation in _ops)
        {
            operation.Fail(error);
        }

        try { await ReleaseAsync(returnWatchConnection: false).ConfigureAwait(false); }
        finally { foreach (var operation in _ops) operation.AllowErrorInspection(); }
    }

    // Only watched transactions carry pool ownership; ordinary transactions retain their existing size.
    private protected virtual DedicatedConnectionPool? WatchPool => null;

    private ValueTask ReleaseAsync(bool returnWatchConnection)
    {
        _buffer.Release();
        if (_watchConnection is null)
        {
            return ValueTask.CompletedTask;
        }

        System.Diagnostics.Debug.Assert(WatchPool is not null, "A watched connection must retain its owning pool.");
        if (returnWatchConnection)
        {
            WatchPool!.Return(_watchConnection);
            return ValueTask.CompletedTask;
        }

        // Disposing without EXEC leaves WATCH state behind. A failed send has uncertain server
        // state and may still have unread replies, so neither path is safe to reuse.
        return WatchPool!.DiscardAsync(_watchConnection);
    }

    private RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        ThrowIfCompleted();
        ConnectionPolicy.ValidateQueuedCommand(operation);
        var bufferMark = _buffer.Count;
        var clusterSlot = _clusterSlot;
        var hasClusterSlot = _hasClusterSlot;
        try
        {
            if (_client.Core.Cluster is not null && command.TryGetClusterSlot(out var slot))
            {
                ValidateClusterSlot(slot);
            }

            var writer = new RespWriter(_buffer, command.GetWriteSizeHint());
            command.Write(ref writer);
            writer.Complete();
        }
        catch
        {
            _buffer.TruncateTo(bufferMark);
            _clusterSlot = clusterSlot;
            _hasClusterSlot = hasClusterSlot;
            throw;
        }

        if (CursorCommandMetadata.IsServerCursorContinuation(in command)) _hasCursorContinuation = true;
        RetryContext?.RecordCategory(command.RetryCategory);
        _retryCategories.Add(command.RetryCategory);
        var pending = new RespirePending<T>();
        _ops.Add(ScriptingEngineInfo.IsScriptingCommand(operation)
            ? new ScriptingTxOp<T>(operation, pending, convert, ScriptingEngineInfo.ExpectedEngine(in command, operation))
            : new TxOp<T>(operation, pending, convert));
        return pending;
    }

    private void ValidateClusterSlot(int slot)
    {
        int? candidate = _hasClusterSlot ? _clusterSlot : null;
        ValidateClusterSlot(slot, ref candidate);
        ApplyClusterSlot(candidate);
    }

    private bool TryBeginClusterKeyValidation(out int? slot)
    {
        ThrowIfCompleted();
        slot = _hasClusterSlot ? _clusterSlot : null;
        return _client.Core.Cluster is not null;
    }

    private void ValidateClusterKey(in RespireKey key, ref int? candidate)
    {
        if (_client.Key(in key).TryGetClusterSlot(out var slot))
        {
            ValidateClusterSlot(slot, ref candidate);
        }
    }

    // Shared with RespireClient so WATCH and queued commands enforce the same slot contract.
    internal static void ValidateClusterSlot(int slot, ref int? candidate)
    {
        if (candidate is { } current && current != slot)
        {
            throw new InvalidOperationException(
                "Redis Cluster transactions require every key to use the same hash slot. " +
                "Use matching {...} hash tags for related keys.");
        }

        candidate = slot;
    }

    private void ApplyClusterSlot(int? slot)
    {
        if (slot is not { } value)
        {
            return;
        }

        _clusterSlot = value;
        _hasClusterSlot = true;
    }

    private RespireHashImportSession.Usage? PrepareCommit()
    {
        RespireHashImportSession.Usage? usage = null;
        try
        {
            ThrowIfCompleted();
            usage = ConnectionPolicy.EnterOperation();
            if (ConnectionPolicy.IsImportSession)
                ConnectionPolicy.PinnedConnection!.ValidateTransactionCapacity(_ops.Count, includeMulti: false);
            _completed = true;
            return usage;
        }
        catch (Exception error)
        {
            usage?.Dispose();
            ErrorObservation.FinishFinal(default, error);
            throw;
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction has already been committed or disposed.");
        }
    }

    private abstract class TxOp(string operation) : DeferredOperation(operation)
    {
        public virtual string? ExpectedEngine => null;

        public abstract Exception? Complete(RespireClient client, in RespValue element);
        public abstract void Abort();
    }

    /// <summary>Completes from a borrowed EXEC-array element; the parent reply owns the storage.</summary>
    private class TxOp<T>(
        string operation, RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : TxOp(operation)
    {
        public override bool IsCompleted => pending.IsCompleted;
        public override Exception? Complete(RespireClient client, in RespValue element)
        {
            if (element.IsError)
            {
                var error = ResponseReader.ServerError(in element, Operation);
                pending.Fail(error);
                return error;
            }

            try
            {
                pending.Succeed(convert(client, element));
                return null;
            }
            catch (Exception ex)
            {
                pending.Fail(ex);
                // Conversion failed after Redis completed successfully; not a DB error.
                return ex;
            }
        }

        public override void Fail(Exception error) => pending.Fail(error);
        public override bool ReportError() => pending.ReportError();
        public override void AllowErrorInspection() => pending.AllowErrorInspection();
        public override RespireTelemetry.ErrorObservation Observation => pending.Observation;

        public override void Abort() => pending.Abort();
    }

    // Ordinary queued operations retain their existing object size.
    private sealed class ScriptingTxOp<T>(string operation, RespirePending<T> pending,
        Func<RespireClient, RespValue, T> convert, string? engine) : TxOp<T>(operation, pending, convert)
    {
        public override string? ExpectedEngine => engine;
    }
}

/// <summary>
/// An unwatched MULTI/EXEC transaction. Redis always executes a successfully queued EXEC, so
/// commit has no result to inspect.
/// </summary>
public sealed class RespireTransaction : RespireTransactionBase
{
    internal RespireTransaction(RespireClient client)
        : base(client, watchConnection: null)
    {
    }

    internal RespireTransaction(RespireClient client, RespireHashImportSession importSession)
        : base(client, watchConnection: null, watchSlot: importSession.ClusterSlot, importSession: importSession)
    {
    }

    /// <summary>
    /// Executes the transaction. Pendings hold their results after EXEC; per-command runtime
    /// errors fault only that command's pending.
    /// </summary>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        _ = await CommitCoreAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// A MULTI/EXEC transaction using WATCH for optimistic concurrency. A false commit result means
/// a watched key changed and Redis discarded the transaction; queued pendings report aborted.
/// Cluster rejections require a fresh attempt and surface as <see cref="RespireTransactionRetryException"/>.
/// </summary>
public sealed class RespireWatchedTransaction : RespireTransactionBase
{
    internal WatchedTransactionRetryContext? HelperRetryContext { get; set; }
    private protected override WatchedTransactionRetryContext? RetryContext => HelperRetryContext;

    internal RespireWatchedTransaction(RespireClient client)
        : base(client, watchConnection: null)
    {
    }

    internal RespireWatchedTransaction(RespireClient client, RespireConnection watchConnection,
        DedicatedConnectionPool watchPool, int? watchSlot)
        : base(client, ValidateLease(watchConnection, watchPool), watchSlot)
    {
        WatchPool = watchPool;
    }

    private protected override DedicatedConnectionPool? WatchPool { get; }

    private static RespireConnection ValidateLease(RespireConnection connection, DedicatedConnectionPool pool)
    {
        // Validate before the base constructor allocates its transaction buffer.
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(pool);
        return connection;
    }

    /// <summary>
    /// Executes the transaction; returns false when a watched key changed before EXEC.
    /// </summary>
    public ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
        => CommitCoreAsync(cancellationToken);

    // A read-only retry callback still needs EXEC to validate its decision against WATCH.
    internal ValueTask<bool> CommitWithWatchValidationAsync(CancellationToken cancellationToken)
        => CommitCoreAsync(cancellationToken, validateEmptyWatch: true);
}
