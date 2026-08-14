using System.Diagnostics.CodeAnalysis;
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
public abstract class RespireTransactionBase : IAsyncDisposable, IRespireCommandQueue, IPendingSink
{
    private readonly RespireClient _client;
    private readonly RespireConnection? _watchConnection;
    private readonly WriteBuffer _buffer = new(1024);
    private readonly List<TxOp> _ops = [];
    private int _clusterSlot;
    private bool _hasClusterSlot;
    private bool _completed;

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

    internal RespireTransactionBase(RespireClient client, RespireConnection? watchConnection)
    {
        _client = client;
        _watchConnection = watchConnection;
    }

    /// <summary>The number of commands queued for the transaction.</summary>
    public int Count => _ops.Count;

    // Deferred command facets, grouped exactly like the client's — and the same interfaces a
    // batch exposes, so helper code can queue into either. Created on first use.

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
    private protected async ValueTask<bool> CommitCoreAsync(CancellationToken cancellationToken)
    {
        ThrowIfCompleted();
        _completed = true;
        var core = _client.Core;
        var telemetry = RespireTelemetry.StartBatchOperation(
            "MULTI",
            _ops,
            static op => op.Operation,
            core.Multiplexer.Host,
            core.Multiplexer.Port,
            core.Options.Database,
            out var telemetryOperation);
        RespireConnection? connection = _watchConnection;
        Exception? operationError = null;
        var returnWatchConnection = false;
        try
        {
            if (_ops.Count == 0)
            {
                return true;
            }

            // MULTI/EXEC bypasses the regular send path and can contain arbitrary mutations.
            core.ClientCache?.FlushForUnknownCommand();

            RespValue result;
            try
            {
                // Transactions are not intentionally blocking, so CommandTimeout applies here
                // exactly as it does on the regular send path.
                if (_client.Core.Options.CommandTimeout is { } timeout)
                {
                    using var timeoutSource = CommandTimeoutCancellation.Create(
                        cancellationToken,
                        timeout);
                    try
                    {
                        result = await SendAsync(timeoutSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new RespireTimeoutException("MULTI/EXEC", timeout);
                    }
                }
                else
                {
                    result = await SendAsync(cancellationToken).ConfigureAwait(false);
                }

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
                var error = ResponseReader.ServerError(in result, "MULTI/EXEC");
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
                foreach (var op in _ops)
                {
                    op.Abort();
                }

                return false;
            }

            var elements = result.AsArray();
            var completeCount = Math.Min(_ops.Count, elements.Length);
            for (var i = 0; i < completeCount; i++)
            {
                var itemError = _ops[i].Complete(_client, in elements[i]);
                operationError ??= itemError;
            }

            if (completeCount < _ops.Count)
            {
                var mismatch = new RespireProtocolException(
                    $"EXEC returned {elements.Length} results for {_ops.Count} queued commands.");
                operationError ??= mismatch;
                for (var i = completeCount; i < _ops.Count; i++)
                {
                    _ops[i].Fail(mismatch);
                }
            }

            result.Dispose();
            return true;
        }
        finally
        {
            try
            {
                await ReleaseAsync(returnWatchConnection).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                operationError ??= ex;
                throw;
            }
            finally
            {
                telemetry.Complete(
                    core,
                    telemetryOperation,
                    error: operationError,
                    connection: connection,
                    batchSize: _ops.Count == 1 ? null : _ops.Count);
            }
        }

        async ValueTask<RespValue> SendAsync(CancellationToken token)
        {
            var cluster = core.Cluster;
            for (var attempt = 0; ; attempt++)
            {
                connection ??= await _client.AcquireConnectionAsync(
                        _hasClusterSlot ? _clusterSlot : null, token)
                    .ConfigureAwait(false);
                var reply = await connection.SendTransactionAsync(_buffer.WrittenMemory, _ops.Count, token)
                    .ConfigureAwait(false);
                if (!reply.IsError || cluster is null || attempt >= ClusterRouter.RedirectLimit)
                {
                    return reply;
                }

                var redirect = ResponseReader.ServerError(in reply, "MULTI/EXEC");
                if (!ClusterRouter.IsRedirect(redirect))
                {
                    return reply;
                }

                reply.Dispose();
                if (redirect.Code == RespireErrorCodes.Ask)
                {
                    throw new RespireConnectionException(
                        "Redis Cluster transactions cannot follow ASK redirects during slot migration.",
                        redirect);
                }

                connection = await cluster.GetRedirectConnectionAsync(redirect, connection, token)
                    .ConfigureAwait(false);
            }
        }
    }

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

        await ReleaseAsync(returnWatchConnection: false).ConfigureAwait(false);
    }

    private ValueTask ReleaseAsync(bool returnWatchConnection)
    {
        _buffer.Release();
        if (_watchConnection is null)
        {
            return ValueTask.CompletedTask;
        }

        if (returnWatchConnection)
        {
            _client.Core.DedicatedPool.Return(_watchConnection);
            return ValueTask.CompletedTask;
        }

        // Disposing without EXEC leaves WATCH state behind. A failed send has uncertain server
        // state and may still have unread replies, so neither path is safe to reuse.
        return _client.Core.DedicatedPool.DiscardAsync(_watchConnection);
    }

    private RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        ThrowIfCompleted();
        var bufferMark = _buffer.Count;
        var clusterSlot = _clusterSlot;
        var hasClusterSlot = _hasClusterSlot;
        try
        {
            if (_client.Core.Cluster is not null && command.TryGetClusterSlot(out var slot))
            {
                ValidateClusterSlot(slot);
            }

            var writer = new RespWriter(_buffer);
            command.Write(ref writer);
        }
        catch
        {
            _buffer.TruncateTo(bufferMark);
            _clusterSlot = clusterSlot;
            _hasClusterSlot = hasClusterSlot;
            throw;
        }

        var pending = new RespirePending<T>();
        _ops.Add(new TxOp<T>(operation, pending, convert));
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

    private static void ValidateClusterSlot(int slot, ref int? candidate)
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

    private void ThrowIfCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("The transaction has already been committed or disposed.");
        }
    }

    private abstract class TxOp
    {
        protected TxOp(string operation) => Operation = operation;

        public string Operation { get; }

        public abstract Exception? Complete(RespireClient client, in RespValue element);

        public abstract void Fail(Exception error);

        public abstract void Abort();
    }

    /// <summary>Completes from a borrowed EXEC-array element; the parent reply owns the storage.</summary>
    private sealed class TxOp<T>(
        string operation, RespirePending<T> pending, Func<RespireClient, RespValue, T> convert) : TxOp(operation)
    {
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

        public override void Abort() => pending.Abort();
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

    /// <summary>
    /// Executes the transaction. Pendings hold their results after EXEC; per-command runtime
    /// errors fault only that command's pending.
    /// </summary>
    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        if (!await CommitCoreAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new RespireProtocolException("An unwatched EXEC unexpectedly returned a null reply.");
        }
    }
}

/// <summary>
/// A MULTI/EXEC transaction using WATCH for optimistic concurrency. A false commit result means
/// a watched key changed and Redis discarded the transaction; queued pendings report aborted.
/// </summary>
public sealed class RespireWatchedTransaction : RespireTransactionBase
{
    internal RespireWatchedTransaction(RespireClient client, RespireConnection? watchConnection)
        : base(client, watchConnection)
    {
    }

    /// <summary>
    /// Executes the transaction; returns false when a watched key changed before EXEC.
    /// </summary>
    public ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
        => CommitCoreAsync(cancellationToken);
}
