using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>A connection-bound Redis 8.10 HIMPORT fieldset session.</summary>
/// <remarks>
/// Always dispose the session. Its dedicated connection is closed on disposal, dropping all
/// fieldsets. Connection loss or an uncertain send invalidates the session without replay.
/// Operations are sequential; await each operation or queue a session batch/transaction.
/// Cluster sessions accept only keys in the routing key's effective slot.
/// Disposal closes the connection without waiting for an in-flight operation; that operation fails without replay.
/// </remarks>
public sealed class RespireHashImportSession : IAsyncDisposable
{
    // Connection-local names are not routing keys. Catalog verbs default to argument zero.
    internal const string PrepareOperation = "HIMPORT PREPARE";
    internal const string SetOperation = "HIMPORT SET";
    internal const string DiscardOperation = "HIMPORT DISCARD";
    internal const string DiscardAllOperation = "HIMPORT DISCARDALL";
    private static readonly Verb PrepareVerb = new(-1, PrepareOperation);
    private static readonly Verb DiscardVerb = new(-1, DiscardOperation);
    private static readonly Verb DiscardAllVerb = new(-1, DiscardAllOperation);
    private readonly RespireClient _client;
    private readonly DedicatedConnectionPool _pool;
    private readonly RespireConnection _connection;
    private readonly ClusterRouter.StreamRouteVersion _routeVersion;
    private TaskCompletionSource? _disposal;
    private int _disposed;
    private int _busy;

    private RespireHashImportSession(RespireClient client, DedicatedConnectionPool pool,
        RespireConnection connection, int? slot, ClusterRouter.StreamRouteVersion routeVersion)
    {
        _client = client;
        _pool = pool;
        _connection = connection;
        ClusterSlot = slot;
        _routeVersion = routeVersion;
    }

    internal int? ClusterSlot { get; }
    internal RespireConnection Connection => _connection;

    internal static ValueTask<RespireHashImportSession> OpenAsync(RespireClient client,
        RespireKey? routingKey, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(client.Core.Disposed, client);
        int? slot = null;
        if (client.Core.Cluster is not null)
        {
            if (routingKey is not { } key)
                throw new InvalidOperationException("Redis Cluster hash import sessions require a routing key.");
            if (!client.Key(in key).TryGetClusterSlot(out var value))
                throw new ArgumentException("The routing key must identify a Cluster slot.", nameof(routingKey));
            slot = value;
        }
        return RentAsync(client, slot, cancellationToken);
    }

    private static async ValueTask<RespireHashImportSession> RentAsync(RespireClient client,
        int? slot, CancellationToken cancellationToken)
    {
        var core = client.Core;
        var cluster = core.Cluster;
        var pool = cluster is null
            ? await core.GetDedicatedPoolAsync(cancellationToken).ConfigureAwait(false)
            : await cluster.GetDedicatedPoolAsync(slot, cancellationToken, discovery: null).ConfigureAwait(false);
        RespireConnection connection;
        if (cluster is null)
            (pool, connection) = await core.RentDedicatedConnectionAsync(pool, cancellationToken, reuseIdle: false).ConfigureAwait(false);
        else
            (pool, connection) = await cluster.RentDedicatedConnectionAsync(pool, slot, cancellationToken,
                discovery: null, reuseIdle: false).ConfigureAwait(false);
        return new(client, pool, connection, slot, cluster?.CaptureSlotVersion(slot) ?? default);
    }

    /// <summary>Defines or replaces a connection-local fieldset. Fields retain their supplied order. Redis: HIMPORT PREPARE.</summary>
    public ValueTask<bool> PrepareAsync(RespireValue name, params ReadOnlySpan<RespireValue> fields)
        => PrepareAsync(name, fields, CancellationToken.None);

