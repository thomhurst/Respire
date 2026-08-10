namespace Respire;

/// <summary>
/// The Respire client surface — implemented by <see cref="RespireClient"/> and its key-prefixed
/// views, and the type to inject and mock.
/// </summary>
public interface IRespireClient : IAsyncDisposable
{
    RespireEndpoint Endpoint { get; }

    bool IsConnected { get; }

    event Action<RespireConnectionState>? ConnectionStateChanged;

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
    ValueTask<byte[]?> GetBytesAsync(RespireKey key, CancellationToken cancellationToken = default);

    ValueTask<bool> SetAsync(
        RespireKey key,
        RespireValue value,
        TimeSpan? expiry = null,
        SetWhen when = SetWhen.Always,
        bool keepTtl = false,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SetAsync<T>(
        RespireKey key,
        T value,
        TimeSpan? expiry = null,
        SetWhen when = SetWhen.Always,
        bool keepTtl = false,
        CancellationToken cancellationToken = default);

    ValueTask<long> DeleteAsync(params ReadOnlySpan<RespireKey> keys);
    ValueTask<bool> ExistsAsync(RespireKey key, CancellationToken cancellationToken = default);
    ValueTask<long> IncrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<long> DecrementAsync(RespireKey key, long by = 1, CancellationToken cancellationToken = default);
    ValueTask<bool> ExpireAsync(RespireKey key, TimeSpan expiry, CancellationToken cancellationToken = default);
    ValueTask<TimeSpan> PingAsync(CancellationToken cancellationToken = default);

    // Pub/sub.
    ValueTask<long> PublishAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);
    ValueTask<long> PublishShardedAsync(string channel, RespireValue message, CancellationToken cancellationToken = default);
    RespireSubscription Subscribe(params string[] channels);
    RespireSubscription SubscribePattern(params string[] patterns);
    RespireSubscription SubscribeSharded(params string[] channels);

    // Batches and transactions.
    RespireBatch CreateBatch();
    RespireTransaction CreateTransaction();
    ValueTask<RespireTransaction> CreateTransactionAsync(RespireKey[] watchKeys, CancellationToken cancellationToken = default);

    /// <summary>Returns the effective Redis key after this client applies any key transformation.</summary>
    RespireKey ResolveKey(RespireKey key) => key;

    // Raw escape hatch.
    ValueTask<RespireResult> ExecuteAsync(RespireCommand command, params RespireValue[] args);

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command, int firstArgument, params RespireValue[] args)
        => ExecuteAsync(command, [(RespireValue)firstArgument, .. args]);

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command, RespireCommandFlags flags, params RespireValue[] args)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command, RespireValue[] args, RespireCommandFlags flags)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken);

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommand command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(string command, params RespireValue[] args);

    ValueTask<RespireResult> ExecuteAsync(
        string command, int firstArgument, params RespireValue[] args)
        => ExecuteAsync(command, [(RespireValue)firstArgument, .. args]);

    ValueTask<RespireResult> ExecuteAsync(
        string command, RespireCommandFlags flags, params RespireValue[] args)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        string command, RespireValue[] args, RespireCommandFlags flags)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        string command, RespireValue[] args, CancellationToken cancellationToken)
        => throw new NotSupportedException("Raw command cancellation is not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        string command,
        RespireValue[] args,
        RespireCommandFlags flags,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command, CancellationToken cancellationToken = default);

    ValueTask<RespireResult> ExecuteAsync(
        RespireCommandInterpolatedStringHandler command,
        RespireCommandFlags flags,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Command flags are not supported by this client implementation.");

    ValueTask ExecuteFireAndForgetAsync(RespireCommand command, params RespireValue[] args)
        => throw new NotSupportedException("Fire-and-forget commands are not supported by this client implementation.");

    ValueTask ExecuteFireAndForgetAsync(
        RespireCommand command, RespireValue[] args, CancellationToken cancellationToken)
        => throw new NotSupportedException("Fire-and-forget commands are not supported by this client implementation.");

    ValueTask ExecuteFireAndForgetAsync(string command, params RespireValue[] args)
        => throw new NotSupportedException("Fire-and-forget commands are not supported by this client implementation.");

    ValueTask ExecuteFireAndForgetAsync(
        string command, RespireValue[] args, CancellationToken cancellationToken)
        => throw new NotSupportedException("Fire-and-forget commands are not supported by this client implementation.");

    /// <summary>A view that prepends a prefix to every key; shares this client's connections.</summary>
    IRespireClient WithKeyPrefix(string prefix);
}
