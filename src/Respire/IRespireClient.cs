namespace Respire;

/// <summary>
/// The Respire client surface — implemented by <see cref="RespireClient"/> and its key-prefixed
/// views, and the type to inject and mock.
/// </summary>
public interface IRespireClient : IAsyncDisposable
{
    RespireEndpoint Endpoint { get; }

    bool IsConnected { get; }

    event Action<RespireConnectionStateChange>? ConnectionStateChanged;

    // Typed convenience facets, grouped by data type.
    IStringCommands Strings { get; }
    IKeyCommands Keys { get; }
    ILockCommands Locks { get; }
    IHashCommands Hashes { get; }
    IListCommands Lists { get; }
    ISetCommands Sets { get; }
    ISortedSetCommands SortedSets { get; }
    IStreamCommands Streams { get; }
    IBitmapCommands Bitmaps { get; }
    IHyperLogLogCommands HyperLogLog { get; }
    IGeoCommands Geo { get; }
    IScriptCommands Scripts { get; }
    IServerCommands Server { get; }

    // Root shortcuts for the most common operations.
    ValueTask<string?> GetStringAsync(RespireKey key, CancellationToken cancellationToken = default);
    ValueTask<T?> GetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a key's value deserialized as <typeparamref name="T"/> alongside a Found flag, so a
    /// missing key is distinguishable from a stored <c>default(T)</c>. Redis: GET.
    /// </summary>
    ValueTask<RespireGet<T>> TryGetAsync<T>(RespireKey key, CancellationToken cancellationToken = default);

    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

    ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        RespireExpiry expiry = default,
        SetWhen when = SetWhen.Always,
        CancellationToken cancellationToken = default);

    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);
    ValueTask<long> DeleteAsync(ReadOnlySpan<RespireKey> keys, CancellationToken cancellationToken);
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<bool> ExpireAsync(
        RespireKey key,
        RespireExpiry expiry,
        ExpireWhen when = ExpireWhen.Always,
        CancellationToken cancellationToken = default);
    ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default);

    // Pub/sub.
    ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);
    ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);

    // Subscribing is always awaited: the subscription is live on the server when the task
    // completes, so a publish that follows is guaranteed to reach it.
    ValueTask<RespireSubscription> SubscribeAsync(string channel, CancellationToken cancellationToken = default);
    ValueTask<RespireSubscription> SubscribeAsync(params ReadOnlySpan<string> channels);
    ValueTask<RespireSubscription> SubscribeAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken);
    ValueTask<RespireSubscription> SubscribePatternAsync(string pattern, CancellationToken cancellationToken = default);
    ValueTask<RespireSubscription> SubscribePatternAsync(params ReadOnlySpan<string> patterns);
    ValueTask<RespireSubscription> SubscribePatternAsync(
        ReadOnlySpan<string> patterns, CancellationToken cancellationToken);
    ValueTask<RespireSubscription> SubscribeShardedAsync(string channel, CancellationToken cancellationToken = default);
    ValueTask<RespireSubscription> SubscribeShardedAsync(params ReadOnlySpan<string> channels);
    ValueTask<RespireSubscription> SubscribeShardedAsync(
        ReadOnlySpan<string> channels, CancellationToken cancellationToken);

    // Batches and transactions.
    RespireBatch CreateBatch();
    RespireTransaction CreateTransaction();
    ValueTask<RespireTransaction> CreateTransactionAsync(
        RespireKey[] watchKeys, CancellationToken cancellationToken = default);
    ValueTask<RespireTransaction> CreateTransactionAsync(params ReadOnlySpan<RespireKey> watchKeys);
    ValueTask<RespireTransaction> CreateTransactionAsync(
        ReadOnlySpan<RespireKey> watchKeys, CancellationToken cancellationToken);

    /// <summary>Returns the effective Redis key after this client applies any key transformation.</summary>
    RespireKey ResolveKey(RespireKey key);

    // Escape hatch. A string converts implicitly to RespireCommand. The params shape covers the
    // common case; the array shape adds flags and cancellation as optional arguments.

    /// <summary>Sends a command; each value is exactly one argument.</summary>
    ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>Sends a command with optional policy flags and cancellation.</summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Sends a command written as an interpolated string; each hole is one argument.</summary>
    ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags = RespireCommandFlags.None,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a command and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args);

    /// <summary>Queues a command with cancellation and discards its reply.</summary>
    ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken = default);

    /// <summary>A view that prepends a prefix to every key; shares this client's connections.</summary>
    IRespireClient WithKeyPrefix(string prefix);
}
