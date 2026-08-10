namespace Respire;

/// <summary>
/// The Respire client surface — implemented by <see cref="RespireClient"/> and its key-prefixed
/// views, and the type to inject and mock.
/// </summary>
public interface IRespireClient : IAsyncDisposable
{
    /// <summary>The primary endpoint currently used by this client.</summary>
    RespireEndpoint Endpoint { get; }

    /// <summary>Whether at least one command connection is currently usable.</summary>
    bool IsConnected { get; }

    /// <summary>Raised when a command connection changes health.</summary>
    event Action<RespireConnectionStateChange>? ConnectionStateChanged;

    // Typed convenience facets, grouped by data type.
    /// <summary>String commands.</summary>
    IStringCommands Strings { get; }
    /// <summary>Generic key commands.</summary>
    IKeyCommands Keys { get; }
    /// <summary>Distributed lock commands.</summary>
    ILockCommands Locks { get; }
    /// <summary>Hash commands.</summary>
    IHashCommands Hashes { get; }
    /// <summary>List commands.</summary>
    IListCommands Lists { get; }
    /// <summary>Set commands.</summary>
    ISetCommands Sets { get; }
    /// <summary>Sorted-set commands.</summary>
    ISortedSetCommands SortedSets { get; }
    /// <summary>Stream commands.</summary>
    IStreamCommands Streams { get; }
    /// <summary>Bitmap commands.</summary>
    IBitmapCommands Bitmaps { get; }
    /// <summary>HyperLogLog commands.</summary>
    IHyperLogLogCommands HyperLogLog { get; }
    /// <summary>Geospatial commands.</summary>
    IGeoCommands Geo { get; }
    /// <summary>Lua script and function commands.</summary>
    IScriptCommands Scripts { get; }
    /// <summary>Server administration and introspection commands.</summary>
    IServerCommands Server { get; }

    // Root shortcuts for the most common operations.
    /// <summary>Gets a UTF-8 string, or null when the key is absent. Redis: GET.</summary>
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets and deserializes a value, or default when the key is absent. Redis: GET.</summary>
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value deserialized as <typeparamref name="T"/> alongside a Found flag, so a
    /// missing key is distinguishable from a stored <c>default(T)</c>. Redis: GET.
    /// </summary>
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Gets raw bytes, or null when the key is absent. Redis: GET.</summary>
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>Sets a protocol value with optional expiry and condition. Redis: SET.</summary>
    ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Serializes and sets a value with optional expiry and condition. Redis: SET.</summary>
    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes keys and returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);
    /// <summary>Deletes keys and returns how many existed. Redis: DEL.</summary>
    ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    /// <summary>Returns whether a key exists. Redis: EXISTS.</summary>
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);
    /// <summary>Increments an integer and returns the new value. Redis: INCR/INCRBY.</summary>
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    /// <summary>Decrements an integer and returns the new value. Redis: DECR/DECRBY.</summary>
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    /// <summary>Sets or clears a key's expiry under an optional condition. Redis: PEXPIRE/PERSIST.</summary>
    ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default);

    /// <summary>Measures a PING round trip.</summary>
    ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default);

    // Pub/sub.
    /// <summary>Publishes a message and returns the receiver count. Redis: PUBLISH.</summary>
    ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);
    /// <summary>Publishes a sharded message and returns the receiver count. Redis: SPUBLISH.</summary>
    ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);

    // Subscribing is always awaited: the subscription is live on the server when the task
    // completes, so a publish that follows is guaranteed to reach it.
    /// <summary>Subscribes to one channel. Redis: SUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeAsync(string channel, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to channels. Redis: SUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeAsync(params ReadOnlySpan<string> channels);

    /// <summary>Subscribes to channels with cancellation. Redis: SUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken);

    /// <summary>Subscribes to one channel pattern. Redis: PSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribePatternAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to channel patterns. Redis: PSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribePatternAsync(params ReadOnlySpan<string> patterns);

    /// <summary>Subscribes to channel patterns with cancellation. Redis: PSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<string> patterns, CancellationToken cancellationToken);

    /// <summary>Subscribes to one sharded channel. Redis: SSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeShardedAsync(string channel, CancellationToken cancellationToken = default);

    /// <summary>Subscribes to sharded channels. Redis: SSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeShardedAsync(params ReadOnlySpan<string> channels);

    /// <summary>Subscribes to sharded channels with cancellation. Redis: SSUBSCRIBE.</summary>
    ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken);

    // Batches and transactions.
    /// <summary>Creates an explicit pipeline that queues commands until execution.</summary>
    RespireBatch CreateBatch();
    /// <summary>Creates an un-watched transaction.</summary>
    RespireTransaction CreateTransaction();

    /// <summary>Creates a transaction watching keys for optimistic concurrency. Redis: WATCH.</summary>
    ValueTask<RespireTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default);

    /// <summary>Creates a transaction watching keys for optimistic concurrency. Redis: WATCH.</summary>
    ValueTask<RespireTransaction> CreateTransactionAsync(params ReadOnlySpan<RespireKey> watchKeys);

    /// <summary>Creates a transaction watching keys with cancellation. Redis: WATCH.</summary>
    ValueTask<RespireTransaction> CreateTransactionAsync(
        ReadOnlySpan<RespireKey> watchKeys, CancellationToken cancellationToken);

    /// <summary>Returns the effective Redis key after this client applies any key transformation.</summary>
    RespireKey ResolveKey(RespireKey key);

    // Raw escape hatch. Two shapes per command form: a params call for the common case, and an
    // array call that adds flags and cancellation as optional arguments — name the one you need.

    /// <summary>Sends a catalog command; each value is exactly one argument.</summary>
    ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>Sends a catalog command with optional policy flags and cancellation.</summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Sends any command; the name may contain spaces and each value is one argument.</summary>
    ValueTask<RespireResult> ExecuteAsync(string command, params RespireValue[] args);

    /// <summary>Sends any command with optional policy flags and cancellation.</summary>
    ValueTask<RespireResult> ExecuteAsync(
        string command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a command written as an interpolated string; each hole is one argument.</summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a catalog command and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>Queues a catalog command with cancellation and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken = default);

    /// <summary>Queues any command and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(string command, params RespireValue[] args);

    /// <summary>Queues any command with cancellation and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(
        string command, RespireValue[] args, CancellationToken cancellationToken = default);

    /// <summary>A view that prepends a prefix to every key; shares this client's connections.</summary>
    IRespireClient WithKeyPrefix(string prefix);
}
