using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Respire.Commands;
using Respire.Internal;
using Respire.Networking;
using Respire.Protocol;

namespace Respire;

/// <summary>
/// The Respire Redis client. Connect once, share the instance, and call commands from any
/// thread — every call pipelines onto a small set of multiplexed connections. Commands that
/// intentionally block (BLPOP-style waits) transparently run on dedicated pooled connections
/// instead, so they are fully supported.
/// </summary>
public sealed partial class RespireClient : IRespireClient
{
    private readonly ClientCore _core;
    private readonly string? _keyPrefix;
    private readonly bool _ownsCore;

    private RespireClient(ClientCore core, string? keyPrefix, bool ownsCore)
    {
        _core = core;
        _keyPrefix = keyPrefix;
        _ownsCore = ownsCore;
        Strings = new StringCommands(this);
        Keys = new KeyCommands(this);
        Hashes = new HashCommands(this);
        Lists = new ListCommands(this);
        Sets = new SetCommands(this);
        SortedSets = new SortedSetCommands(this);
        Streams = new StreamCommands(this);
        Scripts = new ScriptCommands(this);
        Server = new ServerCommands(this);
    }

    /// <summary>
    /// Connects using a connection string: "host", "host:port", or a
    /// <c>redis://[user[:password]@]host[:port][/db]</c> URI (see <see cref="RespireOptions.Parse"/>).
    /// </summary>
    public static ValueTask<RespireClient> ConnectAsync(string connectionString, CancellationToken cancellationToken = default)
        => ConnectAsync(RespireOptions.Parse(connectionString), cancellationToken);

    public static async ValueTask<RespireClient> ConnectAsync(RespireOptions options, CancellationToken cancellationToken = default)
    {
        var client = Create(options);
        try
        {
            await client._core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    /// <summary>
    /// Creates a client without connecting; the first command connects. Useful for dependency
    /// injection, where construction should not block on the network.
    /// </summary>
    public static RespireClient Create(string connectionString) => Create(RespireOptions.Parse(connectionString));

    public static RespireClient Create(RespireOptions options)
        => new(new ClientCore(options ?? throw new ArgumentNullException(nameof(options))), keyPrefix: null, ownsCore: true);

    public RespireEndpoint Endpoint => new(_core.Multiplexer.Host, _core.Multiplexer.Port);

    public bool IsConnected => _core.Multiplexer.IsConnected;

    /// <summary>Raised when a dead connection is noticed and again when its replacement lands.</summary>
    public event Action<RespireConnectionState>? ConnectionStateChanged
    {
        add => _core.Multiplexer.StateChanged += value;
        remove => _core.Multiplexer.StateChanged -= value;
    }

    public IStringCommands Strings { get; }
    public IKeyCommands Keys { get; }
    public IHashCommands Hashes { get; }
    public IListCommands Lists { get; }
    public ISetCommands Sets { get; }
    public ISortedSetCommands SortedSets { get; }
    public IStreamCommands Streams { get; }
    public IScriptCommands Scripts { get; }
    public IServerCommands Server { get; }

    /// <summary>
    /// A view of this client that prepends <paramref name="prefix"/> to every key (channels and
    /// server-level commands are untouched). Views share this client's connections; disposing a
    /// view is a no-op — dispose the root client.
    /// </summary>
    public IRespireClient WithKeyPrefix(string prefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        return new RespireClient(_core, _keyPrefix is null ? prefix : _keyPrefix + prefix, ownsCore: false);
    }

    /// <summary>Sends PING and returns the measured round-trip time. Redis: PING.</summary>
    public async ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var response = await SendAsync("PING", new RawCommand(RespCommands.Ping), cancellationToken).ConfigureAwait(false);
        response.Dispose();
        return Stopwatch.GetElapsedTime(start);
    }

    // Raw escape hatch

    /// <summary>
    /// Sends any command. The command may contain spaces ("CONFIG GET"); each arg is exactly one
    /// argument. The result is a lease — dispose it.
    /// </summary>
    public async ValueTask<RespireResult> ExecuteAsync(string command, params RespireValue[] args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokens = new RespireValue[words.Length + args.Length];
        for (var i = 0; i < words.Length; i++)
        {
            tokens[i] = words[i];
        }

        args.CopyTo(tokens, words.Length);
        var response = await SendAsync(words[0].ToUpperInvariant(), new DynamicCommand(tokens), CancellationToken.None)
            .ConfigureAwait(false);
        return new RespireResult(in response);
    }

    /// <summary>
    /// Sends a command written as an interpolated string — <c>ExecuteAsync($"SET {key} {value} EX {60}")</c>.
    /// Literal text splits on spaces; every interpolation hole is exactly one argument and is
    /// never re-tokenized, so values containing spaces are safe.
    /// </summary>
    public async ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        CancellationToken cancellationToken = default)
    {
        var (operation, tokens) = command.Build();
        var response = await SendAsync(operation, new DynamicCommand(tokens), cancellationToken).ConfigureAwait(false);
        return new RespireResult(in response);
    }

