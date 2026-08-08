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

        var connection = core.Multiplexer.GetConnection();
        return await SendOnConnectionAsync(operation, connection, command, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RespValue> SendOnConnectionAsync<TCommand>(
        string operation,
        RespireConnection connection,
        TCommand command,
        CancellationToken cancellationToken)
        where TCommand : struct, IRespCommand
    {
        var core = _core;
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
                    response = await connection.SendAsync(in command, timeoutSource.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new RespireTimeoutException(operation, timeout);
                }
            }
            else
            {
                response = await connection.SendAsync(in command, cancellationToken).ConfigureAwait(false);
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

    // Wire-level primitives for the caching package (see InternalsVisibleTo). Keyed operations
    // honor this view's key prefix.

    internal bool RequiresReliableCorrectionOrdering(CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled || _core.Options.CommandTimeout is not null;

    /// <summary>
    /// Captures Redis client IDs before any cache command can become latent. A correction can
    /// then fence a locally dead socket server-side instead of assuming socket loss canceled
    /// bytes Redis may already have buffered.
    /// </summary>
    internal async ValueTask EnsureReliableCorrectionOrderingAsync(CancellationToken cancellationToken = default)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (core.Multiplexer.HasReliableCorrectionOrdering)
        {
            return;
        }

        if (core.Options.CommandTimeout is not { } timeout)
        {
            await core.Multiplexer.EnsureReliableCorrectionOrderingAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await core.Multiplexer.EnsureReliableCorrectionOrderingAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // No cache command is sent until identity setup completes, so timing this stage out
            // leaves no cache mutation to correct.
            throw new RespireTimeoutException("CLIENT ID / CLIENT KILL", timeout);
        }
    }

    internal readonly record struct TrackedScriptExecution(
        long ServerClientId,
        ValueTask<RespireResult> Response);

    /// <summary>
    /// Starts a cache script on a known multiplexed connection. The caller keeps the Redis
    /// client ID even when the reply wait fails, so it can establish a server-side barrier for
    /// that exact command before surfacing the failure.
    /// </summary>
    internal TrackedScriptExecution StartTrackedScriptExecution(
        RespireScript script,
        RespireKey[] keys,
        RespireValue[] args,
        CancellationToken cancellationToken)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        if (!core.Multiplexer.HasReliableCorrectionOrdering)
        {
            throw new InvalidOperationException(
                "Reliable correction ordering must be initialized before a tracked script starts.");
        }

        var connection = core.Multiplexer.GetConnection();
        var serverClientId = connection.ServerClientId;
        if (serverClientId <= 0)
        {
            throw new InvalidOperationException("Tracked cache connection has no Redis client ID.");
        }

        return new TrackedScriptExecution(
            serverClientId,
            ExecuteScriptOnConnectionAsync(connection, script, BuildScriptTail(keys, args), cancellationToken));
    }

    private async ValueTask<RespireResult> ExecuteScriptOnConnectionAsync(
        RespireConnection connection,
        RespireScript script,
        RespireValue[] tail,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await SendOnConnectionAsync(
                "EVALSHA", connection, new Cmd2N(Verbs.EvalSha, script.Sha1, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
        catch (RespireServerException ex) when (ex.Code == "NOSCRIPT")
        {
            var reply = await SendOnConnectionAsync(
                "EVAL", connection, new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]), cancellationToken)
                .ConfigureAwait(false);
            return new RespireResult(in reply);
        }
    }

    /// <summary>
    /// Kills one multiplexed Redis client through a separate control connection and waits for
    /// the server acknowledgement. The acknowledged kill is an ordering barrier: no command
    /// from the target client can execute afterward.
    /// </summary>
    internal async ValueTask FenceCorrectionConnectionAsync(long serverClientId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(serverClientId);
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);

        var control = await core.DedicatedPool.RentAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var reply = await control.SendAsync(
                new ClientKillIdCommand(serverClientId), CancellationToken.None).ConfigureAwait(false);
            if (reply.IsError)
            {
                var error = ResponseReader.ServerError(in reply);
                reply.Dispose();
                throw error;
            }

            reply.Dispose();
        }
        finally
        {
            core.DedicatedPool.Return(control);
        }

        await core.Multiplexer.RetireConnectionAsync(serverClientId).ConfigureAwait(false);
    }

    internal RespireValue[] BuildScriptTail(RespireKey[]? keys, RespireValue[]? args)
    {
        var keyCount = keys?.Length ?? 0;
        var argCount = args?.Length ?? 0;
        var tail = new RespireValue[1 + keyCount + argCount];
        tail[0] = keyCount;
        for (var i = 0; i < keyCount; i++)
        {
            tail[1 + i] = Key(in keys![i]);
        }

        for (var i = 0; i < argCount; i++)
        {
            tail[1 + keyCount + i] = args![i];
        }

        return tail;
    }

    // The removal script: delete only while this removal's lease key is still alive. The lease
    // is placed — and its reply awaited — before this script is ever sent, and it carries a
    // TTL, so the script's authority to delete expires on the server's own duration clock: a
    // latent copy (flushed to the server, then abandoned) becomes harmless no later than lease
    // expiry, with no client action required. Returns whether the delete ran; 0 means the lease
    // was gone — expired in transit under a stall — and a still-waiting caller retries with a
    // fresh lease. Plain EVAL: removals are rare enough that EVALSHA probing isn't worth a
    // NOSCRIPT fallback on the dedicated connection.
    internal static readonly RespireScript LeasedUnlinkScript = RespireScript.Create("""
        if redis.call('EXISTS', KEYS[2]) == 1 then
          redis.call('UNLINK', KEYS[1])
          redis.call('UNLINK', KEYS[2])
          return 1
        end
        return 0
        """);

    // How long a removal's lease lives: long enough that no stall a successful removal should
    // survive expires it mid-flight (an in-transit expiry costs one retry round trip), short
    // enough to bound the failure path, which may have to outwait it. Mutable so tests can
    // shrink the bound.
    internal TimeSpan RemovalLeaseTtl = TimeSpan.FromSeconds(30);

    // Added to the client-side wait for lease expiry. The server counts the TTL from the
    // script's execution, the client from the lease reply's arrival — strictly later — so the
    // only error source is clock-*rate* drift between the hosts over the lease's lifetime,
    // which this covers by orders of magnitude. No wall-clock instants are ever compared.
    private static readonly TimeSpan LeaseExpiryMargin = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Removal on a dedicated pooled connection, with the wait bounded by the caller's token
    /// and either <see cref="RespireOptions.CommandTimeout"/> or the lease TTL when no command
    /// timeout is configured. Abandoning the wait discards the dedicated connection, killing
    /// the command when it is still queued client-side — but
    /// bytes already flushed can execute server-side after the failure is reported, and a plain
    /// UNLINK landing late would delete a replacement the caller wrote in response to that
    /// failure. So removal is leased (<see cref="LeasedUnlinkScript"/>): the delete only runs
    /// while its lease key lives, and the failure path never surfaces the failure until the
    /// latent script is provably harmless — either its lease was revoked and the revocation's
    /// reply seen, or the lease's TTL has certainly expired on the server (waited out locally;
    /// both are bounded by the TTL, so a wedged server cannot hang removal indefinitely). Only
    /// then can the caller observe the failure and write a replacement, which the latent script
    /// therefore cannot delete. On the multiplexed connections no wait bound could be honored
    /// at all: abandoning a wait there leaves the command queued indefinitely, and killing a
    /// shared connection would fault every innocent in-flight command on it.
    /// </summary>
    internal async ValueTask UnlinkGuardedAsync(RespireKey key, CancellationToken cancellationToken)
    {
        var timeout = _core.Options.CommandTimeout ?? RemovalLeaseTtl;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await UnlinkLeasedAsync(key, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RespireTimeoutException("UNLINK", timeout);
        }
    }

    private async ValueTask UnlinkLeasedAsync(RespireKey key, CancellationToken cancellationToken)
    {
        while (true)
        {
            RespireKey leaseKey = "respire-rm-lease:" + Guid.NewGuid().ToString("N");
            var lease = Key(in leaseKey);

            // The lease must be on the server before the removal script is sent — its reply is
            // the proof. A failure here (including an abandoned wait) leaves nothing latent:
            // the script was never sent, and a lease-set that lands late just expires unused.
            await PlaceLeaseAsync(lease, cancellationToken).ConfigureAwait(false);
            var leaseStart = Stopwatch.GetTimestamp();

            var command = new Cmd2N(Verbs.Eval, LeasedUnlinkScript.Source, 2, [Key(in key), lease]);
            RespValue value;
            try
            {
                value = await SendBlockingAsync("UNLINK", command, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RespireServerException and not ObjectDisposedException)
            {
                // The script's execution is undecided (a server error would prove it ran, and a
                // disposed client was rejected before sending), so the failure must not surface
                // until the latent copy can no longer delete anything written after it.
                await MakeLatentRemovalHarmlessAsync(lease, leaseStart).ConfigureAwait(false);
                throw;
            }

            var ran = ResponseReader.Integer(in value);
            value.Dispose();
            if (ran == 1)
            {
                return;
            }

            // The lease expired before the script executed — a stall longer than the TTL that
            // the caller's bounds chose to sit out. The delete never ran, so retry fresh; each
            // such pass costs at least a full lease lifetime, so the loop cannot spin.
        }
    }

    private async ValueTask PlaceLeaseAsync(RespireValue lease, CancellationToken cancellationToken)
    {
        var command = new Cmd4(Verbs.Set, lease, 1, "PX", (long)RemovalLeaseTtl.TotalMilliseconds);
        await _core.Multiplexer.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var reply = await _core.Multiplexer.SendAsync(in command, cancellationToken).ConfigureAwait(false);
        if (reply.IsError)
        {
            var error = ResponseReader.ServerError(in reply);
            reply.Dispose();
            throw error;
        }

        reply.Dispose();
    }

    /// <summary>
    /// Blocks until the latent removal script provably cannot run its delete: either its lease
    /// is revoked and the revocation's reply seen, or what remains of the lease's TTL (plus
    /// <see cref="LeaseExpiryMargin"/>) has been waited out, after which the server has
    /// certainly expired it. The revocation is uncancelable (no caller token, no
    /// <see cref="RespireOptions.CommandTimeout"/> — once owed it must not be abandonable) and
    /// its wait is bounded by the lease remainder, past which expiry has done its job anyway; a
    /// timed-out revocation stays queued (multiplexed sends are never cancelled) and is
    /// observed in the background so a late fault is not unhandled.
    /// </summary>
    private async ValueTask MakeLatentRemovalHarmlessAsync(RespireValue lease, long leaseStart)
    {
        var remaining = RemovalLeaseTtl - Stopwatch.GetElapsedTime(leaseStart);
        if (remaining > TimeSpan.Zero)
        {
            var revoke = RevokeLeaseAsync(new Cmd1(Verbs.Unlink, lease));
            try
            {
                await revoke.WaitAsync(remaining).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
                _ = ObserveRevokeAsync(revoke);
            }
            catch (Exception ex) when (ex is RespireException or ObjectDisposedException)
            {
                // The revocation could not be sent or died with its connection. Swallowed — it
                // must not mask the caller's real failure — and expiry below still bounds the
                // latent script's authority.
            }
        }

        var wait = RemovalLeaseTtl + LeaseExpiryMargin - Stopwatch.GetElapsedTime(leaseStart);
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait).ConfigureAwait(false);
        }
    }

    private async Task RevokeLeaseAsync(Cmd1 command)
    {
        await _core.Multiplexer.EnsureConnectedAsync(CancellationToken.None).ConfigureAwait(false);
        var reply = await _core.Multiplexer.SendAsync(in command, CancellationToken.None).ConfigureAwait(false);
        if (reply.IsError)
        {
            var error = ResponseReader.ServerError(in reply);
            reply.Dispose();
            throw error;
        }

        reply.Dispose();
    }

    private static async Task ObserveRevokeAsync(Task revoke)
    {
        try
        {
            await revoke.ConfigureAwait(false);
        }
        catch
        {
            // An abandoned revocation that fails means its connection died and cannot deliver
            // more bytes; lease expiry decides the race either way.
        }
    }

    /// <summary>
    /// Executes a script on every connection via
    /// <see cref="Infrastructure.RespireConnectionMultiplexer.SendToAllConnectionsAsync{TCommand}"/> — the copy
    /// sharing a connection with an earlier still-buffered command executes after it, so the
    /// script must be idempotent and safe out of order elsewhere. Locally dead connections are
    /// fenced by Redis client ID before a retry can complete. Plain EVAL (no EVALSHA
    /// probing: the callers are rare correction paths) and no caller token or command timeout —
    /// a correction, once owed, must not be abandonable.
    /// </summary>
    internal async ValueTask ExecuteOnAllConnectionsAsync(RespireScript script, RespireKey[] keys, RespireValue[] args)
    {
        var core = _core;
        ObjectDisposedException.ThrowIf(core.Disposed, this);
        var tail = new RespireValue[1 + keys.Length + args.Length];
        tail[0] = keys.Length;
        for (var i = 0; i < keys.Length; i++)
        {
            tail[1 + i] = Key(in keys[i]);
        }

        args.CopyTo(tail, 1 + keys.Length);
        await core.Multiplexer.SendToAllConnectionsAsync(
            new Cmd2N(Verbs.Eval, script.Source, tail[0], tail[1..]), CancellationToken.None).ConfigureAwait(false);
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