    /// <summary>Defines or replaces a connection-local fieldset with cancellation. Redis: HIMPORT PREPARE.</summary>
    public ValueTask<bool> PrepareAsync(RespireValue name, ReadOnlySpan<RespireValue> fields, CancellationToken cancellationToken)
        => SendAsync(PrepareOperation, PrepareCommand(name, fields), static value => ResponseReader.Ok(in value), cancellationToken);

    /// <summary>Creates or overwrites a hash from values in the prepared field order. Redis: HIMPORT SET.</summary>
    public ValueTask<bool> SetAsync(RespireKey key, RespireValue name, params ReadOnlySpan<RespireValue> values)
        => SetAsync(key, name, values, CancellationToken.None);

    /// <summary>Creates or overwrites a hash with cancellation. Redis: HIMPORT SET.</summary>
    public ValueTask<bool> SetAsync(RespireKey key, RespireValue name, ReadOnlySpan<RespireValue> values, CancellationToken cancellationToken)
        => SendAsync(SetOperation, SetCommand(key, name, values), static value => ResponseReader.Ok(in value), cancellationToken);

    /// <summary>Removes a fieldset; returns whether it existed. Redis: HIMPORT DISCARD.</summary>
    public ValueTask<bool> DiscardAsync(RespireValue name, CancellationToken cancellationToken = default)
        => SendAsync(DiscardOperation, DiscardCommand(name), static value => ReadDiscard(in value), cancellationToken);

    /// <summary>Removes every fieldset on this session; returns how many existed. Redis: HIMPORT DISCARDALL.</summary>
    public ValueTask<long> DiscardAllAsync(CancellationToken cancellationToken = default)
        => SendAsync(DiscardAllOperation, DiscardAllCommand(), static value => ReadDiscardAll(in value), cancellationToken);

    internal static bool ReadDiscard(in RespValue value)
    {
        var count = ReadDiscardAll(in value);
        if (count > 1) throw new RespireProtocolException("HIMPORT DISCARD must return zero or one.");
        return count == 1;
    }

    internal static long ReadDiscardAll(in RespValue value)
    {
        if (value.Type != RespDataType.Integer || value.AsInteger() < 0)
            throw new RespireProtocolException("HIMPORT discard replies must be nonnegative integers.");
        return value.AsInteger();
    }

    /// <summary>Creates a single-use pipeline on this session's connection. Only HIMPORT commands are accepted.</summary>
    public RespireBatch CreateBatch()
    {
        CheckUsable();
        return new(_client, this);
    }

    /// <summary>Creates a single-use MULTI/EXEC queue on this session's connection. Only HIMPORT commands are accepted.</summary>
    public RespireTransaction CreateTransaction()
    {
        CheckUsable();
        return new(_client, this);
    }

    internal CmdN PrepareCommand(RespireValue name, ReadOnlySpan<RespireValue> fields)
    {
        CheckUsable();
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfZero(fields.Length);
        var args = new RespireValue[fields.Length + 1];
        args[0] = name.Snapshot();
        var distinct = new HashSet<RespireValue>();
        for (var index = 0; index < fields.Length; index++)
        {
            ValidateName(fields[index]);
            args[index + 1] = fields[index].Snapshot();
            if (!distinct.Add(args[index + 1]))
                throw new ArgumentException("A field name must not appear twice in a fieldset.", nameof(fields));
        }
        return new(PrepareVerb, args);
    }

