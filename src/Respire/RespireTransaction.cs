using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// A MULTI/EXEC transaction. Commands serialize immediately into a pooled buffer as they are
/// queued, each returning a <see cref="RespirePending{T}"/>; <see cref="CommitAsync"/> sends
/// MULTI + commands + EXEC as one atomic append so no other multiplexed command can interleave
/// into the server-side transaction, then completes every pending from EXEC's reply.
/// </summary>
/// <remarks>
/// Commands are grouped into the same facets as the client and a batch — see
/// <see cref="RespireBatch"/> for what the deferred surface leaves out.
/// Single-shot and not thread-safe: build, commit once, discard. When created with watch keys
/// (<see cref="RespireClient.CreateTransactionAsync"/>), the transaction owns a dedicated
/// connection and <see cref="CommitAsync"/> returns false if a watched key changed — always
/// commit or dispose so that connection is released.
/// </remarks>
public sealed class RespireTransaction : IAsyncDisposable, IPendingSink
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

    internal RespireTransaction(RespireClient client, RespireConnection? watchConnection)
    {
        _client = client;
        _watchConnection = watchConnection;
    }

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

    // Root shortcuts, mirroring the client's.

    /// <inheritdoc cref="IBatchStringCommands.GetStringAsync"/>
    public RespirePending<string?> GetStringAsync(RespireKey key) => Strings.GetStringAsync(key);

    /// <inheritdoc cref="IBatchStringCommands.GetAsync{T}"/>
    public RespirePending<T?> GetAsync<T>(RespireKey key) => Strings.GetAsync<T>(key);

    /// <inheritdoc cref="IBatchStringCommands.GetBytesAsync"/>
    public RespirePending<byte[]?> GetBytesAsync(RespireKey key) => Strings.GetBytesAsync(key);

    /// <inheritdoc cref="IBatchStringCommands.SetAsync(RespireKey, RespireValue, TimeSpan?, SetWhen, bool)"/>
    public RespirePending<bool> SetAsync(
        RespireKey key, RespireValue value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Strings.SetAsync(key, value, expiry, when, keepTtl);

    /// <inheritdoc cref="IBatchStringCommands.SetAsync{T}(RespireKey, T, TimeSpan?, SetWhen, bool)"/>
    public RespirePending<bool> SetAsync<T>(
        RespireKey key, T value, TimeSpan? expiry = null, SetWhen when = SetWhen.Always, bool keepTtl = false)
        => Strings.SetAsync(key, value, expiry, when, keepTtl);

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

    internal void ValidateClusterKeys(ReadOnlySpan<RespireKey> keys)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        foreach (ref readonly var key in keys)
        {
            ValidateClusterKey(in key, ref slot);
        }

        ApplyClusterSlot(slot);
    }

    internal void ValidateClusterKeys(RespireKey first, RespireKey second)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        ValidateClusterKey(in first, ref slot);
        ValidateClusterKey(in second, ref slot);
        ApplyClusterSlot(slot);
    }

    internal void ValidateClusterKeys(RespireKey first, ReadOnlySpan<RespireKey> rest)
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

        ApplyClusterSlot(slot);
    }

    internal void ValidateClusterKeys(ReadOnlySpan<(RespireKey Key, RespireValue Value)> pairs)
    {
        if (!TryBeginClusterKeyValidation(out var slot))
        {
            return;
        }

        foreach (ref readonly var pair in pairs)
        {
            ValidateClusterKey(in pair.Key, ref slot);
        }

        ApplyClusterSlot(slot);
    }

    RespirePending<T> IPendingSink.Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        => Add<TCommand, T>(operation, in command, convert);

    /// <summary>
    /// Executes the transaction. Returns true when EXEC ran (pendings hold their results;
    /// per-command runtime errors fault only that command's pending) and false when a watched
    /// key changed and the whole transaction was discarded (pendings report aborted).
    /// </summary>
    public async ValueTask<bool> CommitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfCompleted();
        if (_ops.Count == 0)
        {
            throw new InvalidOperationException("The transaction has no commands.");
        }

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
        try
        {
            RespValue result;
            try
            {
                // Transactions are not intentionally blocking, so CommandTimeout applies here
                // exactly as it does on the regular send path.
                if (_client.Core.Options.CommandTimeout is { } timeout)
                {
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutSource.CancelAfter(timeout);
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
                var error = ResponseReader.ServerError(in result);
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
                await ReleaseAsync().ConfigureAwait(false);
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

                var redirect = ResponseReader.ServerError(in reply);
                if (!ClusterRouter.IsRedirect(redirect))
                {
                    return reply;
                }

                reply.Dispose();
                if (redirect.Code == "ASK")
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

    /// <summary>Releases the buffer (and any watch connection) of an uncommitted transaction.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await ReleaseAsync().ConfigureAwait(false);
    }

    private async ValueTask ReleaseAsync()
    {
        _buffer.Release();
        if (_watchConnection is not null)
        {
            // Rented from the dedicated pool at creation; discarded (never reused) because an
            // uncommitted transaction still carries WATCH state on the connection.
            await _client.Core.DedicatedPool.DiscardAsync(_watchConnection).ConfigureAwait(false);
        }
    }

    private RespirePending<T> Add<TCommand, T>(
        string operation, in TCommand command, Func<RespireClient, RespValue, T> convert)
        where TCommand : struct, IRespCommand
    {
        ThrowIfCompleted();
        if (_client.Core.Cluster is not null && command.TryGetClusterSlot(out var slot))
        {
            ValidateClusterSlot(slot);
        }

        var writer = new RespWriter(_buffer);
        command.Write(ref writer);
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
                var error = ResponseReader.ServerError(in element);
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
                return null;
            }
        }

        public override void Fail(Exception error) => pending.Fail(error);

        public override void Abort() => pending.Abort();
    }
}