    // Pub/sub

    /// <summary>Publishes to a channel; returns the number of subscribers that received it. Redis: PUBLISH.</summary>
    public ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => IntegerAsync("PUBLISH", new Cmd2(Verbs.Publish, channel, message), cancellationToken);

    /// <summary>Publishes to a sharded channel (Redis 7+). Redis: SPUBLISH.</summary>
    public ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default)
        => IntegerAsync("SPUBLISH", new Cmd2(Verbs.SPublish, channel, message), cancellationToken);

    /// <summary>
    /// Subscribes to channels as an async stream: <c>await foreach (var msg in client.Subscribe("news"))</c>.
    /// The SUBSCRIBE is sent when enumeration starts; disposing the subscription unsubscribes.
    /// Redis: SUBSCRIBE.
    /// </summary>
    public RespireSubscription Subscribe(params string[] channels)
        => _core.Hub.CreateSubscription(SubscriptionKind.Channel, channels);

    /// <summary>Subscribes to glob patterns ("news.*"). Redis: PSUBSCRIBE.</summary>
    public RespireSubscription SubscribePattern(params string[] patterns)
        => _core.Hub.CreateSubscription(SubscriptionKind.Pattern, patterns);

    /// <summary>Subscribes to sharded channels (Redis 7+). Redis: SSUBSCRIBE.</summary>
    public RespireSubscription SubscribeSharded(params string[] channels)
        => _core.Hub.CreateSubscription(SubscriptionKind.Sharded, channels);

    // Batches and transactions

    /// <summary>
    /// Starts an explicit pipeline: queue commands, then <see cref="RespireBatch.SendAsync"/>
    /// flushes them together. Queued results are unreadable until the batch is sent — awaiting
    /// early throws instead of deadlocking.
    /// </summary>
    public RespireBatch CreateBatch() => new(this);

    /// <summary>Starts a MULTI/EXEC transaction. Queue commands, then <see cref="RespireTransaction.CommitAsync"/>.</summary>
    public RespireTransaction CreateTransaction() => new(this, watchConnection: null);

    /// <summary>
    /// Starts a transaction that WATCHes keys first: if any watched key changes before commit,
    /// <see cref="RespireTransaction.CommitAsync"/> returns false. Runs on a dedicated
    /// connection for correct WATCH isolation; always commit or dispose the transaction.
    /// For read-modify-write loops, prefer a Lua script (<see cref="Scripts"/>) — one round
    /// trip, no retry loop.
    /// </summary>
    public async ValueTask<RespireTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(watchKeys);
        if (watchKeys.Length == 0)
        {
            return CreateTransaction();
        }

        ObjectDisposedException.ThrowIf(_core.Disposed, this);

        // Rented from the tracked dedicated pool (not raw-connected) so client disposal can
        // see and abort this connection even if it opens mid-disposal.
        var connection = await _core.DedicatedPool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new Cmd1N(Verbs.Watch, Key(watchKeys[0]), MapKeys(watchKeys.AsSpan(1)));
            RespValue reply;
            if (_core.Options.CommandTimeout is { } timeout)
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                try
                {
                    reply = await connection.SendAsync(in command, timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RespireTimeoutException("WATCH", timeout);
                }
            }
            else
            {
                reply = await connection.SendAsync(in command, cancellationToken).ConfigureAwait(false);
            }

            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply);
                reply.Dispose();
                throw error;
            }

            reply.Dispose();
            return new RespireTransaction(this, connection);
        }
        catch
        {
            await _core.DedicatedPool.DiscardAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownsCore)
        {
            await _core.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Internal machinery shared by facets, batches, and transactions.

    internal ClientCore Core => _core;

    internal string? KeyPrefix => _keyPrefix;

    /// <summary>Resolves a user key to a command argument, applying this view's key prefix.</summary>
    internal RespireValue Key(in RespireKey key)
        => _keyPrefix is null ? key.AsValue() : key.Prepend(_keyPrefix).AsValue();

    internal RespireValue[] MapKeys(ReadOnlySpan<RespireKey> keys)
    {
        if (keys.Length == 0)
        {
            return [];
        }

        var mapped = new RespireValue[keys.Length];
        for (var i = 0; i < keys.Length; i++)
        {
            mapped[i] = Key(in keys[i]);
        }

        return mapped;
    }

    internal static RespireValue[] MapValues(ReadOnlySpan<RespireValue> values) => values.ToArray();

    internal RespireValue Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (typeof(T) == typeof(string))
        {
            return (string)(object)value;
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (byte[])(object)value;
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        _core.Options.Serializer.Serialize(buffer, value);
        return buffer.WrittenMemory;
    }

    /// <summary>Reads a value the caller does not own (e.g. a transaction-array element).</summary>
    internal T? DeserializeBorrowed<T>(in RespValue value)
    {
        if (value.IsNull)
        {
            return default;
        }

        if (typeof(T) == typeof(string))
        {
            return (T)(object)value.AsString();
        }

        if (typeof(T) == typeof(byte[]))
        {
            return (T)(object)value.AsSpan().ToArray();
        }

        return _core.Options.Serializer.Deserialize<T>(value.AsSpan());
    }

    /// <summary>The central send path: lazy connect, optional command timeout, telemetry, error translation.</summary>
#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<RespValue> SendAsync<TCommand>(string operation, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        await core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        using var activity = RespireTelemetry.StartActivity(operation, core.Multiplexer.Host, core.Multiplexer.Port);
        var start = RespireTelemetry.TimestampIfEnabled();
        try
        {
            RespValue response;
            if (core.Options.CommandTimeout is { } timeout)
            {
                using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutSource.CancelAfter(timeout);
                try
                {
                    response = await core.Multiplexer.SendAsync(in command, timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RespireTimeoutException(operation, timeout);
                }
            }
            else
            {
                response = await core.Multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
            }

            if (response.IsError)
            {
                var error = ResponseReader.ServerError(in response);
                response.Dispose();
                throw error;
            }

            RespireTelemetry.Record(operation, start, success: true);
            return response;
        }
        catch (Exception ex)
        {
            RespireTelemetry.Record(operation, start, success: false);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Sends an intentionally blocking command (BLPOP, blocking XREADGROUP, …) on a dedicated
    /// pooled connection so it cannot stall multiplexed traffic. No command timeout applies —
    /// blocking is the point; cancel via the token (which abandons the connection).
    /// </summary>
    internal async ValueTask<RespValue> SendBlockingAsync<TCommand>(string operation, TCommand command, CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);

        using var activity = RespireTelemetry.StartActivity(operation, core.Multiplexer.Host, core.Multiplexer.Port);
        var connection = await core.DedicatedPool.RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await connection.SendAsync(in command, cancellationToken).ConfigureAwait(false);
            core.DedicatedPool.Return(connection);
            if (response.IsError)
            {
                var error = ResponseReader.ServerError(in response);
                response.Dispose();
                throw error;
            }

            return response;
        }
        catch (Exception ex) when (ex is not RespireServerException)
        {
            // The connection may still be mid-block server-side; don't return it to the pool.
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            await core.DedicatedPool.DiscardAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    internal ValueTask<RespValue> SendTransactionCoreAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        return SendTransactionSlowAsync(serializedCommands, commandCount, cancellationToken);
    }

    private async ValueTask<RespValue> SendTransactionSlowAsync(
        ReadOnlyMemory<byte> serializedCommands, int commandCount, CancellationToken cancellationToken)
    {
        await _core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return await _core.Multiplexer.SendTransactionAsync(serializedCommands, commandCount, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<RespireConnection> AcquireConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_core.Disposed, this);
        await _core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        return _core.Multiplexer.GetConnection();
    }

    // Typed send helpers — one per reply shape, shared by every facet.

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<long> IntegerAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.Integer(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<bool> FlagAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.Flag(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<bool> OkOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.OkOrNull(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
#endif
    internal async ValueTask OkAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        try
        {
            ResponseReader.ExpectOk(in value);
        }
        finally
        {
            value.Dispose();
        }
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<string> StringAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.String(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<string?> StringOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.StringOrNull(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<byte[]?> BytesOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.BytesOrNull(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<double> DoubleAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.Double(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<double?> DoubleOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.DoubleOrNull(in value);
        value.Dispose();
        return result;
    }

#if NET
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    internal async ValueTask<long?> IntegerOrNullAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.IntegerOrNull(in value);
        value.Dispose();
        return result;
    }

    internal async ValueTask<string[]> StringArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.StringArray(in value);
        value.Dispose();
        return result;
    }

    internal async ValueTask<string?[]> NullableStringArrayAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.NullableStringArray(in value);
        value.Dispose();
        return result;
    }

    internal async ValueTask<Dictionary<string, string>> StringMapAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        var result = ResponseReader.StringMap(in value);
        value.Dispose();
        return result;
    }

    internal async ValueTask<T?> DeserializeAsync<T, TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        try
        {
            return DeserializeBorrowed<T>(in value);
        }
        finally
        {
            value.Dispose();
        }
    }

    internal async ValueTask<RespireLease> LeaseAsync<TCommand>(string operation, TCommand command, CancellationToken ct)
        where TCommand : struct, IRespCommand
    {
        var value = await SendAsync(operation, command, ct).ConfigureAwait(false);
        return new RespireLease(in value);
    }
}