    internal CmdN SetCommand(RespireKey key, RespireValue name, ReadOnlySpan<RespireValue> values)
    {
        CheckUsable();
        ValidateName(name);
        ArgumentOutOfRangeException.ThrowIfZero(values.Length);
        var mapped = _client.Key(in key).Snapshot();
        if (ClusterSlot is { } slot && (!mapped.TryGetClusterSlot(out var candidate) || candidate != slot))
            throw new InvalidOperationException("Every hash import key must use the session's effective Cluster slot.");
        var args = new RespireValue[values.Length + 2];
        args[0] = mapped;
        args[1] = name.Snapshot();
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].IsNull) throw new ArgumentException("Import values cannot be null.", nameof(values));
            args[index + 2] = values[index].Snapshot();
        }
        return new(RespireCommands.Hash.HIMPORT_SET.Verb, args);
    }

    internal CmdN DiscardCommand(RespireValue name)
    {
        CheckUsable();
        ValidateName(name);
        return new(DiscardVerb, [name.Snapshot()]);
    }

    internal CmdN DiscardAllCommand()
    {
        CheckUsable();
        return new(DiscardAllVerb, []);
    }

    private static void ValidateName(RespireValue name)
    {
        if (name.IsNull) throw new ArgumentException("Fieldset and field names cannot be null.", nameof(name));
    }

    internal void ValidateQueuedCommand(string operation)
    {
        CheckUsable();
        if (operation is not (PrepareOperation or SetOperation or DiscardOperation or DiscardAllOperation))
            throw new NotSupportedException("Hash import session queues accept only HIMPORT commands.");
    }

    private async ValueTask<T> SendAsync<T>(string operation, CmdN command,
        Func<RespValue, T> convert, CancellationToken cancellationToken)
    {
        using var observation = RespireTelemetry.ErrorObservation.Rent(force: true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var usage = EnterOperation();
            var cache = operation == SetOperation ? _client.Core.ClientCache : null;
            var fence = cache is null ? default : cache.BeforeCommand(operation, in command);
            try
            {
                using var response = operation == SetOperation
                    ? await _client.SendMutationOnConnectionAsync(operation, _connection, command,
                        fence, cancellationToken, allowStreamingConnectionReroute: false, observation: observation).ConfigureAwait(false)
                    : await _client.SendOnConnectionAsync(operation, _connection, new ProtocolCommand<CmdN>(command),
                        cancellationToken, allowStreamingConnectionReroute: false, observation: observation).ConfigureAwait(false);
                return convert(response);
            }
            catch (Exception error)
            {
                await ExpireIfUncertainAsync(error).ConfigureAwait(false);
                throw;
            }
            finally { cache?.CompleteMutation(in fence); }
        }
        catch (Exception error)
        {
            observation.Final(error);
            throw;
        }
    }

    internal Usage EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Await the current hash import operation before starting another.");
        try { CheckUsable(); }
        catch { Volatile.Write(ref _busy, 0); throw; }
        return new(this);
    }

    private void CheckUsable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0 || _client.Core.Disposed, this);
        var current = _client.Core.Cluster is { } cluster
            ? cluster.IsDedicatedStreamRouteCurrent(ClusterSlot, _routeVersion, _connection)
            : _client.Core.IsDedicatedStreamRouteCurrent(_pool, _connection);
        if (!_connection.IsConnected || _pool.IsStopping || !current)
            throw new InvalidOperationException("The hash import session lost its connection or route. Dispose it and open a new session.");
    }

    internal ValueTask ExpireIfUncertainAsync(Exception error)
        => new QueuedConnectionPolicy(this).ExpireAsync(error);

    internal async ValueTask ExpireAsync(Exception error)
    {
        if (error is RespireServerException server)
        {
            _client.Core.Cluster?.LearnWatchedRoute(server, _connection, ClusterSlot);
        }
        try { await DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Preserve the operation failure; the pool retains cleanup failures. */ }
    }

    /// <summary>Closes the dedicated connection, discarding every connection-local fieldset. Repeated disposal is safe.</summary>
    public ValueTask DisposeAsync()
    {
        Volatile.Write(ref _disposed, 1);
        var completion = Volatile.Read(ref _disposal);
        if (completion is null)
        {
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var existing = Interlocked.CompareExchange(ref _disposal, completion, null);
            if (existing is not null) completion = existing;
            else _ = DisposeCoreAsync(completion);
        }
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            await _pool.DiscardAsync(_connection).ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }

    internal readonly struct Usage(RespireHashImportSession owner) : IDisposable
    {
        public void Dispose() => Volatile.Write(ref owner._busy, 0);
    }
}
